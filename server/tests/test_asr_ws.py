"""ASR WebSocket 端到端测试：真实语音 → WS → 识别结果。

协议（抓包实测）：
  up start → down ready → up <PCM 二进制>* → down partial* → up finish → down final

用 SAPI 合成一句已知的话当「用户说的」，逐块推过去，验证：
  1. 控制帧顺序对（ready → partial → final）
  2. partial 是**累积文本**（后一条是前一条的超集）
  3. final 与原文核心字命中率够高
  4. 边录边出字真的发生了（partial 不止一条）

用法：python tests/test_asr_ws.py --port 443
"""
from __future__ import annotations

import argparse
import asyncio
import json
import os
import ssl
import sys

import websockets

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

HOST = "api.botlearn.com.cn"
_FMT_16K_16BIT_MONO = 9


def synth(text: str) -> bytes:
    import comtypes.client as cc

    voice = cc.CreateObject("SAPI.SpVoice")
    try:
        for tok in voice.GetVoices():
            if "Chinese" in tok.GetDescription() or "Huihui" in tok.GetDescription():
                voice.Voice = tok
                break
    except Exception:                                    # noqa: BLE001
        pass
    ms = cc.CreateObject("SAPI.SpMemoryStream")
    ms.Format.Type = _FMT_16K_16BIT_MONO
    voice.AudioOutputStream = ms
    voice.Speak(text)
    return bytes(ms.GetData())


def ctx() -> ssl.SSLContext:
    c = ssl.create_default_context()
    c.check_hostname = False
    c.verify_mode = ssl.CERT_NONE
    return c


def server_asr_mode(port: int) -> str:
    """问服务器当前 ASR 后端。

    doubao（云端）只能整段识别，**不会产生 partial**；断言必须跟着后端变，
    否则启用云端后这个测试会永远「失败」。
    """
    import http.client
    try:
        conn = http.client.HTTPSConnection(HOST, port, context=ctx(), timeout=5)
        conn.request("GET", "/")
        data = json.loads(conn.getresponse().read().decode())
        conn.close()
        return str((data.get("asr") or {}).get("mode") or "")
    except Exception:                                    # noqa: BLE001
        return ""


async def run(port: int, text: str, chunk_ms: int) -> int:
    mode = server_asr_mode(port)
    cloud = mode == "doubao"
    pcm = synth(text)
    dur = len(pcm) / 2 / 16000
    print(f"合成 {dur:.2f}s 音频（{len(pcm)} 字节）→ {text!r}")
    print(f"ASR 后端：{mode or '(查询失败，按本地处理)'}\n")

    url = f"wss://{HOST}:{port}/diandian/ws/asr/volcengine"
    partials: list[str] = []
    ctrl: list[str] = []
    final = None

    async with websockets.connect(url, ssl=ctx(), server_hostname=HOST,
                                  max_size=None, proxy=None) as ws:
        # 注意：ASR 端点**不主动发首帧**，是先等客户端 `start` 再回 `ready`。
        # 这里不能先 recv，否则会一直阻塞（踩过一次）。
        await ws.send(json.dumps({"type": "start", "sample_rate": 16000,
                                  "language_hints": ["zh"], "keywords": []}))

        async def reader():
            nonlocal final
            while True:
                try:
                    msg = await asyncio.wait_for(ws.recv(), timeout=40)
                except (asyncio.TimeoutError, Exception):  # noqa: BLE001
                    return
                if isinstance(msg, bytes):
                    continue
                o = json.loads(msg)
                t = o.get("type")
                if t not in ctrl:
                    ctrl.append(t)
                if t == "partial":
                    partials.append(o.get("text") or "")
                elif t == "final":
                    final = o.get("text") or ""
                    return

        task = asyncio.create_task(reader())

        # 按 40ms 一块推，模拟客户端实时上行
        step = int(16000 * 2 * chunk_ms / 1000)
        t0 = asyncio.get_event_loop().time()
        for i in range(0, len(pcm), step):
            await ws.send(pcm[i:i + step])
            await asyncio.sleep(chunk_ms / 1000)
        print(f"  上行完成，用时 {(asyncio.get_event_loop().time() - t0) * 1000:.0f}ms")

        await ws.send(json.dumps({"type": "finish"}))
        try:
            await asyncio.wait_for(task, timeout=60)
        except asyncio.TimeoutError:
            task.cancel()

    print(f"\n  控制帧：{ctrl}")
    print(f"  partial（{len(partials)} 条）：")
    for p in partials[:12]:
        print(f"    {p!r}")
    print(f"  final：{final!r}")

    fails = 0

    def check(name: str, cond: bool, detail: str = "") -> None:
        nonlocal fails
        if not cond:
            fails += 1
        print(f"  [{'PASS' if cond else 'FAIL'}] {name}" + (f"  {detail}" if detail else ""))

    print()
    check("有 ready", "ready" in ctrl)
    check("有 final", final is not None)
    if cloud:
        # 云端 ASR 吃整段文件，做不了流式。这是已知限制，不是回归。
        check("云端模式不产生 partial（设计如此）", len(partials) == 0,
              f"{len(partials)} 条")
    else:
        check("边录边出字（partial ≥ 2 条）", len(partials) >= 2, f"{len(partials)} 条")
    if not cloud and len(partials) >= 2:
        # 不要求严格单调：滚动重解码会修正前面的字（例如先听到「帮我看」
        # 后面改成「帮我看看」），长度偶尔会回退。这里只检查整体在变长。
        check("partial 整体递增（末条 ≥ 首条）",
              len(partials[-1]) >= len(partials[0]),
              f"{len(partials[0])} → {len(partials[-1])} 字")
        grew = sum(1 for i in range(len(partials) - 1)
                   if len(partials[i + 1]) > len(partials[i]))
        check("多数 partial 在变长", grew >= (len(partials) - 1) * 0.6,
              f"{grew}/{len(partials) - 1} 次增长")
    if final:
        core = [c for c in text if "\u4e00" <= c <= "\u9fff"]
        hit = sum(1 for c in core if c in final)
        ratio = hit / len(core) if core else 0
        check("final 命中率 ≥ 60%", ratio >= 0.6, f"{hit}/{len(core)} = {ratio:.0%}")
        if partials:
            # 最后一条 partial 是「说到一半」的结果，final 是整段的结果，
            # 所以 final 只会更完整（长度不减），不要求相等。
            check("final 不比最后一条 partial 短", len(final) >= len(partials[-1]),
                  f"{len(partials[-1])} → {len(final)} 字")

    print("\n" + ("全部通过" if fails == 0 else f"{fails} 项失败"))
    return 0 if fails == 0 else 1


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=443)
    ap.add_argument("--text", default="帮我看看屏幕上写了什么")
    ap.add_argument("--chunk-ms", type=int, default=40)
    a = ap.parse_args()
    return asyncio.run(run(a.port, a.text, a.chunk_ms))


if __name__ == "__main__":
    sys.exit(main())
