"""xiaoliu 服务器端 —— 与点点逐字段线协议兼容。

路由前缀照抄抓包结果：`api.botlearn.com.cn/diandian/*`。
把 hosts 指向本机后，点点可以直接连上来（见 README 的接入章节）。

启动：
    uvicorn app.main:app --host 0.0.0.0 --port 8787
"""
from __future__ import annotations

import asyncio
import json
import logging
import os
import time
import urllib.parse
import uuid
from typing import Any

from fastapi import FastAPI, Request, WebSocket, WebSocketDisconnect
from fastapi.responses import JSONResponse, RedirectResponse, Response, StreamingResponse

from . import (asr, asr_doubao, asr_qwen3, ocr, paddle_ocr, protocol, stubs,
               tts,
               upstream, vision)
from .config import settings

log = logging.getLogger("xiaoliu")
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)-5s %(message)s",
    datefmt="%H:%M:%S",
)

app = FastAPI(title="xiaoliu server", version="0.1.0")

# 预热本地 OCR。PaddleOCR-VL 加载权重约 3-5 秒，如果等第一次截图才加载，
# 用户会觉得「点了截图卡住了」。这里在启动时后台加载，不阻塞服务起来。
paddle_ocr.warmup()


# ------------------------------------------------------------------ 工具

def _no_store(payload: Any, status: int = 200) -> JSONResponse:
    return JSONResponse(payload, status_code=status,
                        headers={"Cache-Control": "no-store"})


def _auth_ok(request: Request) -> bool:
    if not settings.require_auth:
        return True
    return bool(request.headers.get("authorization"))


def _unauthorized() -> JSONResponse:
    return JSONResponse({"error": "Unauthorized"}, status_code=401,
                        headers={"WWW-Authenticate": "Bearer"})


@app.api_route("/", methods=["GET", "HEAD"])
def index() -> dict[str, Any]:
    problems = settings.check()
    return {
        "service": "xiaoliu",
        "protocol": "diandian-compatible (Anthropic Messages SSE)",
        "mock_mode": settings.mock_mode,
        "upstream": settings.upstream_base_url or None,
        "model": settings.upstream_model,
        # 语音链路的状态放在这里，一眼能看出「是不是还在用桩文本」
        "asr": {"mode": settings.asr_backend,
                "detail": asr.status(),
                "doubao": asr_doubao.status(),
                "qwen3": asr_qwen3.status()},
        "tts": tts.status(),
        "vision": {"mode": settings.vision_mode,
                   "ocr": ocr.available(),
                   "ocr_backend": settings.ocr_backend,
                   "paddle": paddle_ocr.status(),
                   "windows_ocr": ocr._windows_available(),
                   "vl": vision.status()},
        "ready": not problems,
        "problems": problems,
    }


# ------------------------------------------------------------ /proxy/*

@app.get("/diandian/proxy/healthz")
def healthz(request: Request):
    if not _auth_ok(request):
        return _unauthorized()
    return _no_store(stubs.healthz())


@app.get("/diandian/proxy/capabilities")
def capabilities(request: Request):
    if not _auth_ok(request):
        return _unauthorized()
    return _no_store(stubs.capabilities())


