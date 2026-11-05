"""OCR 引擎评测：Windows 自带 OCR vs RapidOCR。

为什么需要客观评测：
    之前只靠肉眼看「识别出来的字好像对了一些」，无法判断哪个方案真的更好。
    Windows OCR 在 UI 截图上有两个毛病：字与字之间插空格、形近字认错
    （「预研」→「予贞石开」）。这类退化肉眼看很模糊，必须用标准答案量化。

做法：
    从截图里人工确认若干**确实出现在图上**的词组作为 ground truth，
    各方案跑完后统一去掉空白再算命中率。去掉空白是因为 Windows OCR 会把
    「点点」输出成「点 点」，不处理的话命中率会被无谓拉低。

用法：
    python tests/test_ocr_quality.py
    python tests/test_ocr_quality.py --img ../evidence/xxx.jpg
"""
from __future__ import annotations

import argparse
import base64
import io
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from app import ocr                                        # noqa: E402

# 从 evidence/selfcheck-capture-1931.jpg 上人工确认存在的文本。
# 这些词在图上是清晰可读的，OCR 认不出来就是引擎的问题，不是图的问题。
GROUND_TRUTH = [
    "点点", "Windows", "预研", "集成", "spike",
    "帮助", "关于", "运行中",
]


def squash(s: str) -> str:
    """去掉所有空白。Windows OCR 会给每个汉字之间插空格。"""
    return "".join(ch for ch in s if not ch.isspace())


def score(text: str) -> tuple[int, list[str]]:
    flat = squash(text)
    hits = [w for w in GROUND_TRUTH if squash(w) in flat]
    return len(hits), hits


def to_b64(img) -> str:
    buf = io.BytesIO()
    img.save(buf, "PNG")
    return base64.b64encode(buf.getvalue()).decode()


def variants(img):
    """返回 (名称, PIL 图) 列表。不依赖 numpy。"""
    from PIL import Image, ImageEnhance, ImageOps

    g = img.convert("L")
    yield "原图", img
    yield "灰度反色", ImageOps.invert(g).convert("RGB")
    yield "反色+对比度2x", ImageEnhance.Contrast(
        ImageOps.invert(g)).enhance(2.0).convert("RGB")
    yield "对比度2x", ImageEnhance.Contrast(img).enhance(2.0)


def run_windows(img, tag: str) -> str:
    t0 = time.time()
    text = ocr.image_to_text(to_b64(img))
    dt = (time.time() - t0) * 1000
    n, hits = score(text)
    print(f"  Windows OCR / {tag:<14} {dt:>6.0f}ms  {len(text):>5}字符  "
          f"命中 {n}/{len(GROUND_TRUTH)}")
    return text


def run_rapid(img, tag: str) -> str | None:
    try:
        from rapidocr_onnxruntime import RapidOCR
    except Exception:                                      # noqa: BLE001
        return None
    global _RAPID
    try:
        eng = _RAPID
    except NameError:
        eng = _RAPID = RapidOCR()
    import numpy as np

    t0 = time.time()
    res, _ = eng(np.asarray(img.convert("RGB")))
    dt = (time.time() - t0) * 1000
    lines = []
    for item in (res or []):
        # item = [box, text, score]
        if len(item) >= 2 and item[1]:
            lines.append(str(item[1]))
    text = "\n".join(lines)
    n, hits = score(text)
    print(f"  RapidOCR    / {tag:<14} {dt:>6.0f}ms  {len(text):>5}字符  "
          f"命中 {n}/{len(GROUND_TRUTH)}")
    return text


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--img", default=os.path.join(
        os.path.dirname(os.path.dirname(HERE)), "evidence",
        "selfcheck-capture-1931.jpg"))
    ap.add_argument("--show", action="store_true", help="打印识别全文")
    args = ap.parse_args()

    from PIL import Image

    img = Image.open(args.img).convert("RGB")
    print("=" * 78)
    print(f"OCR 质量评测　{os.path.basename(args.img)}　{img.size[0]}x{img.size[1]}")
    print(f"标准答案（{len(GROUND_TRUTH)} 个）：{'、'.join(GROUND_TRUTH)}")
    print("=" * 78)

    have_rapid = True
    try:
        import rapidocr_onnxruntime  # noqa: F401
    except Exception:                                      # noqa: BLE001
        have_rapid = False

    print("\n[Windows 自带 OCR]")
    win_texts = {}
    for tag, im in variants(img):
        win_texts[tag] = run_windows(im, tag)

    if have_rapid:
        print("\n[RapidOCR]")
        rapid_texts = {}
        for tag, im in variants(img):
            t = run_rapid(im, tag)
            if t is not None:
                rapid_texts[tag] = t
    else:
        print("\n[RapidOCR] 未安装，跳过。pip install rapidocr-onnxruntime")
        rapid_texts = {}

    if args.show:
        for name, texts in (("Windows", win_texts), ("RapidOCR", rapid_texts)):
            for tag, t in texts.items():
                print(f"\n----- {name} / {tag} -----")
                print(t[:900])

    # 结论
    print("\n" + "=" * 78)
    best_w = max(win_texts.items(), key=lambda kv: score(kv[1])[0])
    print(f"Windows OCR 最佳：{best_w[0]}　命中 {score(best_w[1])[0]}/{len(GROUND_TRUTH)}")
    if rapid_texts:
        best_r = max(rapid_texts.items(), key=lambda kv: score(kv[1])[0])
        print(f"RapidOCR   最佳：{best_r[0]}　命中 {score(best_r[1])[0]}/{len(GROUND_TRUTH)}")
        dw = score(best_w[1])[0]
        dr = score(best_r[1])[0]
        if dr > dw:
            print(f"\n→ RapidOCR 更好（+{dr - dw} 个命中），建议切到 RapidOCR。")
        elif dr == dw:
            print("\n→ 两者命中数相同，优先 RapidOCR（字形错误更少）。")
        else:
            print("\n→ Windows OCR 命中更多，保留现状。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
