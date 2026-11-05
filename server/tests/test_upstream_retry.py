"""上游连接重试测试。

背景（2026-10-28 实测）：这台机器经 `HTTPS_PROXY=127.0.0.1:7897` 出去，代理瞬断时
httpx 会在代理 tunnel 的 `start_tls` 阶段抛 ConnectError，客户端只看到
「模型流无效：provider_error」。服务端必须在**还没向客户端吐任何字节**时重试一次。

这里用裸 socket 起一个本地服务，按脚本控制每次连接的行为：
    "close" → 收完请求直接断开（模拟代理瞬断）
    "ok"    → 回一条合法的 Anthropic SSE
    "400"   → 回 400（不可重试）

用法：python tests/test_upstream_retry.py
"""
from __future__ import annotations

import asyncio
import os
import socket
import sys
import threading

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

# 测试目标在本机，必须绕过系统代理，否则请求会被送到 127.0.0.1:7897
os.environ["NO_PROXY"] = "127.0.0.1,localhost"
os.environ["no_proxy"] = "127.0.0.1,localhost"

from app import upstream                                 # noqa: E402
from app.config import settings                           # noqa: E402

SSE = (b'event: message_start\ndata: {"type":"message_start"}\n\n'
       b'event: message_stop\ndata: {"type":"message_stop"}\n\n'
       b'data: [DONE]\n\n')

fails = 0
attempts: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    global fails
    if not cond:
        fails += 1
    print(f"  [{'PASS' if cond else 'FAIL'}] {name}" + (f"  {detail}" if detail else ""))


def serve(sock: socket.socket, script: list[str]) -> None:
    for action in script:
        try:
            conn, _ = sock.accept()
        except OSError:
            return
        attempts.append(action)
        try:
            conn.settimeout(5)
            try:
                conn.recv(65536)
            except OSError:
                pass
            if action == "close":
                conn.close()
                continue
            if action == "400":
                body = b'{"error":"bad request"}'
                conn.sendall(b"HTTP/1.1 400 Bad Request\r\nContent-Type: application/json\r\n"
                             b"Connection: close\r\nContent-Length: %d\r\n\r\n" % len(body) + body)
            else:
                conn.sendall(b"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n"
                             b"Connection: close\r\nContent-Length: %d\r\n\r\n" % len(SSE) + SSE)
        finally:
            try:
                conn.close()
            except OSError:
                pass
    try:
        sock.close()
    except OSError:
        pass


def run(script: list[str]):
    """起一个按脚本回应的本地上游，跑一次 stream_chat，返回 (chunks, error)。"""
    global attempts
    attempts = []
    sock = socket.socket()
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    sock.bind(("127.0.0.1", 0))
    sock.listen(4)
    port = sock.getsockname()[1]

    old = (settings.upstream_base_url, settings.upstream_api_key, settings.upstream_thinking)
    settings.upstream_base_url = f"http://127.0.0.1:{port}"
    settings.upstream_api_key = "test-key"
    settings.upstream_thinking = "smart"
    threading.Thread(target=serve, args=(sock, script), daemon=True).start()

    body = {"model": "x", "max_tokens": 100, "stream": True,
            "messages": [{"role": "user", "content": "你好"}]}

    async def collect():
        out = bytearray()
        async for chunk in upstream.stream_chat(body):
            out.extend(chunk)
        return bytes(out)

    err = None
    chunks = b""
    try:
        chunks = asyncio.run(collect())
    except Exception as exc:                             # noqa: BLE001
        err = exc
    finally:
        settings.upstream_base_url, settings.upstream_api_key, settings.upstream_thinking = old
    return chunks, err


def main() -> int:
    print("=" * 70)
    print("上游连接重试")
    print("=" * 70)

    print("\n1. 第一次瞬断、第二次正常 → 应重试并成功")
    chunks, err = run(["close", "ok"])
    check("没有抛错", err is None, repr(err)[:80] if err else "")
    check("拿到了 SSE", b"message_stop" in chunks, f"{len(chunks)} 字节")
    check("连接了 2 次", len(attempts) == 2, str(attempts))

    print("\n2. 连续两次瞬断 → 应重试一次后抛出")
    chunks, err = run(["close", "close"])
    check("抛出了连接错误", err is not None and isinstance(
        err, (upstream.httpx.ConnectError, upstream.httpx.RemoteProtocolError,
              upstream.httpx.ReadError)), type(err).__name__ if err else "None")
    check("连接了 2 次后放弃", len(attempts) == 2, str(attempts))

    print("\n3. 400 不是连接错误 → 不重试")
    chunks, err = run(["400"])
    check("抛出 UpstreamError", isinstance(err, upstream.UpstreamError),
          type(err).__name__ if err else "None")
    check("只连接 1 次", len(attempts) == 1, str(attempts))

    print("\n" + ("全部通过" if fails == 0 else f"{fails} 项失败"))
    return 0 if fails == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