@app.post("/diandian/proxy/chat")
async def chat(request: Request):
    """核心端点：Anthropic Messages SSE。

    客户端按 `request.Tools != null` 二选一：
      - 无工具 → SseTextStreamDecoder，读 text_delta 增量
      - 有工具 → NativeAgentStreamDecoder，读完整快照
    两者读的是同一份字节流，所以这里只需要输出规范 SSE。
    """
    if not _auth_ok(request):
        return _unauthorized()

    try:
        body = await request.json()
    except Exception:
        return _no_store({"error": {"code": "invalid_request"}}, 400)

    model = body.get("model") or settings.upstream_model
    # 客户端来源：点点和 xiaoliu 都走这个端点（ADR 0012 的双模式），
    # 不记的话日志里分不清是谁发的请求，排障时只能靠猜。
    client_build = request.headers.get("x-diandian-client-build", "").strip()
    log.info("chat 来源=%s 模型=%s", client_build or "未标注", model)
    if settings.log_bodies:
        log.info("chat %s", upstream.dumps(upstream.summarize(body)))
        # 工具回环的成败全在客户端回传的 tool_result 里，单独摘出来记
        for tr in upstream.tool_results(body):
            txt = tr.get("content")
            if isinstance(txt, list):
                txt = " ".join(str(b.get("text", "")) for b in txt
                               if isinstance(b, dict))
            log.info("  tool_result is_error=%s %s",
                     tr.get("is_error"), str(txt)[:400])
    upstream.dump_body(body)

    headers = {
        "Content-Type": "text/event-stream; charset=utf-8",
        "Cache-Control": "no-store",
        "Connection": "keep-alive",
        "X-Accel-Buffering": "no",
    }

    if settings.mock_mode:
        return StreamingResponse(
            protocol.simple_reply(model, _mock_text(body)), headers=headers)

    async def gen():
        try:
            async for chunk in upstream.stream_chat(body):
                yield chunk
        except upstream.UpstreamError as exc:
            log.warning("upstream error %s: %s", exc.status, exc.detail[:200])
            for frame in protocol.error_stream(model, exc.detail[:300]):
                yield frame
        except Exception as exc:                      # noqa: BLE001
            log.exception("upstream crashed")
            for frame in protocol.error_stream(model, str(exc)[:300]):
                yield frame

    return StreamingResponse(gen(), headers=headers)


def _mock_text(body: dict[str, Any]) -> str:
    """mock 模式：把收到的结构回显出来，方便肉眼确认请求确实到位。"""
    s = upstream.summarize(body)
    tools = ", ".join(s["tool_names"]) or "无"
    return (
        f"mock 模式已收到请求。模型 {s['model']}，"
        f"{s['messages']} 条消息，内容块 {s['blocks']}，"
        f"system {s['system_chars']} 字，工具 {s['tools']} 个（{tools}）。"
    )


@app.post("/diandian/proxy/credits/settlements")
async def settlements(request: Request):
    """结算回执。

    抓包里真实服务端返回 404 `credits_write_rejected`，但那是「写入被拒」的错误分支。
    客户端拿到 404 后会在 `CreditSettlementResponse.Validate` 里解析失败，最终兜成
    「额度服务暂不可用，请稍后重试。」。自建服务器没有真实计费，直接回一份合法回执。
    """
    try:
        body = await request.json()
    except Exception:                                  # noqa: BLE001
        body = {}
    interaction_id = stubs.find_key(body, "interactionId")
    if settings.log_bodies:
        log.info("settlement <- %s", upstream.dumps(body)[:300])
    if not interaction_id:
        # 找不到就退回抓包里的错误形状，让客户端按它自己的降级逻辑处理
        return _no_store({"error": {"code": "credits_write_rejected"}}, 404)
    return _no_store(stubs.settlement(interaction_id))


# --------------------------------------------------------------- /v1/*

@app.get("/diandian/v1/credits")
def v1_credits(request: Request):
    if not _auth_ok(request):
        return _unauthorized()
    return _no_store(stubs.credits())


@app.post("/diandian/v1/credits/quotes")
async def v1_quotes(request: Request):
    if not _auth_ok(request):
        return _unauthorized()
    try:
        body = await request.json()
    except Exception:
        body = {}
    return _no_store(stubs.quote(model=body.get("model") or "aliyun.qwen3.8-flash",
                                 tier=body.get("tier") or "standard"))


@app.get("/diandian/v1/credits/transactions")
def v1_transactions(request: Request):
    if not _auth_ok(request):
        return _unauthorized()
    return _no_store(stubs.transactions())


@app.get("/diandian/v1/activation")
def v1_activation(request: Request):
    if not _auth_ok(request):
        return _unauthorized()
    return _no_store(stubs.activation())


@app.get("/diandian/v1/account/lifecycle")
def v1_lifecycle(request: Request):
    if not _auth_ok(request):
        return _unauthorized()
    return _no_store(stubs.lifecycle())


@app.get("/diandian/v1/account/profile")
def v1_profile(request: Request):
    if not _auth_ok(request):
        return _unauthorized()
    return _no_store(stubs.profile())


