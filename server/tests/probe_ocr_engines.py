"""OCR 引擎横评：PP-OCRv6 / PP-OCRv5 / PaddleOCR-VL 在同一张截图上对比。

为什么单独写一个：
    `test_ocr_quality.py` 比的是 Windows OCR 和 RapidOCR，都是「字级」方案。
    现在要回答的是另一个问题：**屏幕截图这件事，到底该用哪一档模型** ——
    是继续用 0.9B 的文档解析 VLM（6 秒），还是退回传统「检测+识别」管线
    （理论上 0.1-0.3 秒）。这需要把两类引擎放进同一个评分口径里。

评分口径沿用 `test_ocr_quality.py`：从截图里人工确认确实存在的词组当标准答案，
去掉空白后算命中数。**不要**用字符相似度 —— Windows OCR 那种「字之间插空格」
会被无谓扣分，而形近字错误（「预研」→「予贞石开」）反而看不出来。

用法（每个引擎单独起进程，避免 paddle 全局状态互相污染）：

    python tests/probe_ocr_engines.py --engine v6_medium --img ../evidence/xxx.jpg
    python tests/probe_ocr_engines.py --engine v6_small  --img ../evidence/xxx.jpg
    python tests/probe_ocr_engines.py --engine v5_server --img ../evidence/xxx.jpg

输出一行 JSON，便于外面用脚本汇总。
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))

# paddlex 默认缓存目录是 C:\Users\<用户名>\.paddlex，这台机器用户名是中文，
# C++ 层打不开非 ASCII 路径（见 app/paddle_ocr.py 的「踩过的坑」）。
os.environ.setdefault("PADDLE_PDX_CACHE_HOME", os.path.join(ROOT, "models", "paddlex"))

# 与 test_ocr_quality.py 保持一致
GROUND_TRUTH = [
    "点点", "Windows", "预研", "集成", "spike",
    "帮助", "关于", "运行中",
]

ENGINES = {
    # 传统「检测 + 识别」管线：快，但只给文字行，没有版面结构
    "v6_tiny": ("PP-OCRv6_tiny_det", "PP-OCRv6_tiny_rec"),
    "v6_small": ("PP-OCRv6_small_det", "PP-OCRv6_small_rec"),
    "v6_medium": ("PP-OCRv6_medium_det", "PP-OCRv6_medium_rec"),
    "v5_server": ("PP-OCRv5_server_det", "PP-OCRv5_server_rec"),
    "v5_mobile": ("PP-OCRv5_mobile_det", "PP-OCRv5_mobile_rec"),
}


def squash(s: str) -> str:
    return "".join(ch for ch in s if not ch.isspace())


def score(text: str) -> tuple[int, list[str]]:
    flat = squash(text)
    hits = [w for w in GROUND_TRUTH if squash(w) in flat]
    return len(hits), hits


def build(engine: str, device: str):
    from paddleocr import PaddleOCR

    det, rec = ENGINES[engine]
    t0 = time.time()
    pipe = PaddleOCR(
        text_detection_model_name=det,
        text_recognition_model_name=rec,
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_textline_orientation=False,
        device=device,
    )
    return pipe, time.time() - t0


def to_text(out) -> str:
    """把 predict 的结果拼成纯文本（每行一条）。"""
    lines: list[str] = []
    for r in out or []:
        d = getattr(r, "json", None)
        payload = d.get("res", d) if isinstance(d, dict) else {}
        texts = payload.get("rec_texts") or []
        lines.extend(str(t) for t in texts if t)
    return "\n".join(lines)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--engine", required=True, choices=sorted(ENGINES))
    ap.add_argument("--img", required=True)
    ap.add_argument("--runs", type=int, default=3)
    ap.add_argument("--device", default="gpu")
    args = ap.parse_args()

    import numpy as np
    from PIL import Image

    img = Image.open(args.img).convert("RGB")
    arr = np.asarray(img)

    pipe, load_s = build(args.engine, args.device)

    # 冷启动第一次：paddle 要在这一步选 kernel，单独计时，不计入均值
    t0 = time.time()
    to_text(pipe.predict(arr))
    first_ms = (time.time() - t0) * 1000

    times = []
    text = ""
    for _ in range(args.runs):
        t0 = time.time()
        text = to_text(pipe.predict(arr))
        times.append((time.time() - t0) * 1000)

    n, hits = score(text)
    print(json.dumps({
        "engine": args.engine,
        "img": os.path.basename(args.img),
        "size": f"{img.width}x{img.height}",
        "load_s": round(load_s, 2),
        "first_ms": round(first_ms),
        "warm_ms": [round(t) for t in times],
        "warm_avg_ms": round(sum(times) / len(times)),
        "chars": len(text),
        "hits": f"{n}/{len(GROUND_TRUTH)}",
        "missed": [w for w in GROUND_TRUTH if w not in hits],
        "text": text,
    }, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
