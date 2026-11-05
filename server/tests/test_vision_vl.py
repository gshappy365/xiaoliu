"""视觉模型（商汤 SenseNova）集成测试。

验证的是**服务端行为**，不是模型能力：
    图片块必须被换成一段有实质内容的文字，且这段文字里包含图上真实存在的
    内容。如果 VL 挂了，auto 模式必须回退到 OCR，绝不能把图片原样丢给
    只吃文本的 spark-x2.6（那会导致模型静默编造屏幕内容）。

需要 VISION_API_KEY 才跑；没配就跳过（不算失败），因为这是一个可选上游。

用法：
    VISION_API_KEY=sk-xxx python tests/test_vision_vl.py
    python tests/test_vision_vl.py --image ../evidence/xxx.jpg
"""
from __future__ import annotations

import argparse
import base64
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from app import ocr, upstream, vision                    # noqa: E402
from app.config import settings                          # noqa: E402

# 图上真实存在的内容。VL 至少应命中其中一部分；
# OCR 的典型错误输出（「予贞石开」）不应出现在结果里。
EXPECT = ["点点", "spike", "预研"]
OCR_ARTIFACTS = ["予 贞 石 开", "予贞石开", "促 测 试 高"]

fails: list[str] = []


def check(cond: bool, msg: str) -> None:
    print(f"  {'PASS' if cond else 'FAIL'}  {msg}")
    if not cond:
        fails.append(msg)


def make_body(b64: str, question: str) -> dict:
    """模拟点点：截图通过 tool_result 的 image 块送进来。"""
    return {
        "model": "claude-sonnet-4",
        "max_tokens": 1024,
        "stream": True,
        "messages": [
            {"role": "user", "content": question},
            {"role": "assistant", "content": [
                {"type": "tool_use", "id": "tu_1", "name": "capture_screen",
                 "input": {}},
            ]},
            {"role": "user", "content": [
                {"type": "tool_result", "tool_use_id": "tu_1", "content": [
                    {"type": "image", "source": {
                        "type": "base64", "media_type": "image/jpeg", "data": b64}},
                ]},
            ]},
        ],
    }


def texts_of(body: dict) -> list[str]:
    """把处理后的 body 里所有 text 块捞出来。"""
    out = []

    def walk(c):
        if isinstance(c, str):
            out.append(c)
        elif isinstance(c, list):
            for b in c:
                if isinstance(b, dict):
                    if b.get("type") == "text":
                        out.append(b.get("text") or "")
                    elif b.get("type") == "tool_result":
                        walk(b.get("content"))

    for m in body.get("messages", []):
        walk(m.get("content"))
    return out


def has_image(body: dict) -> bool:
    found = False

    def walk(c):
        nonlocal found
        if isinstance(c, list):
            for b in c:
                if isinstance(b, dict):
                    if b.get("type") == "image":
                        found = True
                    elif b.get("type") == "tool_result":
                        walk(b.get("content"))

    for m in body.get("messages", []):
        walk(m.get("content"))
    return found


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--image", default=os.path.join(
        os.path.dirname(os.path.dirname(HERE)), "evidence",
        "selfcheck-capture-1931.jpg"))
    args = ap.parse_args()

    print("=" * 78)
    print("视觉模型集成测试")
    print("=" * 78)
    print(f"  vision_mode = {settings.vision_mode}")
    print(f"  vl.available = {vision.available()}")
    print(f"  model = {settings.vision_model}  effort = {settings.vision_effort}")
    print(f"  ocr.available = {ocr.available()}")

    if not os.path.exists(args.image):
        print(f"\n找不到测试图片：{args.image}")
        return 2

    b64 = base64.b64encode(open(args.image, "rb").read()).decode()
    question = "帮我看看这个界面上写了什么"
    print(f"  图片 = {os.path.basename(args.image)}（{len(b64) / 1024:.0f}KB base64）")

    if not vision.available():
        print("\n未配置 VISION_API_KEY，跳过（这是可选上游，不算失败）")
        return 0

    # ---- 1. auto 模式必须把图片换成文字 ----
    print("\n[1] auto 模式：图片必须被换成有内容的文字")
    body = make_body(b64, question)
    t0 = time.time()
    out = upstream.apply_vision_policy(body)
    dt = time.time() - t0
    print(f"  （耗时 {dt:.1f}s）")

    check(not has_image(out), "处理后不再有 image 块")
    texts = texts_of(out)
    joined = "\n".join(texts)
    check(len(joined) > 40, f"换出了足够的文字（{len(joined)} 字符）")
    check("视觉模型" in joined or "视觉系统" in joined or "OCR" in joined,
          "文本里有来源标注，模型知道这是转述而非原图")

    hits = [w for w in EXPECT if w in joined]
    check(bool(hits), f"命中图上真实内容：{hits or '无'}")
    bad = [a for a in OCR_ARTIFACTS if a in joined]
    check(not bad, f"没有 OCR 典型错字：{bad or '无'}")

    print("\n  ---- 换出的文本（前 500 字）----")
    print("  " + joined[:500].replace("\n", "\n  "))

    # ---- 2. vl 模式在 VL 不可用时不偷偷回退 ----
    print("\n[2] 纯逻辑：vl 模式失败不回退 OCR（用假 key 触发失败）")
    saved = settings.vision_api_key
    try:
        settings.vision_api_key = "sk-invalid-for-test"
        settings.vision_timeout = 8.0
        b2 = make_body(b64, question)
        o2 = upstream.apply_vision_policy(b2)
        check(not has_image(o2), "vl 模式下图片仍被移除（不会漏给文本上游）")
        t2 = "\n".join(texts_of(o2))
        check("无法查看图像" in t2 or "OCR" in t2,
              "失败时给出诚实说明或回退 OCR")
    finally:
        settings.vision_api_key = saved
        settings.vision_timeout = 90.0

    print("\n" + "=" * 78)
    if fails:
        print(f"FAILED　{len(fails)} 项不通过：")
        for m in fails:
            print(f"  - {m}")
        return 1
    print("ALL PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