@app.get("/diandian/v1/public/runtime-parameters")
def v1_runtime_params():
    return _no_store(stubs.runtime_parameters())


@app.get("/diandian/v1/payments/orders")
def v1_pay_orders(request: Request):
    if not _auth_ok(request):
        return _unauthorized()
    return _no_store(stubs.payments_orders())


@app.get("/diandian/v1/payments/credits")
def v1_pay_credits(request: Request):
    if not _auth_ok(request):
        return _unauthorized()
    return _no_store(stubs.payments_credits())


@app.get("/diandian/v1/payments/products")
def v1_pay_products(request: Request):
    if not _auth_ok(request):
        return _unauthorized()
    return _no_store(stubs.payments_products())


@app.get("/diandian/v1/payments/capabilities")
def v1_pay_caps(request: Request):
    if not _auth_ok(request):
        return _unauthorized()
    return _no_store(stubs.payments_capabilities())


@app.post("/diandian/v1/metrics/asr")
async def v1_metrics_asr(request: Request):
    # 遥测：收下就丢，客户端看到 200 就清本地队列
    return _no_store(stubs.metrics_ack())


@app.post("/diandian/v1/metrics/interactions/batches")
async def v1_metrics_interactions(request: Request):
    if settings.log_bodies:
        try:
            body = await request.json()
            n = len(body.get("interactions") or body.get("items") or [])
            log.info("telemetry %s interactions (discarded)", n)
        except Exception:
            pass
    return _no_store(stubs.metrics_ack())


# --------------------------------------------------------------- 语音

TTS_SAMPLE_RATE = 24000
ASR_SAMPLE_RATE = 16000


def _silence(ms: int, rate: int = TTS_SAMPLE_RATE) -> bytes:
    """16-bit 单声道静音。SAPI 不可用时的兜底。"""
    n = int(rate * ms / 1000)
    return b"\x00\x00" * n


async def _speak(ws: WebSocket, text: str, voice_id: str | None = None) -> int:
    """合成一句话并按 20ms 帧推给客户端，返回推了几帧。

    合成走线程池：SAPI 是同步阻塞的（首句约 300ms），放在事件循环里
    会把同一条 WS 上的心跳和其它连接一起卡住。

    **按实时节奏推**，不是一次性灌完。客户端的音频管道有自己的缓冲上限
    （它自己的日志里有 `buffer_overflow` 这个事件），4 秒的音频在 50ms 内
    塞进去大概率溢出丢帧。真实 TTS 引擎也是边合成边按时间轴吐字节。
    开头先抢 10 帧（200ms）把首声延迟压下来，之后严格跟时间轴走。
    """
    # 纯标点/空白（客户端会把「，」单独当一个 text 片段送来）合成出来是空音频，
    # 没必要走一遍合成再回退静音——直接跳过。
    if not text.strip(" \t\r\n，。、；：！？…—·,.!?;:()（）[]【】\"'“”‘’"):
        return 0

    pcm = await asyncio.to_thread(tts.synthesize, text, voice_id)
    if not pcm:
        # 合成不可用：退回静音，保证客户端拿到「等长音频 + finished」，
        # 否则它会以为 TTS 挂了，整轮回答都不出声。
        pcm = _silence(max(200, len(text) * 120))
        log.info("tts: 合成不可用，回退静音 %d 字节", len(pcm))

    chunks = tts.frames(pcm)
    lead = 10                                   # 先发的帧数，不等待
    t0 = time.monotonic()
    for i, frame in enumerate(chunks):
        await ws.send_bytes(frame)
        if i >= lead:
            due = t0 + (i - lead) * 0.02
            gap = due - time.monotonic()
            if gap > 0:
                await asyncio.sleep(gap)
    return len(chunks)


