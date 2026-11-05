"""火山 Agent Plan TTS：doubao-seed-tts-2.0，直连 HTTP。

为什么直连而不是走 arkcli：

| 方式 | 首包 | 全文 | 说明 |
|---|---|---|---|
| arkcli `+gen --modality audio` | — | 1.48s | 每次多 435ms 子进程启动 + profile 解析 |
| 直连 openspeech | **0.60s** | 0.87s | 一个 Key、一个 POST |

为什么不用 `api/plan/v3/audio/speech`：该路径返回 404。套餐的 TTS 实际挂在
`openspeech.bytedance.com/api/v3/plan/tts/unidirectional`。

响应格式（实测，不是普通音频流）：

    {"code":0,"message":"","data":"<base64 PCM 分片>"}\n
    {"code":0,"message":"","data":"..."}\n

一次 15 字短句返回 9 行、约 114KB PCM（24 kHz / 16-bit / mono / s16le），
正好是点点要的格式，不需要 ffmpeg。

**失败不一定体现在状态码上**：音色与资源不匹配时返回 HTTP 200 +
`{"code":55000000,"message":"resource ID is mismatched..."}`。所以必须逐行
解析 JSON 的 code，不能只看 status。
"""
from __future__ import annotations

import base64
import json
import logging
import time

import httpx

from . import voice_map
from .config import settings

log = logging.getLogger("xiaoliu.ark_tts")

SAMPLE_RATE = 24000

# 音色与资源不匹配（实测：xiaotian / yunzhou / vivi 在该资源下都报这个）
ERR_SPEAKER_MISMATCH = 55000000

# 成功码。0 = 数据分片，20000000 = 流结束的终止码（实测最后一行固定是
# {"code":20000000,"message":"OK"}，不带 data）。把终止码当失败会把整段
# 合成结果丢掉——踩过一次。
OK_CODES = frozenset({0, 20000000})


class ArkTTSError(RuntimeError):
    def __init__(self, code: int, message: str):
        super().__init__(f"{code}: {message}")
        self.code = code
        self.message = message


def available() -> bool:
    return bool(settings.ark_api_key.strip()) and bool(settings.ark_tts_url.strip())


def _headers() -> dict[str, str]:
    # 这套头是实测出来的：只给 Bearer 会报 "app key not found in header or query"，
    # 必须同时带 X-Api-Key；X-Api-Resource-Id 缺失会报 "get resource id empty"。
    key = settings.ark_api_key.strip()
    return {
        "Authorization": f"Bearer {key}",
        "X-Api-Key": key,
        "X-Api-Resource-Id": settings.ark_tts_resource_id,
        "Content-Type": "application/json",
    }


def _request(text: str, speaker: str, timeout: float) -> bytes:
    """一次合成。返回 24k/s16le/mono PCM；上游 code 非 0 抛 ArkTTSError。"""
    body = {
        "req_params": {
            "audio_params": {"format": "pcm", "sample_rate": SAMPLE_RATE},
            "speaker": speaker,
            "text": text,
        }
    }
    pcm = bytearray()
    with httpx.stream("POST", settings.ark_tts_url, headers=_headers(),
                      json=body, timeout=timeout) as r:
        if r.status_code != 200:
            raise ArkTTSError(r.status_code,
                              r.read()[:200].decode("utf-8", "replace"))
        for raw in r.iter_lines():
            line = raw.strip()
            if not line:
                continue
            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                raise ArkTTSError(-1, line[:200])
            code = int(msg.get("code") or 0)
            if code not in OK_CODES:
                raise ArkTTSError(code, str(msg.get("message") or ""))
            data = msg.get("data")
            if data:
                pcm.extend(base64.b64decode(data))
    return bytes(pcm)


def _request_with_retry(text: str, speaker: str, timeout: float) -> bytes:
    """超时/连接类失败重试一次。

    实测正常合成 0.6-2s（28 字 → 约 1.2s），而 2026-10-28 15:42 那次挂到
    15.8s 才超时，用户干等 15 秒静音才听到 SAPI 的声音。链路偶发卡住重试一次
    基本就能拿到；业务错误（ArkTTSError）不重试，重试也没用。
    """
    try:
        return _request(text, speaker, timeout)
    except ArkTTSError:
        raise
    except Exception as exc:                             # noqa: BLE001
        log.warning("ark_tts: 首次请求失败（%s），重试一次", exc)
        return _request(text, speaker, timeout)


def synthesize(text: str, voice_id: str | None = None,
               timeout: float | None = None) -> bytes:
    """文本 → 24k PCM。任何失败返回空串，由 tts.py 回退 SAPI。"""
    text = (text or "").strip()
    if not text or not available():
        return b""

    speaker = voice_map.speaker_for(voice_id)
    t0 = time.time()
    try:
        pcm = _request_with_retry(text, speaker, timeout or settings.ark_tts_timeout)
    except ArkTTSError as exc:
        default = voice_map.default_speaker()
        if exc.code == ERR_SPEAKER_MISMATCH and speaker != default:
            # 点点的冷门音色在该资源下不存在。换默认音色再来一次，
            # 总比整句没声音好。
            log.warning("ark_tts: 音色 %s 不被该资源支持（%s），改用默认 %s 重试",
                        speaker, exc.message, default)
            try:
                pcm = _request(text, default, timeout or settings.ark_tts_timeout)
                speaker = default
            except (ArkTTSError, Exception) as exc2:     # noqa: BLE001
                log.warning("ark_tts: 默认音色也失败：%s", exc2)
                return b""
        else:
            log.warning("ark_tts: 合成失败（code=%s）：%s", exc.code, exc.message)
            return b""
    except Exception as exc:                             # noqa: BLE001
        log.warning("ark_tts: 请求失败（%.0fms）：%s",
                    (time.time() - t0) * 1000, exc)
        return b""

    dt = (time.time() - t0) * 1000
    if not pcm:
        log.warning("ark_tts: 返回空音频（speaker=%s，%.0fms）", speaker, dt)
        return b""
    log.info("ark_tts: %d 字 → %.2fs 音频，%.0fms（speaker=%s）",
             len(text), len(pcm) / 2 / SAMPLE_RATE, dt, speaker)
    return pcm


def status() -> dict[str, object]:
    return {
        "available": available(),
        "url": settings.ark_tts_url,
        "resource_id": settings.ark_tts_resource_id,
        "default_speaker": voice_map.default_speaker(),
        "voices": voice_map.status(),
        "timeout": settings.ark_tts_timeout,
    }


__all__ = ["available", "synthesize", "status", "ArkTTSError", "SAMPLE_RATE"]
