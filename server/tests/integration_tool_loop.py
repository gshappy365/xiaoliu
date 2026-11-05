#!/usr/bin/env python3
"""集成测试：xiaoliu ⇄ 假上游 ⇄ 点点原生解码器。

这条链路之前从未被跑过——`MOCK_MODE` 只验证了 protocol.py，`upstream.py`
的透传路径是空白。这里补上，并且覆盖工具执行回环（tool_use → tool_result → 第二轮）。

    python tests/integration_tool_loop.py

它会自己拉起两个 uvicorn 进程，跑完自动收掉。
"""
from __future__ import annotations

import json
import os
import socket
import subprocess
import sys
import time
from typing import Any

import httpx

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, ROOT)

from tests.test_against_native_decoder import feed  # noqa: E402

FAKE_PORT = 8901
XIAO_PORT = 8902
RECORD = os.path.join(HERE, "_upstream_record.json")

TOOLS = [
    {"name": "capture_screen", "description": "截取整屏",
     "input_schema": {"type": "object",
                      "properties": {"reason": {"type": "string"}},
                      "required": ["reason"]}},
]


def free(port: int) -> bool:
    s = socket.socket()
    try:
        s.bind(("127.0.0.1", port))
        return True
    except OSError:
        return False
    finally:
        s.close()


def spawn(args: list[str], env: dict[str, str]) -> subprocess.Popen:
    return subprocess.Popen(
        args, cwd=ROOT, env={**os.environ, **env},
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)


def wait_http(url: str, timeout: float = 25.0) -> None:
    end = time.time() + timeout
    while time.time() < end:
        try:
            if httpx.get(url, timeout=1.0).status_code < 500:
                return
        except Exception:
            pass
        time.sleep(0.25)
    raise RuntimeError(f"{url} 没起来")


def chat(system: str, messages: list[dict[str, Any]]) -> bytes:
    r = httpx.post(
        f"http://127.0.0.1:{XIAO_PORT}/diandian/proxy/chat",
        json={
            "model": "aliyun.qwen3.8-flash",
            "max_tokens": 8192,
            "stream": True,
            "system": system,
            "messages": messages,
            "main_reasoning_effort": "medium",
            "tools": TOOLS,
            "diandian_credits": {"interactionId": "x", "quoteId": "y", "requestId": "z"},
        },
        headers={"Authorization": "Bearer selfhost", "Accept": "text/event-stream"},
        timeout=60,
    )
    r.raise_for_status()
    return r.content


def record() -> dict[str, Any]:
    with open(RECORD, "r", encoding="utf-8") as f:
        return json.load(f)


# ------------------------------------------------------------------ 用例

def t_text_passthrough() -> None:
    raw = chat("SCENARIO:text", [{"role": "user", "content": "你好"}])
    assert b"[DONE]" in raw, "服务器没补 [DONE]"
    outs, finished = feed([raw], "sse")
    text = "".join(s for o in outs for s in o)
    assert text == "我看得到你的屏幕。", repr(text)
    assert finished is True
    print(f"  ✓ 文本透传：{text!r}，[DONE] 已补，finished=True")


def t_request_rewrite() -> None:
    """上游收到的请求里，私有字段必须被剥掉、model 必须被换掉。"""
    chat("SCENARIO:text", [{"role": "user", "content": "你好"}])
    rec = record()
    body = rec["body"]
    assert "diandian_credits" not in body, "diandian_credits 没被剥掉"
    assert "main_reasoning_effort" not in body, "main_reasoning_effort 没被剥掉"
    assert body["model"] == "fake-model", f"model 没被改写: {body['model']}"
    assert body["stream"] is True
    assert len(body["tools"]) == 1
    h = {k.lower(): v for k, v in rec["headers"].items()}
    assert h.get("x-api-key") and h.get("authorization"), "密钥头没带上"
    assert h.get("anthropic-version") == "2023-06-01"
    print("  ✓ 请求改写：剥私有字段、换 model、带上密钥与 anthropic-version")


def t_tool_use_round1() -> None:
    raw = chat("SCENARIO:tool", [{"role": "user", "content": "看看我的屏幕"}])
    outs, _ = feed([raw], "agent")
    snap = outs[-1]
    assert snap["finished"] is True, snap
    assert snap["error"] is None, snap["error"]
    assert snap["stopReason"] == "tool_use", snap["stopReason"]
    assert snap["text"] == "我来截个屏。", repr(snap["text"])
    tools = [b for b in snap["content"] if b["type"] == "tool_use"]
    assert len(tools) == 1, snap["content"]
    assert tools[0]["name"] == "capture_screen"
    assert tools[0]["input"] == {"reason": "看看你在做什么"}, tools[0]["input"]
    print(f"  ✓ 工具轮 1：stopReason=tool_use，input={tools[0]['input']}")