@app.websocket("/diandian/ws/tts/volcengine")
async def ws_tts(ws: WebSocket):
    """协议形状照抄抓包：
    down connected → up start → down ready → up text* → up finish → down finished
    二进制帧是裸 PCM（24 kHz / 16-bit / mono）。

    合成和读消息**必须并发**。原来的写法在推 4 秒音频期间读不到任何消息，
    客户端中途发 `cancel`（用户打断）会被压在缓冲里，等音频推完才被处理——
    那时候用户早就不想听了。所以这里用「队列 + 工作协程」：
    读循环只负责收消息，推流由 worker 干；cancel 直接掐掉 worker。
    """
    await ws.accept()
    log.info("tts ws: connected")
    await ws.send_text(json.dumps({"type": "connected"}))

    queue: asyncio.Queue[tuple[str, str]] = asyncio.Queue()
    spoken = 0

    # 客户端是**按碎片**流式送文本的：「可以看到」「，」「你现在」「屏幕上」……
    # 如果每个碎片都单独合成，每段都是独立的「一句话」——各自有起落的语调，
    # 段间还夹着静音，听起来一顿一顿的（实测那次 23 个碎片、2179 帧）。
    # 所以先攒，攒到句子/分句边界再合成。
    #
    # 阈值是首声延迟和自然度之间的取舍：太小就退回逐片合成，太大就要等
    # 模型吐完一整句才出声。10-28 字大致对应中文一个自然分句。
    buf = ""
    # 点点在 start 帧里带 voice_id（形如 volcengine.vivi-2.0）。SAPI/edge 用不上，
    # 但 doubao 后端要靠它选 speaker，所以整轮会话记住它。
    voice = ""
    _SENT_END = "。！？!?…\n"
    _CLAUSE = "，,、；;：:"
    BUF_MIN = 10          # 分句处至少攒这么多字才断
    BUF_MAX = 28          # 太长的句子强制断，别让首声等太久

    def _flush_ready(s: str) -> bool:
        core = s.strip()
        if not core:
            return False
        if core[-1] in _SENT_END:
            return True
        if len(core) >= BUF_MAX:
            return True
        return len(core) >= BUF_MIN and core[-1] in _CLAUSE

    async def worker():
        nonlocal spoken
        while True:
            kind, payload = await queue.get()
            try:
                if kind == "text":
                    spoken += await _speak(ws, payload, voice)
                elif kind == "finish":
                    log.info("tts -> finished（共 %d 帧 PCM）", spoken)
                    await ws.send_text(json.dumps({"type": "finished"}))
                    spoken = 0
                elif kind == "cancel":
                    log.info("tts -> finished（cancel，已发 %d 帧）", spoken)
                    await ws.send_text(json.dumps({"type": "finished"}))
                    spoken = 0
            finally:
                queue.task_done()

    task = asyncio.create_task(worker())
    try:
        while True:
            raw = await ws.receive_text()
            msg = json.loads(raw)
            t = msg.get("type")
            log.info("tts <- %s %s", t,
                     {k: v for k, v in msg.items() if k != "type"})
            if t == "start":
                buf = ""
                voice = str(msg.get("voice_id") or "")
                await ws.send_text(json.dumps({"type": "ready"}))
            elif t == "text":
                buf += msg.get("text") or ""
                if _flush_ready(buf):
                    log.info("tts 攒句合成（%d 字）：%s", len(buf), buf)
                    await queue.put(("text", buf))
                    buf = ""
            elif t == "finish":
                if buf.strip():
                    log.info("tts 收尾合成（%d 字）：%s", len(buf), buf)
                    await queue.put(("text", buf))
                    buf = ""
                # 排队而不是立刻回 finished：前面还有没念完的句子
                await queue.put(("finish", ""))
            elif t == "cancel":
                # 打断：掐掉正在念的，丢掉还没念的
                buf = ""
                task.cancel()
                try:
                    await task
                except asyncio.CancelledError:
                    pass
                while not queue.empty():
                    queue.get_nowait()
                    queue.task_done()
                spoken = 0
                task = asyncio.create_task(worker())
                await queue.put(("cancel", ""))
    except WebSocketDisconnect:
        log.info("tts ws: 客户端断开（已发 %d 帧）", spoken)
    except Exception as exc:                          # noqa: BLE001
        log.warning("tts ws: %s", exc)
    finally:
        task.cancel()
        try:
            await task
        except (asyncio.CancelledError, Exception):   # noqa: BLE001
            pass
        try:
            await ws.close()
        except Exception:                             # noqa: BLE001
            pass


