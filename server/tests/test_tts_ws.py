#!/usr/bin/env python3
"""验收 TTS：连上自建服务器的 WS，说一句话，把收到的 PCM 存成 WAV 并量一下。

用法：
    python tests/test_tts_ws.py                       # 默认 443
    python tests/test_tts_ws.py --port 8443 --text 你好

判据（不靠耳朵）：
  - 收到 `connected` / `ready` / `finished` 三个控制帧
  - PCM 字节数是偶数，且时长与字数成正比
  - 峰值远大于 0（说明不是静音）
"""
from __future__ import annotations

import argparse
import asyncio
import json
import os
import ssl
import struct
import sys
import wave

import websockets

HERE = os.path.dirname(os.path.abspath(__file__))
CERT = os.path.join(os.path.dirname(HERE), "probe", "certs", "server-chain.crt")
HOST = "api.botlearn.com.cn"


def _ctx() -> ssl.SSLContext:
    ctx = ssl.create_default_context()
    if os.path.exists(CERT):
        ctx.load_verify_locations(CERT)
    else:
        ctx.check_hostname = False
        ctx.verify_mode = ssl.CERT_NONE
    # 根证书是自签的，缺 SKI，Python 3.13 的严格模式会拒
    ctx.verify_flags &= ~ssl.VERIFY_X509_STRICT
    return ctx


async def run_cancel(port: int, text: str) -> int:
    """打断测试：推流途中发 cancel，必须在很短时间内收到 finished。

    这一项是专门防回归的——之前推流期间读不到消息，cancel 会被压到音频
    推完之后才处理，用户按了打断却还要听完整段。
    """
    url = f"wss://{HOST}:{port}/diandian/ws/tts/volcengine"
    pcm = bytearray()
    ctrl: list[str] = []
    async with websockets.connect(url, ssl=_ctx(), server_hostname=HOST,
                                      proxy=None) as ws:
        async def drain_until(want: str, timeout: float = 30):
            while True:
                msg = await asyncio.wait_for(ws.recv(), timeout=timeout)
                if isinstance(msg, bytes):
                    pcm.extend(msg)
                    continue
                obj = json.loads(msg)
                ctrl.append(obj.get("type", "?"))
                if obj.get("type") == want:
                    return

        await drain_until("connected")
        await ws.send(json.dumps({"type": "start", "voice_id": "volcengine.xiaohe-2.0",
                                  "sample_rate": 24000, "reuse_connection": True}))
        await drain_until("ready")
        await ws.send(json.dumps({"type": "text", "text": text}))
        await asyncio.sleep(0.6)                    # 让它念一会儿
        t0 = asyncio.get_running_loop().time()
        await ws.send(json.dumps({"type": "cancel"}))
        await drain_until("finished", timeout=5)
        latency = asyncio.get_running_loop().time() - t0

    print(f"控制帧：{ctrl}")
    print(f"打断前已收 {len(pcm)} 字节，cancel→finished 用了 {latency * 1000:.0f}ms")
    ok = latency < 1.0 and "finished" in ctrl
    if not ok:
        print("  ✗ 打断不及时（应 < 1000ms）—— 推流期间大概率堵住了读循环")
    else:
        print("  ✓ 打断及时")
    return 0 if ok else 1


# 真实会话里客户端送来的碎片序列（2026-10-27 12:37 抓到的原文）
REAL_FRAGMENTS = [
    "可以看到", "，", "你现在", "屏幕上", "打开", "着", "一个报销", "表文件，内容",
    "应该是在 Excel", " 里或者表格文档里。你说", "看看", "屏幕之后", "我这边确实",
    "截到了这张表，就是还没有具体看里面的数字和填写情况。", " 你把", "光标放到",
    "表格里，或者直接", "说这张表是哪来的、你现在在做什么，我可以", "接着帮你看细节，比如哪栏填了、哪",
    "栏还没", "填、哪个", "单元格可能格式", "不对。",
]


