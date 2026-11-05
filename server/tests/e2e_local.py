#!/usr/bin/env python3
"""端到端验收：服务器 → 点点原生解码器。

真发一个 HTTP 请求给本地服务器，把响应字节流原样喂给
pipeline_brain_ffi.dll，验证客户端能正确消费。

    python tests/e2e_local.py [base_url]
"""
from __future__ import annotations

import json
import os
import sys

import httpx

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from tests.test_against_native_decoder import feed  # noqa: E402

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:8899"

# 照抄抓包里的 /chat 请求体结构（19 个工具里挑 2 个）
BODY = {
    "model": "aliyun.qwen3.8-flash",
    "max_tokens": 8192,
    "stream": True,
    "system": "你是小刘，一个能在屏幕上看东西的助手。",
    "messages": [
        {"role": "user", "content": [{"type": "text", "text": "你能看到我的屏幕吗？"}]},
    ],
    "main_reasoning_effort": "medium",
    "tools": [
        {"name": "capture_screen", "description": "截取整屏",
         "input_schema": {"type": "object", "properties": {
             "reason": {"type": "string"}}, "required": ["reason"]}},
        {"name": "insert_text", "description": "在光标处插入文字",
         "input_schema": {"type": "object", "properties": {
             "text": {"type": "string"}}, "required": ["text"]}},
    ],
    "diandian_credits": {
        "interactionId": "7f3f6aff-74d2-4539-9726-99b2577cdb4f",
        "quoteId": "4f88a79b-544e-4a4b-bfc1-bc18c2b8311f",
        "requestId": "9ade9e8d-d5ec-48b7-a88a-f60bd508b206",
    },
}


def main() -> int:
    with httpx.Client(timeout=120) as c:
        r = c.post(f"{BASE}/diandian/proxy/chat", json=BODY,
                   headers={"Authorization": "Bearer selfhost",
                            "Accept": "text/event-stream"})
        r.raise_for_status()
        raw = r.content
        ctype = r.headers.get("content-type", "")

    print(f"POST {BASE}/diandian/proxy/chat -> {r.status_code} {ctype}")
    print(f"响应 {len(raw)} 字节，{raw.count(b'event:')} 个事件\n")

    # 无工具路径
    outs, finished = feed([raw], "sse")
    text = "".join(s for o in outs for s in o)
    print(f"[无工具] SSE 解码器: finished={finished} text={text!r}")
    assert finished is True, "sse 解码器未收到 [DONE]"
    assert text, "sse 解码器没拿到文本"

    # 有工具路径
    outs, _ = feed([raw], "agent")
    snap = outs[-1]
    print(f"[有工具] agent 解码器: finished={snap['finished']} "
          f"stopReason={snap['stopReason']} error={snap['error']} "
          f"text={snap['text']!r} blocks={[b['type'] for b in snap['content']]}")
    assert snap["finished"] is True, "agent 解码器 finished 不为真"
    assert snap["error"] is None, f"agent 解码器报错: {snap['error']}"

    print("\n端到端通过：服务器响应被点点原生解码器完整接受。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