@app.websocket("/diandian/ws/asr/volcengine")
async def ws_asr(ws: WebSocket):
    """协议形状照抄抓包：
    up start → down ready → down partial* → up finish → down final
    `partial` 是**累积文本**，每次都重发全句。
    二进制帧是裸 PCM（16 kHz / 16-bit / mono）。

    SenseVoice 是离线模型（不是流式），所以「边录边出字」靠**滚动重解码**：
    每 ~1 秒把已累积的音频整体识别一次，当作 partial 发出去。
    4 秒音频本机约 0.3 秒，代价可接受；用户能边说话边看到字，体验差别很大。
    设 ASR_PARTIAL=0 可关掉。
    """
    await ws.accept()
    log.info("asr ws: connected")
    started = False
    audio = bytearray()
    sample_rate = 16000
    decoding = False
    stop_partial = asyncio.Event()

    async def partial_loop() -> None:
        """滚动重解码，把中间结果当 partial 发。"""
        nonlocal decoding
        last_n = 0
        # 间隔定 0.6s：句子通常 2-4 秒，1 秒间隔只够出 1-2 次，
        # 「边录边出字」就感觉不到。单次解码 50-100ms，CPU 占用约 15%，可接受。
        while not stop_partial.is_set():
            try:
                await asyncio.wait_for(stop_partial.wait(), timeout=0.6)
                return
            except asyncio.TimeoutError:
                pass
            if decoding or len(audio) <= last_n or len(audio) < sample_rate:  # 不足 0.5s
                continue
            last_n = len(audio)
            decoding = True
            try:
                text = await asyncio.to_thread(asr.transcribe, bytes(audio), sample_rate)
            finally:
                decoding = False
            if text:
                try:
                    await ws.send_text(json.dumps(
                        {"type": "partial", "text": text}, ensure_ascii=False))
                except Exception:                        # noqa: BLE001
                    return

    partial_task: asyncio.Task | None = None
    try:
        while True:
            msg = await ws.receive()
            if msg.get("type") == "websocket.disconnect":
                break
            if msg.get("text"):
                data = json.loads(msg["text"])
                t = data.get("type")
                log.info("asr <- %s %s", t,
                         {k: v for k, v in data.items() if k != "type"})
                if t == "start":
                    started = True
                    audio.clear()
                    sr = data.get("sample_rate")
                    if isinstance(sr, int) and 8000 <= sr <= 48000:
                        sample_rate = sr
                    stop_partial.clear()
                    # 云端/整段识别的后端没有 partial。qwen3 虽然快，
                    # 但每次 partial 都是一次 GPU 推理 + HTTP 往返，
                    # 先按完才出字，验证 GPU 占用后再考虑开滚动重解码。
                    if (settings.asr_backend not in ("doubao", "qwen3")
                            and os.environ.get("ASR_PARTIAL", "1") not in ("0", "false")):
                        partial_task = asyncio.create_task(partial_loop())
                    await ws.send_text(json.dumps({"type": "ready"}))
                elif t in ("finish", "cancel"):
                    stop_partial.set()
                    if partial_task:
                        try:
                            await asyncio.wait_for(partial_task, timeout=2.0)
                        except Exception:                # noqa: BLE001
                            partial_task.cancel()
                        partial_task = None

                    pcm = bytes(audio)
                    dur = len(pcm) / 2 / sample_rate
                    text = ""
                    used_stub = False
                    silent = False
                    if t == "finish" and pcm:
                        # 落盘 + 体检：用户报「按住 Ctrl+Alt 没反应」时，
                        # 光看「识别出 N 个字」分不清是模型不准还是麦克风没声。
                        asr.dump(pcm, sample_rate)
                        # 体检是纯统计，不解码。云端模式也先拦静音：
                        # 没有语音就别送去识别（浪费额度，还可能幻觉）。
                        stats = asr.analyze(pcm)
                        stats["dur"] = dur
                        silent = ((settings.asr_backend == "doubao" or asr.available())
                                  and asr.is_silent(stats))
                        if silent:
                            # 麦克风在送数据，但这段里没人说话。
                            # 必须和「识别失败」分开处理：塞「看看我的屏幕」会触发截图，
                            # 用户根本不知道是麦克风没有声音。
                            log.info("asr: 静音——本轮没有有效语音"
                                     "（麦克风可能未接入/被虚拟声卡占用）")
                        elif settings.asr_backend == "qwen3":
                            text = await asyncio.to_thread(
                                asr_qwen3.transcribe, pcm, sample_rate)
                            if not text and asr.available():
                                log.info("asr: qwen3 空结果/失败，回退本地 SenseVoice")
                                text, stats = await asyncio.to_thread(
                                    asr.transcribe_ex, pcm, sample_rate)
                            log.info("asr: %.2fs 音频（%d 字节）→ %r ｜ rms=%.4f peak=%.3f 有声=%.1f%%",
                                     dur, len(pcm), text, stats.get("rms", 0),
                                     stats.get("peak", 0), stats.get("voiced", 0) * 100)
                        elif settings.asr_backend == "doubao":
                            text = await asyncio.to_thread(
                                asr_doubao.transcribe, pcm, sample_rate)
                            if not text and asr.available():
                                log.info("asr: 云端空结果/失败，回退本地 SenseVoice")
                                text, stats = await asyncio.to_thread(
                                    asr.transcribe_ex, pcm, sample_rate)
                            log.info("asr: %.2fs 音频（%d 字节）→ %r ｜ rms=%.4f peak=%.3f 有声=%.1f%%",
                                     dur, len(pcm), text, stats.get("rms", 0),
                                     stats.get("peak", 0), stats.get("voiced", 0) * 100)
                        else:
                            text, stats = await asyncio.to_thread(
                                asr.transcribe_ex, pcm, sample_rate)
                            log.info("asr: %.2fs 音频（%d 字节）→ %r ｜ rms=%.4f peak=%.3f 有声=%.1f%%",
                                     dur, len(pcm), text, stats.get("rms", 0),
                                     stats.get("peak", 0), stats.get("voiced", 0) * 100)
                    if silent:
                        text = settings.asr_silence_text
                        used_stub = bool(text)
                    elif not text:
                        # 没识别出来（模型缺失/取消）：退回可配置桩文本，
                        # 否则点点会认为「什么都没说」直接结束这一轮。
                        text = settings.asr_stub_text if t == "finish" else ""
                        used_stub = bool(text)
                        if text:
                            log.info("asr: 回退桩文本 %r", text)

                    if text and used_stub:
                        # 只有回退桩文本时才逐字「打出来」——真实识别路径已经有
                        # 滚动 partial 提供了实时反馈，这里再逐字重放会让界面上的
                        # 文字从「帮我看看。」倒退成「帮」，视觉上是闪烁。
                        for i in range(1, len(text) + 1):
                            await ws.send_text(json.dumps(
                                {"type": "partial", "text": text[:i]},
                                ensure_ascii=False))
                            await asyncio.sleep(0.015)
                    await ws.send_text(json.dumps(
                        {"type": "final", "text": text}, ensure_ascii=False))
                    log.info("asr -> final %r", text)
                    # 不 break：保持连接，等客户端断开（真实服务端行为）
                    audio.clear()
                    continue
            elif msg.get("bytes") is not None and started:
                audio.extend(msg["bytes"])
    except WebSocketDisconnect:
        log.info("asr ws: 客户端断开")
    except Exception as exc:                          # noqa: BLE001
        log.warning("asr ws: %s", exc)
    finally:
        stop_partial.set()
        if partial_task:
            partial_task.cancel()
        try:
            await ws.close()
        except Exception:
            pass