async def run_fragments(port: int) -> int:
    """碎片流式测试：客户端是按 token 碎片送文本的，服务器必须攒句再合成。

    防的回归：早期实现每个碎片单独合成，23 个碎片 → 2179 帧（43.6s），
    每段都有独立的语调起落和段间静音，听起来一顿一顿。
    """
    url = f"wss://{HOST}:{port}/diandian/ws/tts/volcengine"
    pcm = bytearray()
    ctrl: list[str] = []
    async with websockets.connect(url, ssl=_ctx(), server_hostname=HOST,
                                  proxy=None) as ws:
        async def drain_until(want: str):
            while True:
                msg = await asyncio.wait_for(ws.recv(), timeout=60)
                if isinstance(msg, bytes):
                    pcm.extend(msg)
                    continue
                obj = json.loads(msg)
                ctrl.append(obj.get("type", "?"))
                if obj.get("type") == want:
                    return

        await drain_until("connected")
        await ws.send(json.dumps({"type": "start", "voice_id": "volcengine.xiaohe-2.0",
                                  "sample_rate": 24000, "reuse_connection": True}))
        await drain_until("ready")
        for frag in REAL_FRAGMENTS:
            await ws.send(json.dumps({"type": "text", "text": frag},
                                     ensure_ascii=False))
            await asyncio.sleep(0.02)           # 模拟模型边生成边送
        await ws.send(json.dumps({"type": "finish"}))
        await drain_until("finished")

    n = len(pcm) // 2
    secs = n / 24000
    text = "".join(REAL_FRAGMENTS)
    han = sum(1 for c in text if "\u4e00" <= c <= "\u9fff")
    per_char = secs / han if han else 0

    print(f"  碎片 {len(REAL_FRAGMENTS)} 个，正文 {han} 汉字")
    print(f"  PCM {len(pcm)} 字节 = {secs:.2f}s（{len(pcm) // 960} 帧）")
    print(f"  每字 {per_char * 1000:.0f}ms")

    ok = True
    for need in ("connected", "ready", "finished"):
        if need not in ctrl:
            print(f"  ✗ 缺控制帧 {need}")
            ok = False
    # 中文正常语速 4-5 字/秒 → 每字 200-250ms。加上句间停顿到 350ms 也合理。
    # 超过 450ms/字 说明还在逐片合成，静音和断句把时长撑起来了。
    if per_char > 0.45:
        print(f"  ✗ 每字 {per_char * 1000:.0f}ms 过长（>450ms），大概率还在逐片合成")
        ok = False
    if per_char < 0.15:
        print(f"  ✗ 每字 {per_char * 1000:.0f}ms 过短，可能吞字了")
        ok = False
    if ok:
        print("  ✓ 攒句合成生效")
    return 0 if ok else 1


async def run(port: int, text: str, out: str) -> int:
    url = f"wss://{HOST}:{port}/diandian/ws/tts/volcengine"
    pcm = bytearray()
    ctrl: list[str] = []
    async with websockets.connect(url, ssl=_ctx(), server_hostname=HOST,
                                      proxy=None) as ws:
        async def drain_until(want: str):
            """收到 want 类型的控制帧为止，中间的二进制帧都收进 pcm。"""
            while True:
                msg = await asyncio.wait_for(ws.recv(), timeout=30)
                if isinstance(msg, bytes):
                    pcm.extend(msg)
                    continue
                obj = json.loads(msg)
                ctrl.append(obj.get("type", "?"))
                if obj.get("type") == want:
                    return

        await drain_until("connected")
        await ws.send(json.dumps({"type": "start", "voice_id": "volcengine.xiaohe-2.0",
                                  "sample_rate": 24000, "reuse_connection": True}))
        await drain_until("ready")
        await ws.send(json.dumps({"type": "text", "text": text}))
        await ws.send(json.dumps({"type": "finish"}))
        await drain_until("finished")

    n = len(pcm) // 2
    vals = struct.unpack(f"<{n}h", bytes(pcm[:n * 2])) if n else ()
    peak = max((abs(v) for v in vals), default=0)
    rms = (sum(v * v for v in vals) / n) ** 0.5 if n else 0.0
    secs = n / 24000

    print(f"控制帧：{ctrl}")
    print(f"PCM：{len(pcm)} 字节 = {secs:.2f}s  ({len(pcm) // 960} 帧 @20ms)")
    print(f"峰值 {peak}/32767   RMS {rms:.0f}")

    ok = True
    for need in ("connected", "ready", "finished"):
        if need not in ctrl:
            print(f"  ✗ 缺控制帧 {need}")
            ok = False
    if len(pcm) % 2:
        print("  ✗ PCM 字节数不是偶数")
        ok = False
    if secs < 0.3:
        print("  ✗ 音频太短，几乎肯定是静音")
        ok = False
    if peak < 1000:
        print("  ✗ 峰值过低，不是有效语音")
        ok = False
    if ok:
        print("  ✓ 全部通过")

    if out and pcm:
        with wave.open(out, "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(24000)
            w.writeframes(bytes(pcm))
        print(f"已写 {out}（可以直接双击试听）")
    return 0 if ok else 1


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=443)
    ap.add_argument("--text", default="我来截个屏。看到你的报销表了。")
    ap.add_argument("--out", default=os.path.join(
        os.path.dirname(os.path.dirname(HERE)), "evidence", "tts-ws-capture.wav"))
    ap.add_argument("--cancel", action="store_true", help="只跑打断测试")
    ap.add_argument("--fragments", action="store_true", help="只跑碎片攒句测试")
    a = ap.parse_args()
    if a.cancel:
        return asyncio.run(run_cancel(a.port, a.text))
    if a.fragments:
        print("== 碎片攒句测试 ==")
        return asyncio.run(run_fragments(a.port))
    rc = asyncio.run(run(a.port, a.text, a.out))
    print("\n== 碎片攒句测试 ==")
    rc |= asyncio.run(run_fragments(a.port))
    print("\n== 打断测试 ==")
    rc |= asyncio.run(run_cancel(a.port, "这是一段比较长的话，用来验证打断是否及时，不能等它念完才响应。"))
    return rc


if __name__ == "__main__":
    sys.exit(main())
