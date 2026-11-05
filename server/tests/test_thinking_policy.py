"""推理强度策略测试。

背景：spark-x2.6 是推理模型，默认先吐几百字 thinking 才出正文。
实测同一句「帮我看看屏幕上写了什么」：
    开思考   首正文 4716ms
    关思考   首正文 1112ms   ← 快 4.2 倍
语音助手是先出声再慢慢说，首字延迟比总时长重要，所以默认关掉。

用法：python tests/test_thinking_policy.py
"""
from __future__ import annotations

import copy
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from app import upstream                                # noqa: E402
from app.config import settings                          # noqa: E402

fails = 0


def check(name: str, cond: bool, detail: str = "") -> None:
    global fails
    if not cond:
        fails += 1
    print(f"  [{'PASS' if cond else 'FAIL'}] {name}" + (f"  {detail}" if detail else ""))


def body(effort="medium") -> dict:
    return {
        "model": "aliyun.qwen3.8-flash",
        "max_tokens": 8192,
        "stream": True,
        "system": "sys",
        "main_reasoning_effort": effort,
        "messages": [{"role": "user", "content": "看看屏幕"}],
    }


STATUS_NO_SHOT = ("客户端运行状态更新（不是用户的新请求）：\n"
                  "【画面来源与能力】\n"
                  "当前画面状态：本轮没有附带屏幕截图，你看不到他当前的界面。")


def body_with_status(status: str = STATUS_NO_SHOT) -> dict:
    b = body()
    b["messages"] = [
        {"role": "user", "content": "帮我看看屏幕上写了什么"},
        {"role": "assistant", "content": "好的"},
        {"role": "user", "content": "那你现在截图吧"},
        {"role": "user", "content": status},
    ]
    return b


def body_with_rejection() -> dict:
    b = body()
    b["messages"] = [
        {"role": "user", "content": "在屏幕上画个三角"},
        {"role": "assistant", "content": [
            {"type": "tool_use", "id": "c1", "name": "point_at_screen", "input": {}}]},
        {"role": "user", "content": [
            {"type": "tool_result", "tool_use_id": "c1",
             "content": '{"status":"rejected","error_code":"stale_capture",'
                        '"failure_reason":"screen_changed",'
                        '"recovery":"refresh_capture"}'}]},
    ]
    return b


def main() -> int:
    old = settings.upstream_thinking
    print("=" * 70)
    print(f"推理强度策略（默认 UPSTREAM_THINKING={old}）")
    print("=" * 70)
    try:
        print("\n1. off：明确关闭")
        settings.upstream_thinking = "off"
        out = upstream.rewrite_request(body("high"))
        check("发出 thinking.disabled", out.get("thinking") == {"type": "disabled"},
              str(out.get("thinking")))
        check("main_reasoning_effort 已剥掉", "main_reasoning_effort" not in out)

        print("\n2. on：不干预")
        settings.upstream_thinking = "on"
        out = upstream.rewrite_request(body("medium"))
        check("不塞 thinking 字段", "thinking" not in out, str(out.get("thinking")))

        print("\n3. auto：按客户端强度映射")
        settings.upstream_thinking = "auto"
        cases = [
            ("none", {"type": "disabled"}),
            ("minimal", {"type": "disabled"}),
            ("low", {"type": "enabled", "budget_tokens": 512}),
            ("medium", {"type": "enabled", "budget_tokens": 2048}),
            ("high", {"type": "enabled", "budget_tokens": 8192}),
        ]
        for effort, want in cases:
            got = upstream.rewrite_request(body(effort)).get("thinking")
            check(f"effort={effort:8s} → {want}", got == want, str(got))
        got = upstream.rewrite_request(body("unknown")).get("thinking")
        check("未知强度不塞字段", got is None, str(got))

        print("\n4. smart：需要主动调工具的回合才开思考")
        settings.upstream_thinking = "smart"
        got = upstream.rewrite_request(body_with_status()).get("thinking")
        check("无截图状态 → 开 1024 token 思考",
              got == {"type": "enabled", "budget_tokens": 1024}, str(got))
        got = upstream.rewrite_request(body()).get("thinking")
        check("普通回合 → 关闭思考", got == {"type": "disabled"}, str(got))
        got = upstream.rewrite_request(body_with_rejection()).get("thinking")
        check("上一轮工具被拒（stale_capture）→ 开思考",
              got == {"type": "enabled", "budget_tokens": 1024}, str(got))
        # 客户端有时把 tool_result 再包一层字符串，引号会被转义
        wrapped = body()
        wrapped["messages"] = [{
            "role": "user",
            "content": '客户端工具结果数据：[{\\"recovery\\":\\"refresh_capture\\"}]',
        }]
        got = upstream.rewrite_request(wrapped).get("thinking")
        check("转义字符串里的 recovery 也能识别",
              got == {"type": "enabled", "budget_tokens": 1024}, str(got))
        # 旧的状态串不该点亮后续回合（只看最后 4 条）
        old_status = body_with_status()
        old_status["messages"] = (old_status["messages"]
                                  + [{"role": "user", "content": f"第{i}轮"} for i in range(4)])
        got = upstream.rewrite_request(old_status).get("thinking")
        check("历史里的旧状态串不触发", got == {"type": "disabled"}, str(got))

        print("\n5. 方舟模型：只看推理强度，不套 smart 那条拐杖")
        # 方舟那条分支不看 UPSTREAM_THINKING，这里钉住「与全局策略无关」
        settings.upstream_thinking = "smart"
        ARK = "doubao-seed-2-1-pro-260915"
        got = upstream.rewrite_request(body(), ARK).get("thinking")
        check("普通回合 → 关闭思考", got == {"type": "disabled"}, str(got))
        got = upstream.rewrite_request(body("high"), ARK).get("thinking")
        check("effort=high → 开 4096（深度档）",
              got == {"type": "enabled", "budget_tokens": 4096}, str(got))
        # 这条是**故意**关着的，别"顺手修"成开思考：小六的客户端在运行时状态里
        # 明确写了「不允许模型自主抓屏，不要调用 capture_screen」，模型不调工具
        # 是照客户端要求做。实测开 1024 思考首字 9527ms、关思考 2064ms，两次
        # 回答完全一样（都说看不到、请用户圈选），思考纯属白花时间。
        got = upstream.rewrite_request(body_with_status(), ARK).get("thinking")
        check("无截图状态 → 仍然关闭思考（客户端禁止自主抓屏）",
              got == {"type": "disabled"}, str(got))
        got = upstream.rewrite_request(body_with_rejection(), ARK).get("thinking")
        check("工具被拒 → 仍然关闭思考", got == {"type": "disabled"}, str(got))

        print("\n6. 不破坏其它字段")
        settings.upstream_thinking = "off"
        src = body("medium")
        out = upstream.rewrite_request(copy.deepcopy(src))
        check("model 原样保留（路由交给 pick_upstream）",
              out["model"] == src["model"], out["model"])
        # 空 model 才回落到默认上游名，否则 pick_upstream 无从判断用户选了什么
        blank = copy.deepcopy(src)
        blank["model"] = ""
        check("空 model 回落到 upstream_model",
              upstream.rewrite_request(blank)["model"] == settings.upstream_model,
              upstream.rewrite_request(blank)["model"])
        check("messages 原样", out["messages"] == src["messages"])
        check("system 原样", out["system"] == "sys")
        check("max_tokens 保留", out["max_tokens"] == 8192)
        check("stream 强制 True", out["stream"] is True)
    finally:
        settings.upstream_thinking = old

    print("\n" + ("全部通过" if fails == 0 else f"{fails} 项失败"))
    return 0 if fails == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