@app.post("/diandian/transcribe-token")
def transcribe_token():
    # 客户端用这个拿一次性语音凭据；自建时直接给个占位
    return _no_store({"token": "selfhost", "expires_in": 3600})


@app.post("/diandian/tts")
async def tts_http_stub(request: Request):
    # 注意：函数名不能叫 `tts`，否则会遮蔽上面 `from . import tts` 的模块，
    # 导致 index() 里 `tts.status()` 报 'function' object has no attribute 'status'。
    return Response(_silence(300), media_type="audio/L16",
                    headers={"Cache-Control": "no-store"})


# --------------------------------------------------------------- OIDC

@app.get("/oidc/auth")
async def oidc_auth(request: Request):
    """授权码端点。

    点点在本地会话失效时会打开内嵌 WebView 走这条路：

        GET /oidc/auth?response_type=code&client_id=diandian-windows
            &redirect_uri=ai.botlearn.diandian.windows%3a%2f%2foauth%2fcallback
            &scope=openid+offline_access+proxy%3ainvoke+...
            &state=...&code_challenge=...&code_challenge_method=S256

    自建服务器不需要真的让人输密码，直接 302 回自定义 scheme 并带上 code，
    WebView 会拦截这次跳转，把 code 交给客户端，客户端再来 /oidc/token 换令牌。

    PKCE 的 code_verifier 不校验——自用场景没必要，客户端也感知不到。
    """
    q = request.query_params
    redirect_uri = q.get("redirect_uri") or "ai.botlearn.diandian.windows://oauth/callback"
    state = q.get("state") or ""
    code = "selfhost-" + uuid.uuid4().hex
    sep = "&" if "?" in redirect_uri else "?"
    location = f"{redirect_uri}{sep}code={code}&state={state}"
    log.info("oidc/auth -> 302 回自定义 scheme（state=%s）", state[:12])
    return RedirectResponse(location, status_code=302)


