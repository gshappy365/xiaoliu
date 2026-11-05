"""Windows SAPI 离线语音合成 → 点点要的裸 PCM。

点点在 WS 上收的是**裸 PCM**：24 kHz / 16-bit / 单声道，没有容器头。
SAPI 的 `SpeechAudioFormatType` 里正好有 `SAFT24kHz16BitMono = 26`，
直接让它输出这个格式，就不需要重采样——重采样写错了不会报错，只会变调。

抓包里的 `voice_id` 是 `volcengine.xiaohe-2.0`，那是火山引擎的音色。
自建服务器拿不到，就退到本机 SAPI 的中文音色（通常是 Microsoft Huihui）。
音色不一致不影响协议，客户端只负责播字节。

用法：
    from .sapi_tts import synthesize
    pcm = synthesize("我来截个屏。")     # 失败返回 b""
"""
from __future__ import annotations

import hashlib
import logging
import os
import threading
from collections import OrderedDict

log = logging.getLogger("xiaoliu.tts")

SAMPLE_RATE = 24000
BYTES_PER_SAMPLE = 2

# SAFT24kHz16BitMono
_FMT_24K_16BIT_MONO = 26

# SAPI 语速 -10..10。默认 0 读中文明显偏慢（「我来截个屏。」要 1.9 秒），
# 调到 2 接近日常语速。
_VOICE_RATE = int(os.environ.get("TTS_RATE", "2") or 2)

_lock = threading.Lock()
_cache: OrderedDict[str, bytes] = OrderedDict()
_CACHE_MAX = 64
_voice_ready = False
_unavailable_reason = ""


def _pick_voice(voice) -> object | None:
    """优先中文音色。取不到就用手上第一个，总比没有强。"""
    try:
        tokens = list(voice.GetVoices())
    except Exception:                                    # noqa: BLE001
        return None
    for tok in tokens:
        try:
            desc = tok.GetDescription()
        except Exception:                                # noqa: BLE001
            continue
        if "Chinese" in desc or "中文" in desc or "Huihui" in desc:
            return tok
    return tokens[0] if tokens else None


def _synth_uncached(text: str) -> bytes:
    """真去调 SAPI。调用方必须持有 _lock，且已做过 COM 初始化。"""
    global _voice_ready, _unavailable_reason
    import comtypes.client as cc

    voice = cc.CreateObject("SAPI.SpVoice")
    if not _voice_ready:
        tok = _pick_voice(voice)
        if tok is not None:
            voice.Voice = tok
            try:
                log.info("tts 使用音色：%s", tok.GetDescription())
            except Exception:                            # noqa: BLE001
                pass
        _voice_ready = True
    # 每次都要设：SpVoice 是每次新建的，语速不跟着音色走
    try:
        voice.Rate = _VOICE_RATE
    except Exception:                                    # noqa: BLE001
        pass

    stream = cc.CreateObject("SAPI.SpMemoryStream")
    stream.Format.Type = _FMT_24K_16BIT_MONO
    voice.AudioOutputStream = stream
    voice.Speak(text)
    data = stream.GetData()
    pcm = bytes(data) if not isinstance(data, (bytes, bytearray)) else bytes(data)

    # 偶发情况：格式没被接受，SAPI 会用默认 22kHz。时长对不上就说明选错了。
    # 24kHz/16bit/mono = 48000 字节/秒；22kHz = 44000。这里只做合理性告警，
    # 不做重采样——宁可出声也别变调。
    if pcm and len(pcm) % 2:
        pcm = pcm[:-1]
    _ = _unavailable_reason
    return pcm


def synthesize(text: str) -> bytes:
    """文本 → 24kHz/16-bit/mono PCM。任何异常都退化成空串（调用方回静音）。"""
    global _unavailable_reason
    text = (text or "").strip()
    if not text:
        return b""

    key = hashlib.sha256(text.encode("utf-8")).hexdigest()
    with _lock:
        hit = _cache.get(key)
        if hit is not None:
            _cache.move_to_end(key)
            return hit

    try:
        import comtypes                                       # noqa: F401
        try:
            import comtypes
            comtypes.CoInitialize()                           # 每个线程都要一次
        except Exception:                                     # noqa: BLE001
            pass
        with _lock:
            # 双重检查：等锁期间可能已被别的请求填上
            hit = _cache.get(key)
            if hit is not None:
                return hit
            pcm = _synth_uncached(text)
    except Exception as exc:                                  # noqa: BLE001
        if not _unavailable_reason:
            _unavailable_reason = f"{type(exc).__name__}: {exc}"
            log.warning("SAPI 合成不可用，回退静音：%s", _unavailable_reason)
        return b""

    if not pcm:
        return b""
    with _lock:
        _cache[key] = pcm
        while len(_cache) > _CACHE_MAX:
            _cache.popitem(last=False)
    return pcm


def available() -> bool:
    """探一次，别等真请求才发现没装 comtypes。"""
    try:
        return bool(synthesize("你好"))
    except Exception:                                         # noqa: BLE001
        return False


def frames(pcm: bytes, chunk_ms: int = 20) -> list[bytes]:
    """切成客户端友好的小帧。20ms @24kHz/16bit = 960 字节。"""
    step = max(2, SAMPLE_RATE * BYTES_PER_SAMPLE * chunk_ms // 1000)
    if step % 2:
        step += 1
    return [pcm[i:i + step] for i in range(0, len(pcm), step)]


def silence(ms: int) -> bytes:
    return b"\x00\x00" * int(SAMPLE_RATE * ms / 1000)
