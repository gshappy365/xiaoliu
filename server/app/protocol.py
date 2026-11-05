"""Anthropic Messages SSE 帧构造。

所有帧格式都经过 pipeline_brain_ffi.dll 实测验证（见 reports/decoder-abi-oracle-20261026.md）：

- `event: X\ndata: {json}\n\n` 正确解析；`event:` 行可省，但 **agent 解码器靠事件
  类型字段判断**，所以照发更安全
- 一帧必须凑齐 `\n\n` 才吐出，内部会缓冲 TCP 分片
- 结束标志是 `data: [DONE]`，**不是** `message_stop`
- 事件顺序必须完整，否则带工具的请求会被判 malformed：
      message_start
      → (content_block_start → *_delta* → content_block_stop)*
      → message_delta
      → message_stop
      → [DONE]
- tool_use 的 input 必须走 input_json_delta + partial_json 分片，解码器会拼接
- thinking 块必须带 signature_delta
"""
from __future__ import annotations

import json
import uuid
from typing import Any, Iterable, Iterator

DONE_FRAME = b"data: [DONE]\n\n"


def sse(event: str, data: dict[str, Any]) -> bytes:
    """构造一个 SSE 帧。紧凑 JSON，避免多余空白。"""
    payload = json.dumps(data, ensure_ascii=False, separators=(",", ":"))
    return f"event: {event}\ndata: {payload}\n\n".encode("utf-8")


def _msg_id() -> str:
    return "msg_" + uuid.uuid4().hex[:24]


class StreamWriter:
    """按 Anthropic Messages 规范生成一条完整的流式响应。

    用法：
        w = StreamWriter(model="...", input_tokens=42)
        yield from w.start()
        yield from w.text("你好")
        yield from w.finish(stop_reason="end_turn", output_tokens=3)
    """

    def __init__(self, model: str, input_tokens: int = 0, message_id: str | None = None):
        self.model = model
        self.input_tokens = input_tokens
        self.message_id = message_id or _msg_id()
        self._index = -1
        self._open = False
        self._started = False

    # ---- 生命周期 ----------------------------------------------------

    def start(self) -> Iterator[bytes]:
        self._started = True
        yield sse("message_start", {
            "type": "message_start",
            "message": {
                "id": self.message_id,
                "type": "message",
                "role": "assistant",
                "model": self.model,
                "content": [],
                "stop_reason": None,
                "stop_sequence": None,
                "usage": {"input_tokens": self.input_tokens, "output_tokens": 0},
            },
        })

    # ---- 内容块 ------------------------------------------------------

    def _begin_block(self, block: dict[str, Any]) -> Iterator[bytes]:
        if self._open:
            yield from self._end_block()
        self._index += 1
        self._open = True
        yield sse("content_block_start", {
            "type": "content_block_start",
            "index": self._index,
            "content_block": block,
        })

    def _end_block(self) -> Iterator[bytes]:
        if not self._open:
            return
        self._open = False
        yield sse("content_block_stop", {
            "type": "content_block_stop",
            "index": self._index,
        })

    def text(self, chunk: str) -> Iterator[bytes]:
        """追加文本增量。自动在需要时开一个 text 块。"""
        if not chunk:
            return
        if not self._open or self._current_kind != "text":
            yield from self._begin_block({"type": "text", "text": ""})
            self._current_kind = "text"
        yield sse("content_block_delta", {
            "type": "content_block_delta",
            "index": self._index,
            "delta": {"type": "text_delta", "text": chunk},
        })

    def thinking(self, chunk: str) -> Iterator[bytes]:
        if not chunk:
            return
        if not self._open or self._current_kind != "thinking":
            yield from self._begin_block({"type": "thinking", "thinking": ""})
            self._current_kind = "thinking"
        yield sse("content_block_delta", {
            "type": "content_block_delta",
            "index": self._index,
            "delta": {"type": "thinking_delta", "thinking": chunk},
        })

    def thinking_signature(self, signature: str) -> Iterator[bytes]:
        """thinking 块结束前必须发，否则回传历史时上游可能拒。"""
        yield sse("content_block_delta", {
            "type": "content_block_delta",
            "index": self._index,
            "delta": {"type": "signature_delta", "signature": signature},
        })

    def tool_use(self, tool_id: str, name: str, arguments: dict[str, Any],
                 chunk_size: int = 96) -> Iterator[bytes]:
        """发一个完整的 tool_use 块。input 走 partial_json 分片。"""
        yield from self._begin_block({
            "type": "tool_use", "id": tool_id, "name": name, "input": {},
        })
        self._current_kind = "tool_use"
        raw = json.dumps(arguments, ensure_ascii=False, separators=(",", ":"))
        for i in range(0, len(raw), chunk_size):
            yield sse("content_block_delta", {
                "type": "content_block_delta",
                "index": self._index,
                "delta": {"type": "input_json_delta",
                          "partial_json": raw[i:i + chunk_size]},
            })
        yield from self._end_block()

    # ---- 收尾 --------------------------------------------------------

    def finish(self, stop_reason: str = "end_turn", output_tokens: int = 0,
               include_done: bool = True) -> Iterator[bytes]:
        yield from self._end_block()
        if not self._started:
            yield from self.start()
        yield sse("message_delta", {
            "type": "message_delta",
            "delta": {"stop_reason": stop_reason, "stop_sequence": None},
            "usage": {"output_tokens": output_tokens},
        })
        yield sse("message_stop", {"type": "message_stop"})
        if include_done:
            # 客户端 while (!decoder.IsFinished) 靠这一帧退出
            yield DONE_FRAME

    _current_kind: str = ""


def simple_reply(model: str, text: str, input_tokens: int = 0,
                 chunk_size: int = 24) -> Iterable[bytes]:
    """把一段文本切成增量发出去。用于无工具路径的冒烟测试。"""
    w = StreamWriter(model=model, input_tokens=input_tokens)
    yield from w.start()
    for i in range(0, len(text), chunk_size):
        yield from w.text(text[i:i + chunk_size])
    yield from w.finish(stop_reason="end_turn", output_tokens=len(text))


def error_stream(model: str, message: str = "upstream error") -> Iterable[bytes]:
    """上游失败时的降级流。注意：agent 解码器会把 error 事件映射成
    error="provider_error"，客户端据此抛 InvalidDataException。"""
    yield sse("error", {
        "type": "error",
        "error": {"type": "api_error", "message": message},
    })
    yield DONE_FRAME