@app.get("/favicon.ico")
def favicon():
    return Response(status_code=204)


@app.post("/oidc/token")
async def oidc_token(request: Request):
    """令牌端点。同时支持授权码与刷新两种 grant。

    客户端 access_token 只有 900 秒，过期会来这里刷新。自建时直接发一个
    长期有效的假 token——客户端只把它当不透明串回传，不校验内容。

    注意：不能用 `request.form()`，那需要 python-multipart 依赖，缺了就 500。
    这里手工解析 `application/x-www-form-urlencoded`，零依赖。
    """
    raw = (await request.body()).decode("utf-8", "replace")
    form = {k: v[0] for k, v in urllib.parse.parse_qs(raw).items()}
    grant = form.get("grant_type", "refresh_token")
    log.info("oidc/token grant_type=%s code=%s", grant,
             (form.get("code") or "-")[:16])
    return _no_store({
        "access_token": stubs.jwt(aud="botlearn-clicky-proxy", typ="at+jwt"),
        "expires_in": 86400,
        "id_token": stubs.jwt(scope="openid", aud="diandian-windows", typ="JWT"),
        "refresh_token": "selfhost-refresh-token",
        "scope": "openid offline_access proxy:invoke credits:read credits:use "
                 "payments:read payments:write",
        "token_type": "Bearer",
        "grant_type": grant,
    })


@app.get("/.well-known/openid-configuration")
def oidc_discovery():
    """有的客户端会先拉发现文档，给了更稳。"""
    base = "https://user.botlearn.com.cn"
    return _no_store({
        "issuer": base + "/oidc",
        "authorization_endpoint": base + "/oidc/auth",
        "token_endpoint": base + "/oidc/token",
        "response_types_supported": ["code"],
        "grant_types_supported": ["authorization_code", "refresh_token"],
        "code_challenge_methods_supported": ["S256"],
        "subject_types_supported": ["public"],
        "id_token_signing_alg_values_supported": ["ES256"],
        "scopes_supported": ["openid", "offline_access", "proxy:invoke",
                             "credits:read", "credits:use",
                             "payments:read", "payments:write"],
    })


# 自更新 feed。返回一个没有 <item> 的空 appcast —— 客户端据此判定「无更新」，
# 既不会弹升级，也不会去走 Ed25519 验签闸门。
_APPCAST = """<?xml version="1.0" encoding="utf-8"?>
<rss version="2.0" xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle">
  <channel>
    <title>xiaoliu</title>
    <link>https://api.botlearn.com.cn/diandian/appcast-windows.xml</link>
    <description>selfhost build, no updates published</description>
    <language>zh-CN</language>
  </channel>
</rss>
"""


@app.get("/diandian/appcast-windows.xml")
def appcast():
    return Response(_APPCAST, media_type="application/xml",
                    headers={"Cache-Control": "no-store"})
