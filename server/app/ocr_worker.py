"""PaddleOCR-VL 独立工作进程。

## 为什么必须是独立进程

同一个模型、同一张图、同一份代码：

    独立脚本里跑      7.4 秒，353 字，稳定
    服务进程里跑      30 秒不返回（GPU 利用率 5%、SM 时钟 210 MHz）

服务进程里多了 uvicorn 事件循环、sherpa-onnx（ASR）、comtypes/SAPI（TTS）
这些会初始化 CUDA / COM / OpenMP 的东西。实测把它们和 paddle 放进同一个
进程后，`pipe.predict` 会在某个同步点上卡住，GPU 明明空闲却就是不结束。

与其去猜是哪一家的全局状态踩到了 paddle，不如把 OCR 隔到独立进程里：
它跑的是被验证过稳定的那条路径，而且**卡死了可以直接 kill 掉重启** ——
这在同一个进程内是做不到的（卡住的线程攥着 GPU 上下文，杀不掉）。

## 协议

stdin/stdout 按行收发 JSON：

    请求  {"b64": "<base64 图片>", "media_type": "image/jpeg"}
    响应  {"text": "..."}     或   {"error": "..."}

约定：**每个请求都必须有且只有一行响应**，否则主进程会一直等。

## 单独调试

    python server/app/ocr_worker.py --selftest ../evidence/xxx.jpg
"""
from __future__ import annotations

import base64
import io
import json
import logging
import os
import sys
import time

log = logging.getLogger("xiaoliu.ocr-worker")

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_MODEL_DIR = os.path.join(
    os.path.dirname(os.path.dirname(HERE)),
    "models", "paddlex", "official_models", "PaddleOCR-VL-1.6")

_pipeline = None
_load_ms = 0.0


def model_dir() -> str:
    return os.environ.get("PADDLE_OCR_MODEL_DIR", "").strip() or DEFAULT_MODEL_DIR


def device() -> str:
    return os.environ.get("PADDLE_OCR_DEVICE", "").strip() or "gpu"


def max_new_tokens() -> int:
    try:
        return int(os.environ.get("PADDLE_OCR_MAX_TOKENS", "") or 2048)
    except ValueError:
        return 2048


def _get():
    global _pipeline, _load_ms
    if _pipeline is not None:
        return _pipeline
    from paddleocr import PaddleOCRVL

    t0 = time.time()
    _pipeline = PaddleOCRVL(
        vl_rec_model_dir=os.path.abspath(model_dir()),
        # 三个文档扫描件专用开关。UI 截图不是文档：方向永远正、没有透视畸变，
        # 版面分析反而会把界面切成互不相关的块。关掉还省一次模型加载。
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_layout_detection=False,
        device=device(),
    )
    _load_ms = (time.time() - t0) * 1000
    log.info("模型加载完成 %.0fms（device=%s）", _load_ms, device())
    return _pipeline


def _to_image(b64: str):
    from PIL import Image

    raw = base64.b64decode(b64, validate=False)
    return Image.open(io.BytesIO(raw)).convert("RGB")


def _boot() -> None:
    """加载模型 + 跑一次 dummy 推理。

    为什么一定要跑这一次 dummy：实测在同一个进程里

        直接跑真实截图          → 卡住不返回
        先跑一次纯白 1280x720   → 之后真实截图 7.4 秒稳定返回

    冷启动的第一次 predict 要编译/选择 CUDA kernel，这一步最容易卡在
    某个同步点上。用一张**尺寸相同、内容为空**的图先把它走一遍：
    kernel 按 shape 编译，尺寸一致就命中同一批；内容为空所以模型立刻停下，
    不会真去识别什么（实测 2.7 秒）。
    """
    global _load_ms
    import numpy as np

    pipe = _get()
    blank = np.full((720, 1280, 3), 255, dtype=np.uint8)
    t0 = time.time()
    pipe.predict(blank, max_new_tokens=64)
    log.info("dummy 预热 %.0fms", (time.time() - t0) * 1000)
    _ = pipe


def run(b64: str, media_type: str = "image/jpeg") -> str:
    """base64 → 文字。"""
    import numpy as np
    import paddle

    img = _to_image(b64)
    pipe = _get()
    # 坑：warmup 在后台线程加载模型时，paddle 的全局图模式可能被切成 static，
    # 之后 predict 报 "int(Tensor) is not supported in static graph mode"，
    # 而这个异常会被上层当成「没识别出内容」静默回退 Windows OCR。
    paddle.disable_static()

    t0 = time.time()
    out = pipe.predict(np.asarray(img), max_new_tokens=max_new_tokens())
    dt = (time.time() - t0) * 1000

    parts: list[str] = []
    for r in out:
        md = getattr(r, "markdown", None)
        if isinstance(md, dict):
            parts.append(md.get("markdown_texts") or "")
        elif md:
            parts.append(str(md))
    text = "\n".join(p for p in parts if p).strip()
    # markdown 输出带一堆空行分段，压成单空行省 token
    lines = [ln.rstrip() for ln in text.splitlines()]
    text = "\n".join(ln for ln in lines if ln.strip())
    log.info("%dx%d → %d 字，%.0fms", img.width, img.height, len(text), dt)
    return text


def main() -> int:
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(name)s %(message)s",
        stream=sys.stderr,           # 日志走 stderr，stdout 只留给协议
    )

    # --selftest：不接管道，直接跑一张图，方便单独调试
    if len(sys.argv) >= 3 and sys.argv[1] == "--selftest":
        with open(sys.argv[2], "rb") as f:
            b64 = base64.b64encode(f.read()).decode()
        _boot()
        t = time.time()
        text = run(b64)
        print(f"RESULT {time.time() - t:.2f}s {len(text)} 字")
        print(text[:400])
        return 0

    # 先把模型加载 + dummy 预热做掉，主进程第一次请求就不用等冷启动，
    # 也不会撞上最容易卡的那一次 predict。预热失败不致命：真正的请求
    # 进来时还会再试一次。
    try:
        _boot()
    except Exception as exc:                             # noqa: BLE001
        log.warning("预热失败：%s", exc)

    print(json.dumps({"ready": True, "load_ms": round(_load_ms)}), flush=True)

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except json.JSONDecodeError:
            print(json.dumps({"error": "bad json"}), flush=True)
            continue
        if req.get("op") == "ping":
            print(json.dumps({"pong": True}), flush=True)
            continue
        if req.get("op") == "shutdown":
            return 0
        try:
            text = run(req.get("b64") or "", req.get("media_type") or "image/jpeg")
            print(json.dumps({"text": text}, ensure_ascii=False), flush=True)
        except Exception as exc:                         # noqa: BLE001
            log.warning("识别失败：%s", exc)
            print(json.dumps({"error": str(exc)}, ensure_ascii=False), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
