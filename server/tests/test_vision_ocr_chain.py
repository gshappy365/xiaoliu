"""PaddleOCR-VL 接入 upstream 策略链的端到端验证。

test_paddle_ocr.py 验的是「paddle 这个模块能不能用」，
这里验的是「服务端在 OCR 模式下会不会真的用它，并且把文字塞进正确的位置」。

走的是真实代码路径 `upstream.apply_vision_policy()`，只跳过 HTTP 传输层，
所以不用重启线上服务、也不用额外占一份显存。

断言要点：
  1. 图片块必须消失（不能漏给只吃文本的 spark-x2.6）
  2. 换出来的文字必须带 OCR 来源标注（模型才知道这是转述）
  3. 文字必须命中图上真实内容
  4. 不能有 Windows OCR 的 `[y=` 行坐标标记（那是回退了的证据）

用法：python tests/test_vision_ocr_chain.py
"""
from __future__ import annotations

import base64
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from app import ocr, paddle_ocr, upstream               # noqa: E402
from app.config import settings                         # noqa: E402

IMG = os.path.join(os.path.dirname(os.path.dirname(HERE)),
                   "evidence", "selfcheck-capture-1931.jpg")

EXPECT = ["点点", "预研", "spike", "集成"]
# Windows OCR 的典型错字。出现任何一条就说明静默回退了。
WIN_OCR_ARTIFACTS = ["予 贞 石 开", "予贞石开", "促 测 试 高"]

fails = 0


def check(name: str, cond: bool, detail: str = "") -> None:
    global fails
    if not cond:
        fails += 1
    print(f"  [{'PASS' if cond else 'FAIL'}] {name}" + (f"  {detail}" if detail else ""))


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
    out: list[str] = []

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
    print("=" * 70)
    print("OCR 模式策略链端到端")
    print("=" * 70)
    print(f"  ocr.available = {ocr.available()}")
    print(f"  paddle = {paddle_ocr.status()}")

    if not os.path.isfile(IMG):
        print(f"\n找不到测试图片：{IMG}")
        return 2
    if not paddle_ocr.available():
        print("\npaddle 不可用，跳过")
        return 0

    b64 = base64.b64encode(open(IMG, "rb").read()).decode()

    # 强制 OCR 模式，验证的就是这条路径
    saved_mode = settings.vision_mode
    settings.vision_mode = "ocr"
    try:
        print("\n[1] VISION_MODE=ocr：图片必须被 paddle 换成文字")
        body = make_body(b64, "帮我看看这个界面上写了什么")
        t0 = time.time()
        out = upstream.apply_vision_policy(body)
        dt = time.time() - t0
        print(f"  （耗时 {dt:.1f}s）")

        check("处理后不再有 image 块", not has_image(out))
        joined = "\n".join(texts_of(out))
        check("换出了足够的文字", len(joined) > 40, f"{len(joined)} 字符")
        check("带 OCR 来源标注", "本机 OCR" in joined or "OCR 读取" in joined)
        # 关键：没有走 Windows OCR。它的输出每行都是 `[y=123] ...`
        check("没有 Windows OCR 行坐标", "[y=" not in joined)
        hits = [w for w in EXPECT if w in joined]
        check("命中图上真实内容", len(hits) >= 3, f"{hits}")
        bad = [a for a in WIN_OCR_ARTIFACTS if a in joined]
        check("没有 Windows OCR 典型错字", not bad, f"{bad or '无'}")
        check("速度 < 20s", dt < 20, f"{dt:.1f}s")

        print("\n  ---- 换出的文本（前 400 字）----")
        print("  " + joined[:400].replace("\n", "\n  "))

        print("\n[2] 校验模板措辞：不能出现会让 spark-x2.6 拒答的词")
        # 实测过：提示词里出现「截图」二字，spark-x2.6 会条件反射回
        # 「我无法直接读取图片内容」，完全无视后面 680 字的真实内容。
        # 注意只看**模板本身**（头尾固定文案）—— OCR 正文里出现「截图」
        # 是正常的，图上本来就有这个标签。
        head, tail = joined[:60], joined[-60:]
        check("模板头部不含「截图」", "截图" not in head, head.splitlines()[0] if head else "")
        check("模板尾部不含「截图」", "截图" not in tail)
        check("模板头部不含「无法查看」", "无法查看" not in head)
    finally:
        settings.vision_mode = saved_mode

    print("\n" + "=" * 70)
    print("ALL PASS" if fails == 0 else f"{fails} 项失败")
    return 1 if fails else 0


if __name__ == "__main__":
    raise SystemExit(main())
