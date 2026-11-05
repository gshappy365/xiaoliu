"""PaddleOCR-VL 回归测试。

锁住四个**已经踩过、代价很高**的坑，任何一个复发都会让 OCR 静默退化成
Windows OCR（结果还是形近字全错，但日志看起来一切正常）：

  1. 中文路径 —— SentencePiece / Paddle Inference 的 C++ 层打不开非 ASCII
     路径，Python 的 os.path.exists() 却返回 True。必须**明确报不可用**，
     不能装作能跑然后以 `TypeError: cannot unpack non-iterable bool` 崩掉。
  2. 静态图冲突 —— warmup 后台线程会让 paddle 进 static graph 模式，
     之后 predict 抛 `int(Tensor) is not supported in static graph mode`，
     被上层当成「没识别出内容」静默回退。这里断言预热后仍能正常识别。
  3. 调度层是否真的走 paddle —— 光看「有文字返回」不够，Windows OCR 也给
     文字。要断言输出里没有 `[y=` 这类 Windows OCR 的行坐标标记。
  4. 形近字质量 —— 与 Windows OCR 的对照基线，防止某次依赖升级把模型换掉
     后质量悄悄退化。

用法：python tests/test_paddle_ocr.py
      python tests/test_paddle_ocr.py --no-gpu   # 只跑静态检查，不加载模型
"""
from __future__ import annotations

import base64
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from app import paddle_ocr                              # noqa: E402

IMG = os.path.join(os.path.dirname(os.path.dirname(HERE)),
                   "evidence", "selfcheck-capture-1931.jpg")

# 这张截图上的真实文字。用来判断「识别得对不对」，不只是「有没有识别」。
GROUND_TRUTH = ["点点", "Windows", "预研", "集成", "spike"]

fails = 0


def check(name: str, cond: bool, detail: str = "") -> None:
    global fails
    mark = "PASS" if cond else "FAIL"
    if not cond:
        fails += 1
    print(f"  [{mark}] {name}" + (f"  {detail}" if detail else ""))


def squash(text: str) -> str:
    """去掉所有空白。Windows OCR 会给每个汉字之间插空格。"""
    return "".join(text.split())


# ------------------------------------------------------------------ 静态检查

def test_path_guard() -> None:
    """坑 1：非 ASCII 路径必须被判为不可用，且给出可读原因。"""
    print("\n[1] 中文路径防护")
    check("_ascii_only 认 ASCII",
          paddle_ocr._ascii_only(r"D:\gao\wbai\rea\models\paddlex") is True)
    check("_ascii_only 认中文",
          paddle_ocr._ascii_only(r"C:\Users\李平\.paddlex") is False)

    # 真实场景：把模型目录指向中文路径，available() 必须返回 False 并说明原因
    orig = os.environ.get("PADDLE_OCR_MODEL_DIR")
    saved_state, saved_reason = paddle_ocr._state, paddle_ocr._reason
    try:
        # 目录得真实存在，否则会先撞上「目录不存在」这条分支，测不到路径判断
        fake = os.path.join(HERE, "中文目录测试")
        os.makedirs(fake, exist_ok=True)
        os.environ["PADDLE_OCR_MODEL_DIR"] = fake
        paddle_ocr._state, paddle_ocr._reason = None, ""
        ok = paddle_ocr.available()
        check("中文路径 → 不可用", ok is False)
        check("原因指明非 ASCII", "非 ASCII" in paddle_ocr._reason,
              paddle_ocr._reason)
    finally:
        if orig is None:
            os.environ.pop("PADDLE_OCR_MODEL_DIR", None)
        else:
            os.environ["PADDLE_OCR_MODEL_DIR"] = orig
        paddle_ocr._state, paddle_ocr._reason = saved_state, saved_reason
        try:
            os.rmdir(os.path.join(HERE, "中文目录测试"))
        except OSError:
            pass


def test_model_dir_exists() -> None:
    """默认模型目录必须在纯 ASCII 路径下且真实存在。"""
    print("\n[2] 模型目录")
    d = paddle_ocr.model_dir()
    print(f"     {d}")
    check("目录存在", os.path.isdir(d))
    check("路径纯 ASCII", paddle_ocr._ascii_only(os.path.abspath(d)))
    # 权重文件存在说明模型是完整下载的，不是空壳目录
    has_cfg = any(f.endswith(".json") for f in os.listdir(d)) if os.path.isdir(d) else False
    check("含配置文件", has_cfg)


# ------------------------------------------------------------------ 运行时

