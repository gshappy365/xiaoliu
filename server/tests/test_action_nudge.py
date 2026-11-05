"""动作提示回归测试。

锁的是 2026-10-28 那个「卡住没反应」：用户说「你举个例子吧，怎么演示给我看一下」，
点点只回一句「先看下你屏幕现在是什么状态。」就结束了，屏幕上什么都没有。

实测证据（回放 evidence/chat-dump#160，同一请求各跑 5 次）：

    原样            0/5 命中 capture_screen，思考 1470–20211 字，最长 107.5s
    末尾加一句提示   5/5 命中，思考 231–862 字，最长 7.0s

所以这里断言两件事：
  1. 该加的时候加（本轮没有截图 / 上一轮工具被拒）
  2. 不该加的时候不加（用户自己圈选、带了画面；普通闲聊）

用法：python tests/test_action_nudge.py
"""
from __future__ import annotations

import copy
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from app import upstream                            # noqa: E402

NUDGE = upstream._ACTION_NUDGE

fails = 0


def check(name: str, cond: bool, detail: str = "") -> None:
    global fails
    mark = "PASS" if cond else "FAIL"
    if not cond:
        fails += 1
    print(f"  [{mark}] {name}" + (f"  {detail}" if detail else ""))


def make_body(text: str, *, role: str = "user") -> dict:
    return {
        "model": "aliyun.qwen3.8-flash",
        "max_tokens": 8192,
        "stream": True,
        "system": "你是点点。",
        "tools": [{"name": "capture_screen", "description": "截图"}],
        "messages": [{"role": role, "content": text}],
    }


def last_user_text(body: dict) -> str:
    for m in reversed(body.get("messages") or []):
        if m.get("role") != "user":
            continue
        c = m.get("content")
        if isinstance(c, str):
            return c
        if isinstance(c, list):
            return " ".join(b.get("text") or "" for b in c
                            if isinstance(b, dict) and b.get("type") == "text")
    return ""


def main() -> int:
    print("动作提示")

    # 1. 本轮没有附带截图 —— 这是最常见的工具回合，必须加
    b = make_body("帮我在这上面画个正方形\n\n本轮没有附带屏幕截图")
    out = upstream.apply_action_nudge(b)
    check("没有截图的回合会加提示", NUDGE in last_user_text(out))

    # 2. 上一轮工具被拒 —— recovery 回合，同样要加
    b = make_body('{"status":"rejected","error_code":"stale_capture"}')
    out = upstream.apply_action_nudge(b)
    check("stale_capture 回合会加提示", NUDGE in last_user_text(out))

    # 3. 普通闲聊 —— 不加，否则每句话都在催它截图
    b = make_body("今天星期几？")
    out = upstream.apply_action_nudge(b)
    check("普通对话不加提示", NUDGE not in last_user_text(out))

    # 4. 幂等 —— 客户端重发同一轮时不能叠成两句
    b = make_body("画个正方形\n\n本轮没有附带屏幕截图")
    once = upstream.apply_action_nudge(b)
    twice = upstream.apply_action_nudge(once)
    check("重复调用不叠加", last_user_text(twice).count(NUDGE) == 1,
          f"出现 {last_user_text(twice).count(NUDGE)} 次")

    # 5. 不改动调用方传进来的对象
    b = make_body("画个正方形\n\n本轮没有附带屏幕截图")
    before = copy.deepcopy(b)
    upstream.apply_action_nudge(b)
    check("不修改入参", b == before)

    # 6. 内容块形状（tool_result 之后追加 text 块）
    b = make_body("画个正方形")
    b["messages"] = [
        {"role": "user", "content": "画个正方形\n\n本轮没有附带屏幕截图"},
        {"role": "assistant", "content": [
            {"type": "tool_use", "id": "t1", "name": "capture_screen", "input": {}}]},
        {"role": "user", "content": [
            {"type": "tool_result", "tool_use_id": "t1", "content": "1280x720"}]},
    ]
    out = upstream.apply_action_nudge(b)
    tail = out["messages"][-1]["content"]
    check("tool_result 后追加 text 块",
          isinstance(tail, list) and tail[-1].get("type") == "text"
          and tail[-1].get("text") == NUDGE)
    check("原有 tool_result 块保留", tail[0].get("type") == "tool_result")

    # 7. 顺序：思考策略必须在加提示之前跑完，否则提示里的词会污染判断
    b = make_body("画个正方形\n\n本轮没有附带屏幕截图")
    out = upstream.rewrite_request(b)
    check("rewrite 后提示在位", NUDGE in last_user_text(out))
    check("rewrite 后思考仍按原判断开启",
          (out.get("thinking") or {}).get("type") == "enabled")

    print()
    if fails:
        print(f"{fails} 项失败")
        return 1
    print("全部通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
