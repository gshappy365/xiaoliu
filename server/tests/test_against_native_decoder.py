#!/usr/bin/env python3
"""用点点自己的 native 解码器验收服务器生成的 SSE。

这是本项目最强的兼容性证明：不是「我读懂了文档」，而是
「点点内部的解析器接受我发的字节」。

    python tests/test_against_native_decoder.py

依赖：Diandian 安装目录下的 pipeline_brain_ffi.dll（可用 DIANDIAN_DLL 覆盖）
"""
from __future__ import annotations

import ctypes
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from app import protocol  # noqa: E402

DLL = os.environ.get(
    "DIANDIAN_DLL",
    r"C:\Users\李平\AppData\Local\Programs\Diandian\pipeline_brain_ffi.dll",
)


class NativeStr(ctypes.Structure):
    """实测确认的返回值布局：{ptr, len}，16 字节。

    用 IntPtr 或 c_void_p 声明会崩溃——Win64 下 16 字节结构体按值返回走
    隐藏 sret 指针（RCX），会吃掉第一个实参。
    """
    _fields_ = [("ptr", ctypes.c_void_p), ("len", ctypes.c_size_t)]


class Decoder:
    def __init__(self, mode: str):
        lib = ctypes.WinDLL(DLL)
        self.lib = lib
        self.mode = mode
        pfx = "sse_decoder" if mode == "sse" else "agent_stream"

        self._new = getattr(lib, f"diandian_{pfx}_new")
        self._new.restype = ctypes.c_int64
        self._new.argtypes = []
        self._free = getattr(lib, f"diandian_{pfx}_free")
        self._free.restype = None
        self._free.argtypes = [ctypes.c_int64]
        self._push = getattr(lib, f"diandian_{pfx}_push")
        self._push.restype = NativeStr
        self._push.argtypes = [ctypes.c_int64, ctypes.POINTER(ctypes.c_ubyte),
                               ctypes.c_size_t]
        lib.diandian_string_free.restype = None
        lib.diandian_string_free.argtypes = [NativeStr]

        self.h = self._new()
        assert self.h, "native decoder 创建失败"
        self.outputs: list = []

    def push(self, data: bytes):
        buf = (ctypes.c_ubyte * len(data)).from_buffer_copy(data)
        val = self._push(self.h, buf, len(data))
        try:
            raw = ctypes.string_at(val.ptr, val.len) if val.ptr else b""
        finally:
            self.lib.diandian_string_free(val)
        got = json.loads(raw.decode("utf-8"))
        self.outputs.append(got)
        return got

    @property
    def finished(self) -> bool:
        if self.mode != "sse":
            raise RuntimeError("只有 sse 解码器有 is_finished")
        self.lib.diandian_sse_decoder_is_finished.restype = ctypes.c_int
        self.lib.diandian_sse_decoder_is_finished.argtypes = [ctypes.c_int64]
        return bool(self.lib.diandian_sse_decoder_is_finished(self.h))

    def close(self):
        self._free(self.h)

    def __enter__(self):
        return self

    def __exit__(self, *a):
        self.close()


def feed(chunks, mode: str, split: int = 0):
    """把所有帧拼起来，按 split 字节切片喂（模拟 TCP 分片）。"""
    blob = b"".join(chunks)
    pieces = ([blob[i:i + split] for i in range(0, len(blob), split)]
              if split else [blob])
    with Decoder(mode) as d:
        results = [d.push(p) for p in pieces]
        return results, (d.finished if mode == "sse" else None)


# ------------------------------------------------------------------ 用例

def test_plain_text():
    """无工具路径：SSE 解码器应逐段吐出增量，并在 [DONE] 后 finished。"""
    chunks = list(protocol.simple_reply("m", "你好，我是小刘。", chunk_size=3))
    outs, finished = feed(chunks, "sse")

    pieces = [s for o in outs for s in o]
    assert "".join(pieces) == "你好，我是小刘。", f"增量拼接不符: {pieces}"
    assert finished is True, "data: [DONE] 之后 is_finished 应为 True"
    print(f"  ✓ 无工具路径：{len(pieces)} 个增量，拼接正确，finished=True")