def test_runtime() -> None:
    """坑 2/3/4：worker 能起来、走的是 paddle、质量优于 Windows OCR。"""
    print("\n[3] 运行时识别")
    if not paddle_ocr.available():
        print("     SKIP（paddleocr 不可用：%s）" % paddle_ocr.status()["reason"])
        return
    if not os.path.isfile(IMG):
        print(f"     SKIP（测试图片不存在：{IMG}）")
        return

    # 坑 2 的前置条件：**先**在后台线程拉起 worker（含 dummy 预热），
    # 制造和线上一样的时序
    t0 = time.time()
    paddle_ocr.warmup()
    for _ in range(120):
        if paddle_ocr._proc is not None and paddle_ocr._proc.poll() is None:
            break
        time.sleep(0.5)
    check("worker 已启动", paddle_ocr._proc is not None,
          f"{time.time() - t0:.1f}s")
    if paddle_ocr._proc is None:
        return

    with open(IMG, "rb") as f:
        b64 = base64.b64encode(f.read()).decode()

    t1 = time.time()
    text = paddle_ocr.image_to_text(b64, "image/jpeg")
    dt = time.time() - t1
    print(f"     {len(text)} 字，{dt:.2f}s")
    if text:
        print("     前 120 字：" + text[:120].replace("\n", " / "))

    check("识别出内容", len(text) > 40, f"{len(text)} 字")
    # 预热后仍能识别 = 静态图冲突没复发（复发时 text 会是空串）
    check("预热后无静态图报错", len(text) > 0)
    # 卡死时会被降级并返回空串，这里必须确认没降级
    check("没有降级到 Windows OCR", not paddle_ocr._poisoned,
          paddle_ocr._poison_reason)

    # 坑 3：调度层真的走了 paddle，而不是静默回退到 Windows OCR
    check("非 Windows OCR 输出", "[y=" not in text and "[x=" not in text)

    # 坑 4：质量基线
    sq = squash(text)
    hits = [w for w in GROUND_TRUTH if squash(w) in sq]
    print(f"     命中真实文字 {len(hits)}/{len(GROUND_TRUTH)}：{hits}")
    check("命中 ≥4 项真实文字", len(hits) >= 4, f"{hits}")


def test_speed() -> None:
    """速度基线：RTX 3060 上应显著快于 SenseNova VL（21-48s）。"""
    print("\n[4] 速度基线")
    if not paddle_ocr.available() or not os.path.isfile(IMG):
        print("     SKIP")
        return
    with open(IMG, "rb") as f:
        b64 = base64.b64encode(f.read()).decode()
    t0 = time.time()
    paddle_ocr.image_to_text(b64, "image/jpeg")
    dt = time.time() - t0
    check("单张 < 20s", dt < 20, f"{dt:.2f}s")
    check("快于 SenseNova VL 下限 21s", dt < 21, f"{dt:.2f}s")


def test_dispatch() -> None:
    """坑 4：调度层必须真的走 paddle，而不是静默回退 Windows OCR。

    这是最隐蔽的失败模式：配了 GPU 模型、日志写着「回退 Windows OCR」、
    用户拿到的还是形近字全错的结果，但一切看起来"正常"。
    """
    print("\n[5] 调度层")
    from app import ocr
    from app.config import settings

    check("OCR_BACKEND 不是强制 windows", settings.ocr_backend != "windows",
          settings.ocr_backend)
    check("ocr.available()", ocr.available())
    if not os.path.isfile(IMG):
        print("     SKIP（测试图片不存在）")
        return
    with open(IMG, "rb") as f:
        b64 = base64.b64encode(f.read()).decode()
    t0 = time.time()
    text = ocr.image_to_text(b64, "image/jpeg")
    dt = time.time() - t0
    print(f"     {len(text)} 字，{dt:.2f}s")
    # Windows OCR 的输出每行都带 `[y=123]` 前缀；paddle 的 markdown 没有
    check("走的是 paddle 不是 Windows OCR",
          "[y=" not in text and "[x=" not in text)
    sq = squash(text)
    hits = [w for w in GROUND_TRUTH if squash(w) in sq]
    check("调度层结果同样命中真实文字", len(hits) >= 4, f"{hits}")


def main() -> int:
    print("=" * 62)
    print("PaddleOCR-VL 回归测试")
    print("=" * 62)
    test_path_guard()
    test_model_dir_exists()
    if "--no-gpu" in sys.argv:
        print("\n（--no-gpu：跳过模型加载）")
    else:
        test_runtime()
        test_speed()
        test_dispatch()
    print("\n" + "=" * 62)
    print("ALL PASS" if fails == 0 else f"{fails} 项失败")
    return 1 if fails else 0


if __name__ == "__main__":
    raise SystemExit(main())
