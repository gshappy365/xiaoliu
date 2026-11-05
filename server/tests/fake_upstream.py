#!/usr/bin/env python3
"""假上游：一个最小的 Anthropic Messages 兼容端点。

用途：在没有 API key 的情况下，把 xiaoliu 的 `upstream.py` 透传路径压一遍。
它按场景吐出真实形状的 SSE，并且**故意不发 `data: [DONE]`**——正好验证
服务器会补上这一帧。

启动：
    uvicorn tests.fake_upstream:app --port 8901

场景通过请求体的 `model` 字段选择（服务器会把 model 改写成 UPSTREAM_MODEL，
所以更稳的是走 HTTP 头 `X-Fake-Scenario`，但那个头会被 xiaoliu 丢掉；
因此这里按 model 名里的关键字判断）。
"""
from __future__ import annotations

import json
import os
from typing import Any, AsyncIterator

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse, StreamingResponse

app = FastAPI(title="fake anthropic upstream")

RECORD = os.environ.get("FAKE_UPSTREAM_RECORD", "")


def sse(event: str, data: dict[str, Any]) -> bytes:
    return (f"event: {event}\ndata: "
            f"{json.dumps(data, ensure_ascii=False, separators=(',', ':'))}\n\n"
            ).encode()


def _start() -> list[bytes]:
    return [sse("message_start", {
        "type": "message_start",
        "message": {
            "id": "msg_fake", "type": "message", "role": "assistant",
            "model": "fake-upstream", "content": [],
            "stop_reason": None, "stop_sequence": None,
            "usage": {"input_tokens": 77, "output_tokens": 0},
        },
    })]


def _text(text: str, chunk: int = 8) -> list[bytes]:
    out = [sse("content_block_start", {
        "type": "content_block_start", "index": 0,
        "content_block": {"type": "text", "text": ""},
    })]
    for i in range(0, len(text), chunk):
        out.append(sse("content_block_delta", {
            "type": "content_block_delta", "index": 0,
            "delta": {"type": "text_delta", "text": text[i:i + chunk]},
        }))
    out.append(sse("content_block_stop", {"type": "content_block_stop", "index": 0}))
    return out


def _finish(stop_reason: str, out_tokens: int) -> list[bytes]:
    return [
        sse("message_delta", {
            "type": "message_delta",
            "delta": {"stop_reason": stop_reason, "stop_sequence": None},
            "usage": {"output_tokens": out_tokens},
        }),
        sse("message_stop", {"type": "message_stop"}),
    ]


async def _scenario_text() -> AsyncIterator[bytes]:
    for f in _start():
        yield f
    for f in _text("我看得到你的屏幕。"):
        yield f
    for f in _finish("end_turn", 9):
        yield f


async def _scenario_tool() -> AsyncIterator[bytes]:
    """带工具的一轮：text 块 + tool_use 块，stop_reason=tool_use。

    `capture_screen` 的参数必须严格符合客户端内置的 JSON Schema
    （Diandian.Windows.Core.dll 里的字面量）：

        {"type":"object",
         "properties":{"source":{"enum":["current","history"]},
                       "purpose":{"enum":["visible_content","pointing",
                                          "course_verification","cross_screen_context",
                                          "historical_review"]},
                       "scope":{"enum":["cursor_screen","all_screens"]},
                       "entry_id":{"type":"integer","minimum":1},
                       "screenshot_index":{"type":"integer","minimum":1}},
         "required":["source","purpose"],
         "additionalProperties":false}

    注意 additionalProperties=false：多传一个 `reason` 都会被判「截图参数无效」，
    客户端把该轮标记为 Rejected、images=0，然后带着 is_error 的 tool_result 回来。
    之前就是这么踩的。
    """
    for f in _start():
        yield f
    for f in _text("我来截个屏。"):
        yield f
    yield sse("content_block_start", {
        "type": "content_block_start", "index": 1,
        "content_block": {"type": "tool_use", "id": "toolu_fake",
                          "name": "capture_screen", "input": {}},
    })
    # 必须用 input_json_delta 拼出完整参数（content_block_start 里的 input 会被丢弃）
    for part in ('{"source":', '"current","purpose":', '"visible_content"}'):
        yield sse("content_block_delta", {
            "type": "content_block_delta", "index": 1,
            "delta": {"type": "input_json_delta", "partial_json": part},
        })
    yield sse("content_block_stop", {"type": "content_block_stop", "index": 1})
    for f in _finish("tool_use", 23):
        yield f


async def _scenario_second_turn() -> AsyncIterator[bytes]:
    """第二轮：客户端已经回传 tool_result，这里正常收尾。"""
    for f in _start():
        yield f
    for f in _text("看到你的报销表了。"):
        yield f
    for f in _finish("end_turn", 11):
        yield f


@app.get("/healthz")
def healthz():
    return {"ok": True}


@app.post("/v1/messages")
async def messages(request: Request):
    body = await request.json()

    if RECORD:
        try:
            with open(RECORD, "w", encoding="utf-8") as f:
                json.dump({
                    "body": body,
                    "headers": dict(request.headers),
                }, f, ensure_ascii=False, indent=2)
        except Exception:
            pass

    model = str(body.get("model", ""))
    system = body.get("system") or ""
    if isinstance(system, list):          # Anthropic 允许 system 是块数组
        system = " ".join(str(b.get("text", "")) for b in system
                          if isinstance(b, dict))
    marker = f"{model} {system}"
    msgs = body.get("messages") or []
    has_tool_result = any(
        isinstance(m.get("content"), list)
        and any(isinstance(b, dict) and b.get("type") == "tool_result"
                for b in m["content"])
        for m in msgs
    )

    # 场景选择：优先看 messages 里有没有 tool_result（真实回环），
    # 其次看 model/system 里的标记——注意 xiaoliu 会把 model 改写成
    # UPSTREAM_MODEL，所以标记要放在 system 里才稳。
    if "boom" in marker:
        return JSONResponse({"type": "error",
                             "error": {"type": "overloaded_error",
                                       "message": "fake upstream overloaded"}},
                            status_code=529)
    if has_tool_result:
        return StreamingResponse(_scenario_second_turn(), media_type="text/event-stream")
    if "SCENARIO:tool" in marker:
        return StreamingResponse(_scenario_tool(), media_type="text/event-stream")
    return StreamingResponse(_scenario_text(), media_type="text/event-stream")