def test_tool_use_agent():
    """有工具路径：agent 解码器应给出完整快照、拼好的 tool input。"""
    w = protocol.StreamWriter(model="m", input_tokens=42)
    chunks = list(w.start())
    chunks += list(w.thinking("先看看屏幕"))
    chunks += list(w.thinking_signature("sig-xyz"))
    chunks += list(w.text("我来截个屏。"))
    chunks += list(w.tool_use("toolu_1", "capture_screen", {"reason": "看看你在做什么"}))
    chunks += list(w.finish(stop_reason="tool_use", output_tokens=31))

    outs, _ = feed(chunks, "agent")
    last = outs[-1]

    assert last["text"] == "我来截个屏。", f"text 应为累积全文，实得 {last['text']!r}"
    assert last["finished"] is True, "message_stop 之后 finished 应为 True"
    assert last["stopReason"] == "tool_use", last["stopReason"]
    assert last["error"] is None, last["error"]
    assert last["usage"] == {"input_tokens": 42, "output_tokens": 31}, last["usage"]

    blocks = last["content"]
    assert [b["type"] for b in blocks] == ["thinking", "text", "tool_use"], blocks
    assert blocks[0]["signature"] == "sig-xyz"
    assert blocks[2]["id"] == "toolu_1"
    assert blocks[2]["name"] == "capture_screen"
    assert blocks[2]["input"] == {"reason": "看看你在做什么"}, blocks[2]["input"]
    print(f"  ✓ 有工具路径：3 个内容块，tool input 完整还原，stopReason=tool_use")


def test_tcp_fragmentation():
    """任意 TCP 分片都应正确缓冲（ASCII 文本）。"""
    chunks = list(protocol.simple_reply("m", "fragment ok", chunk_size=5))
    for split in (1, 3, 7, 13):
        outs, finished = feed(chunks, "sse", split=split)
        text = "".join(s for o in outs for s in o)
        assert text == "fragment ok", f"split={split} 得到 {text!r}"
        assert finished is True, f"split={split} 未 finished"
    print("  ✓ 分片：1/3/7/13 字节切片全部还原正确（ASCII）")


def test_utf8_boundary_is_lossy():
    """已知缺陷：解码器每次 push 独立做 UTF-8 解码，不缓冲半个码点。

    一个汉字被 TCP 切开就变成 U+FFFD。这是客户端自身的限制——点点调的是
    同一个函数、喂的是同一个 ReadAsync 结果，所以真实点点也有这个毛病。

    服务器能做的只是把每帧控制在一个报文内，降低被切开的概率。
    这里把行为固定下来，避免以后误以为是服务器 bug。
    """
    frame = protocol.sse("content_block_delta", {
        "type": "content_block_delta", "index": 0,
        "delta": {"type": "text_delta", "text": "中文"},
    })
    whole, _ = feed([frame], "sse")
    assert "".join(s for o in whole for s in o) == "中文"

    broken, _ = feed([frame], "sse", split=1)
    text = "".join(s for o in broken for s in o)
    assert "\ufffd" in text, "预期出现替换字符，说明行为已变化，需重新评估"
    print(f"  ! UTF-8 跨片：已知有损（{text!r}），服务器应把每帧控制在一个报文内")


def test_agent_requires_ordering():
    """agent 解码器对事件顺序严格：缺 message_start 会判 malformed。

    这条决定了服务器**必须**发完整序列，不能省。
    """
    lone = protocol.sse("content_block_delta", {
        "type": "content_block_delta", "index": 0,
        "delta": {"type": "text_delta", "text": "hi"},
    })
    outs, _ = feed([lone], "agent")
    assert outs[-1]["error"] == "malformed", outs[-1]
    print("  ✓ 顺序约束：缺 message_start 被判 malformed（所以必须发完整序列）")


def test_done_required():
    """不发 [DONE] 时 is_finished 保持 False —— 客户端会靠 EOF 兜底。"""
    chunks = list(protocol.simple_reply("m", "abc", chunk_size=1))
    assert chunks[-1] == protocol.DONE_FRAME
    no_done = chunks[:-1]
    _, finished = feed(no_done, "sse")
    assert finished is False, "没有 [DONE] 却 finished 了"
    print("  ✓ 结束帧：无 [DONE] 时 finished=False（证明该帧必需）")


def main() -> int:
    if not os.path.exists(DLL):
        print(f"跳过：找不到 {DLL}（设 DIANDIAN_DLL 指向它）")
        return 0
    print(f"用 {os.path.basename(DLL)} 验收协议兼容性\n")
    tests = [test_plain_text, test_tool_use_agent, test_tcp_fragmentation,
             test_utf8_boundary_is_lossy, test_agent_requires_ordering,
             test_done_required]
    for t in tests:
        print(f"· {t.__name__}")
        t()
    print(f"\n全部 {len(tests)} 项通过 —— 服务器输出被点点原生解码器接受。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
