"""TTS 后端调度。

三个后端，取舍很直接：

| 后端 | 首包延迟 | 音质 | 联网 | 依赖 |
|---|---|---|---|---|
| `sapi` | ~300ms | 机械（Microsoft Huihui，2010 年代音色） | 否 | Windows SAPI |
| `edge` | ~1.2s | 自然（zh-CN-XiaoxiaoNeural 神经音色） | 是 | edge-tts + ffmpeg |
| `doubao` | ~600ms | 自然（豆包 TTS 2.0，套餐内） | 是 | Agent Plan 专属 Key |

对语音助手来说首声延迟很关键，所以默认 `auto` = 有 SAPI 就用 SAPI。
想要更好听就设 `TTS_BACKEND=edge` 或 `TTS_BACKEND=doubao`。
`doubao` 失败时会降级 SAPI，再不行才回退静音。

三个后端都输出 **24 kHz / 16-bit / mono 裸 PCM**（点点要的格式）。
edge 返回 mp3，用 ffmpeg 解码并重采样到 24k——这一步交给 ffmpeg，
不自己写重采样（写错了不报错，只会变调）。doubao 直接要 pcm/24000，
省掉 ffmpeg。
"""
from __future__ import annotations

import asyncio
import logging
import os
import shutil
import subprocess

from . import ark_tts, sapi_tts

log = logging.getLogger("xiaoliu.tts")

SAMPLE_RATE = 24000


def backend() -> str:
    want = (os.environ.get("TTS_BACKEND", "auto") or "auto").strip().lower()
    if want == "doubao":
        if ark_tts.available():
            return "doubao"
        return "sapi" if sapi_tts.available() else ("edge" if _edge_ok() else "none")
    if want == "edge":
        return "edge" if _edge_ok() else ("sapi" if sapi_tts.available() else "none")
    if want == "sapi":
        return "sapi" if sapi_tts.available() else "none"
    # auto：优先 SAPI（快、离线），不可用再退 edge。
    # doubao 不在 auto 里——它是显式选择，避免默认就产生云端调用。
    if sapi_tts.available():
        return "sapi"
    return "edge" if _edge_ok() else "none"


def _find_ffmpeg() -> str | None:
    """找可用的 ffmpeg。

    坑：WinGet 装的 ffmpeg 会在 `AppData\\Local\\Microsoft\\WinGet\\Links\\` 放一个
    0 字节的转发文件，`shutil.which()` 能找到它，但 subprocess 起不来
    （WinError 193 不是有效的 Win32 应用程序）。所以要校验文件非空。
    """
    cand = os.environ.get("FFMPEG_BIN", "").strip()
    if cand and os.path.isfile(cand) and os.path.getsize(cand) > 0:
        return cand

    found = shutil.which("ffmpeg")
    if found and os.path.isfile(found) and os.path.getsize(found) > 0:
        return found

    import glob

    pats = [
        os.path.expandvars(r"%LOCALAPPDATA%\Microsoft\WinGet\Packages\*\ffmpeg*\bin\ffmpeg.exe"),
        os.path.expandvars(r"%LOCALAPPDATA%\Microsoft\WinGet\Packages\*\*\bin\ffmpeg.exe"),
        r"C:\ffmpeg\bin\ffmpeg.exe",
        r"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
    ]
    for p in pats:
        hits = sorted(glob.glob(p), reverse=True)
        if hits:
            return hits[0]
    return None


_FFMPEG: str | None | bool = False


def _ffmpeg() -> str | None:
    global _FFMPEG
    if _FFMPEG is False:
        _FFMPEG = _find_ffmpeg()
        if _FFMPEG:
            log.info("ffmpeg: %s", _FFMPEG)
    return _FFMPEG  # type: ignore[return-value]


def _edge_ok() -> bool:
    if _ffmpeg() is None:
        return False
    try:
        import edge_tts  # noqa: F401
        return True
    except Exception:                                    # noqa: BLE001
        return False


def available() -> bool:
    return backend() != "none"


# ---------------------------------------------------------------- edge
_VOICE = os.environ.get("TTS_EDGE_VOICE", "zh-CN-XiaoxiaoNeural")
_RATE = os.environ.get("TTS_EDGE_RATE", "+8%")


async def _edge_mp3(text: str) -> bytes:
    import edge_tts

    comm = edge_tts.Communicate(text, _VOICE, rate=_RATE)
    buf = bytearray()
    async for chunk in comm.stream():
        if chunk.get("type") == "audio":
            buf.extend(chunk["data"])
    return bytes(buf)


def _mp3_to_pcm(mp3: bytes) -> bytes:
    """mp3 → 24k/16/mono s16le。ffmpeg 处理解码与重采样。"""
    exe = _ffmpeg()
    if not exe:
        raise RuntimeError("找不到可用的 ffmpeg")
    cmd = [
        exe, "-hide_banner", "-loglevel", "error",
        "-i", "pipe:0",
        "-f", "s16le", "-acodec", "pcm_s16le",
        "-ac", "1", "-ar", str(SAMPLE_RATE),
        "pipe:1",
    ]
    p = subprocess.run(cmd, input=mp3, capture_output=True, timeout=60)
    if p.returncode != 0 or not p.stdout:
        raise RuntimeError(f"ffmpeg 解码失败：{p.stderr[:200]!r}")
    return p.stdout


def _edge_synthesize(text: str) -> bytes:
    mp3 = asyncio.run(_edge_mp3(text))
    if not mp3:
        return b""
    return _mp3_to_pcm(mp3)


# ---------------------------------------------------------------- 对外
def synthesize(text: str, voice_id: str | None = None) -> bytes:
    """文本 → 24k/16/mono PCM。失败返回空串，由上层退静音。

    `voice_id` 是点点握手时发来的音色标识，只有 doubao 后端会用到
    （经 voice_map 翻译成 speaker ID）。
    """
    if not text.strip():
        return b""
    b = backend()
    try:
        if b == "sapi":
            return sapi_tts.synthesize(text)
        if b == "edge":
            return _edge_synthesize(text)
        if b == "doubao":
            pcm = ark_tts.synthesize(text, voice_id)
            if pcm:
                return pcm
            # doubao 失败（Key 缺失/套餐到期/网络）不能让整句没声音
            log.warning("tts（doubao）无音频，回退 SAPI")
            return sapi_tts.synthesize(text) if sapi_tts.available() else b""
    except Exception as exc:                             # noqa: BLE001
        log.warning("tts（%s）合成失败：%s，回退 SAPI", b, exc)
        try:
            return sapi_tts.synthesize(text)
        except Exception:                                # noqa: BLE001
            return b""
    return b""


def frames(pcm: bytes):
    return sapi_tts.frames(pcm)


def status() -> dict[str, object]:
    return {
        "backend": backend(),
        "sapi": sapi_tts.available(),
        "edge": _edge_ok(),
        "edge_voice": _VOICE,
        "edge_rate": _RATE,
        "doubao": ark_tts.available(),
    }


__all__ = ["available", "synthesize", "frames", "status", "backend", "SAMPLE_RATE"]