def t_tool_result_round2() -> None:
    """回传 tool_result，验证上游能收到原样的块，且第二轮正常收尾。"""
    msgs = [
        {"role": "user", "content": "看看我的屏幕"},
        {"role": "assistant", "content": [
            {"type": "text", "text": "我来截个屏。"},
            {"type": "tool_use", "id": "toolu_fake", "name": "capture_screen",
             "input": {"reason": "看看你在做什么"}},
        ]},
        {"role": "user", "content": [
            {"type": "tool_result", "tool_use_id": "toolu_fake",
             "content": [{"type": "image", "source": {
                 "type": "base64", "media_type": "image/jpeg",
                 "data": "/9j/4AAQSkZJRg=="}},
                 {"type": "text", "text": "屏幕截图"}]},
        ]},
    ]
    raw = chat("SCENARIO:tool", msgs)
    outs, _ = feed([raw], "agent")
    snap = outs[-1]
    assert snap["finished"] is True and snap["error"] is None, snap
    assert snap["stopReason"] == "end_turn", snap["stopReason"]
    assert snap["text"] == "看到你的报销表了。", repr(snap["text"])

    rec = record()["body"]
    tr = None
    for m in rec["messages"]:
        if isinstance(m.get("content"), list):
            for b in m["content"]:
                if isinstance(b, dict) and b.get("type") == "tool_result":
                    tr = b
    assert tr is not None, "上游没收到 tool_result"
    assert tr["tool_use_id"] == "toolu_fake"
    assert tr["content"][0]["type"] == "image", "图片块丢了"
    assert tr["content"][0]["source"]["type"] == "base64"
    assert tr["content"][1]["text"] == "屏幕截图", "图片后面的 label 文本丢了"
    print("  ✓ 工具轮 2：tool_result 原样透传（含 base64 图片 + label），第二轮 end_turn")


def t_upstream_error() -> None:
    """上游 529 时，服务器应降级成 error 流，而不是把 HTTP 错误抛给客户端。"""
    raw = chat("SCENARIO:boom", [{"role": "user", "content": "x"}])
    assert b"event: error" in raw, "没有降级成 error 流"
    assert b"[DONE]" in raw, "error 流没补 [DONE]"
    outs, _ = feed([raw], "agent")
    assert outs[-1]["error"] == "provider_error", outs[-1]
    print("  ✓ 上游故障：降级成 error 流，agent 解码器读到 provider_error")


def t_metrics_and_stubs() -> None:
    b = f"http://127.0.0.1:{XIAO_PORT}"
    assert httpx.get(f"{b}/diandian/proxy/healthz").json()["status"] == "ok"
    assert httpx.get(f"{b}/diandian/proxy/capabilities").json()["chat_models"]
    assert httpx.get(f"{b}/diandian/v1/credits").json()["data"]["available"] != "0"
    assert httpx.get(f"{b}/diandian/v1/activation").json()["data"]["status"] == "active"
    r = httpx.post(f"{b}/diandian/v1/metrics/interactions/batches",
                   json={"interactions": [{"a": 1}, {"b": 2}]})
    assert r.status_code == 200
    r = httpx.post(f"{b}/diandian/proxy/credits/settlements")
    assert r.status_code == 404, "settlements 应为 404"
    print("  ✓ stub 端点：healthz/capabilities/credits/activation/metrics 全通，settlements 404")


def main() -> int:
    for p in (FAKE_PORT, XIAO_PORT):
        if not free(p):
            print(f"端口 {p} 被占用，先关掉")
            return 2
    if os.path.exists(RECORD):
        os.remove(RECORD)

    fake = spawn([sys.executable, "-m", "uvicorn", "tests.fake_upstream:app",
                  "--host", "127.0.0.1", "--port", str(FAKE_PORT),
                  "--log-level", "warning"],
                 {"FAKE_UPSTREAM_RECORD": RECORD})
    xiao = spawn([sys.executable, "-m", "uvicorn", "app.main:app",
                  "--host", "127.0.0.1", "--port", str(XIAO_PORT),
                  "--log-level", "warning"],
                 {"UPSTREAM_BASE_URL": f"http://127.0.0.1:{FAKE_PORT}",
                  "UPSTREAM_API_KEY": "test-key",
                  "UPSTREAM_MODEL": "fake-model",
                  "MOCK_MODE": "0",
                  "LOG_BODIES": "0"})

    try:
        wait_http(f"http://127.0.0.1:{FAKE_PORT}/healthz")
        wait_http(f"http://127.0.0.1:{XIAO_PORT}/")
        print(f"两个服务已起：fake={FAKE_PORT} xiaoliu={XIAO_PORT}\n")

        tests = [t_text_passthrough, t_request_rewrite, t_tool_use_round1,
                 t_tool_result_round2, t_upstream_error, t_metrics_and_stubs]
        for t in tests:
            print(f"· {t.__name__}")
            t()
        print(f"\n全部 {len(tests)} 项通过 —— 透传链路与工具回环成立。")
        return 0
    except Exception:
        print("\n--- fake upstream 输出 ---")
        print((fake.stdout.read() if fake.stdout else "")[-3000:])
        print("--- xiaoliu 输出 ---")
        print((xiao.stdout.read() if xiao.stdout else "")[-3000:])
        raise
    finally:
        for p in (fake, xiao):
            p.terminate()
            try:
                p.wait(timeout=5)
            except subprocess.TimeoutExpired:
                p.kill()


if __name__ == "__main__":
    sys.exit(main())
