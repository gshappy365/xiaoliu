"""重复识图回归测试。

背景（2026-10-28 15:41 实测）：点点每轮都重发完整历史，历史里的截图会再出现一次。
服务端 `apply_vision_policy` 每轮都重新调一遍 VL，于是同一张图被识别多次。

    evidence/chat-dump 统计：73 个图片实例，只有 38 张唯一图片 → 平均识别 1.9 次

代价是每张图多花约 4 秒和约 2500 token。日志里可以直接看到：

    15:41:18  chat 25 条消息，图片 hash 2b8029d0abbb
    15:41:22  vision(ark) 识图完成 4056ms
    15:41:43  chat 28 条消息，图片 hash 2b8029d0abbb   ← 同一张
    15:41:47  vision(ark) 识图完成 4100ms

用法：python tests/test_vision_cache.py
"""
from __future__ import annotations

import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from app import upstream                               # noqa: E402
from app.config import settings                        # noqa: E402

fails = 0


def check(name: str, ok: bool, detail: str = "") -> None:
    global fails
    print(f"  {'PASS' if ok else 'FAIL'}  {name}" + (f"  [{detail}]" if detail else ""))
    if not ok:
        fails += 1


def body_with(data: str, question: str = "这屏是什么") -> dict:
    return {
        "messages": [
            {"role": "user", "content": "刚才那句话"},
            {"role": "assistant", "content": [{"type": "tool_use", "id": "t1",
                                               "name": "capture_screen", "input": {}}]},
            {"role": "user", "content": [
                {"type": "tool_result", "tool_use_id": "t1", "content": [
                    {"type": "text", "text": "capture_call_id=1"},
                    {"type": "image", "source": {"type": "base64",
                                                 "media_type": "image/jpeg",
                                                 "data": data}},
                ]},
                {"type": "text", "text": question},
            ]},
        ]
    }


def main() -> int:
    print("=" * 62)
    print("重复识图回归测试")
    print("=" * 62)

    calls: list[str] = []
    original = (upstream.vision.available, upstream.vision.describe)

    upstream.vision.available = lambda: True                    # type: ignore[assignment]

    def fake_describe(data: str, media: str = "image/jpeg", question: str = "") -> str:
        calls.append(question)
        return f"画面描述（第 {len(calls)} 次调用）"

    upstream.vision.describe = fake_describe                    # type: ignore[assignment]

    def clear_cache() -> None:
        cache = getattr(upstream, "_vl_cache", None)
        if cache is not None:
            cache.clear()

    clear_cache()
    old_mode = settings.vision_mode
    settings.vision_mode = "auto"

    try:
        print("\n[1] 同一张图连续两轮：只应识别一次")
        data = "AAAABBBBCCCC" * 50
        out1 = upstream.apply_vision_policy(body_with(data))
        n1 = len(calls)
        out2 = upstream.apply_vision_policy(body_with(data))
        n2 = len(calls)

        check("第一轮调用了 VL", n1 == 1, f"{n1} 次")
        check("第二轮没有重复调用", n2 == n1, f"累计 {n2} 次")

        t1 = out1["messages"][-1]["content"][0]["content"][1]["text"]
        t2 = out2["messages"][-1]["content"][0]["content"][1]["text"]
        check("两轮注入的文字一致", t1 == t2)
        check("注入的文字里有画面内容", "画面描述" in t1)

        print("\n[2] 换一张图必须重新识别")
        out3 = upstream.apply_vision_policy(body_with("DDDDEEEEFFFF" * 50))
        check("新图触发了 VL", len(calls) == n2 + 1, f"累计 {len(calls)} 次")
        t3 = out3["messages"][-1]["content"][0]["content"][1]["text"]
        check("新图拿到新描述", t3 != t1)

        print("\n[3] 问题不同应视为不同请求")
        out4 = upstream.apply_vision_policy(body_with(data, question="那个按钮在哪"))
        check("问题变了触发 VL", len(calls) == n2 + 2, f"累计 {len(calls)} 次")
        t4 = out4["messages"][-1]["content"][0]["content"][1]["text"]
        check("不同问题拿到不同描述", t4 != t1)
    finally:
        upstream.vision.available, upstream.vision.describe = original  # type: ignore[assignment]
        settings.vision_mode = old_mode
        clear_cache()

    print("\n" + "=" * 62)
    print("ALL PASS" if fails == 0 else f"{fails} 项失败")
    return 1 if fails else 0


if __name__ == "__main__":
    raise SystemExit(main())
