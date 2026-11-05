"""验收 Agent Plan 识图：doubao-seed-2-0-mini + 关闭深度思考。

判据：
  1. VL 后端选中 ark
  2. 能对真实截图给出非空描述
  3. reasoning_tokens == 0（确认 minimal 真的关掉了思考）
  4. 耗时不高于 10 秒（基线 2.1s）

背景：arkcli 的 +understand/+chat 对图像一律 404（走 Responses 通道），
但直连 api/plan/v3/chat/completions 完全正常。所以这里测的是直连路径。

用法：python tests/test_ark_vision.py
"""
from __future__ import annotations

import base64
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, HERE)

import ark_common                                          # noqa: E402

KEY = ark_common.ensure_key()

sys.path.insert(0, os.path.dirname(HERE))

from app import vision                                     # noqa: E402

IMAGE = os.path.join(ROOT, "evidence", "selfcheck-capture-1931.jpg")
QUESTION = "屏幕上是什么软件？顶部有什么提示？"

fails: list[str] = []


def check(cond: bool, msg: str) -> None:
    print(f"  {'PASS' if cond else 'FAIL'}  {msg}")
    if not cond:
        fails.append(msg)


def main() -> int:
    if not KEY:
        return ark_common.skip("没有 ARK_API_KEY，且 arkcli 取不到套餐 Key")
    if not os.path.isfile(IMAGE):
        return ark_common.skip(f"测试图片不存在：{IMAGE}")

    print("=" * 78)
    print("Agent Plan 识图测试（doubao-seed-2-0-mini / minimal）")
    print("=" * 78)

    b = vision.vl_backend()
    print(f"\nVL 后端：{b}")
    check(b == "ark", f"VL 后端选中 ark（实际 {b}）")

    raw = open(IMAGE, "rb").read()
    b64 = base64.b64encode(raw).decode()
    print(f"图片：{os.path.basename(IMAGE)}　{len(raw) / 1024:.0f}KB")

    print("\n[1] 识图")
    text = vision.describe(b64, "image/jpeg", QUESTION)
    usage = dict(vision.LAST_USAGE)
    print(f"     耗时 {usage.get('ms')}ms　推理 {usage.get('reasoning_tokens')} tok")
    print(f"     正文 {len(text)} 字：{text[:160]!r}")
    check(bool(text), "返回非空描述")
    check((usage.get("reasoning_tokens") or 0) == 0,
          f"reasoning_tokens = 0（实际 {usage.get('reasoning_tokens')}）")
    check(usage.get("backend") == "ark", f"实际走的是 ark（{usage.get('backend')}）")
    check((usage.get("ms") or 99999) < 10000,
          f"耗时 {usage.get('ms')}ms < 10s")

    print("\n[2] 内容相关性")
    # 这张图里明确存在的词。不要求全中，但至少命中一个。
    keys = ["点点", "预研", "在听", "M3", "spike"]
    hits = [k for k in keys if k in text]
    check(bool(hits), f"命中截图特征词：{hits}")

    print("\n" + "=" * 78)
    if fails:
        print(f"FAILED　{len(fails)} 项不通过：")
        for m in fails:
            print(f"  - {m}")
        return 1
    print("ALL PASS　套餐识图可用，且确实关闭了深度思考")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
