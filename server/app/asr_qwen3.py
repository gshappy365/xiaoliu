"""本地 GPU ASR：Qwen3-ASR-1.7B Q8_0，llama.cpp Vulkan 推理。

部署形态（2026-11-04）：

    llama-server（计划任务 qwen3-asr，无窗口）
      ├ D:\\gao\\qwen3-asr\\Qwen3-ASR-1.7B-Q8_0.gguf   2.17GB
      └ D:\\gao\\qwen3-asr\\mmproj.gguf                339MB 音频编码器
      监听 http://127.0.0.1:8930，OpenAI 兼容 /v1/chat/completions

为什么走 HTTP 而不是进程内加载：

  - llama.cpp 的 Vulkan 状态（着色器编译缓存、显存池）留在独立进程里最稳，
    崩了也不拖累服务端；计划任务会自动重启它。
  - 实测 6-8s 音频 0.18-0.20s 出结果（RTF≈0.025），HTTP 往返（本机）<5ms，
    完全不是瓶颈；比 SenseVoice 热态 58ms 慢一点，但准确率高一个档次。

输出剥离：模型会把「语种 + 转写」包在标记里返回，例如
`language Chinese<asr_text>你好</asr_text>`，这里统一剥掉再给上层。

失败策略：超时/连接失败/非 200 都算失败，冷却 5 分钟，上层回退本地 SenseVoice。
没有 partial（整段识别），和 doubao 一样按完才出字。
"""
from __future__ import annotations

import base64
import io
import json
import logging
import re
import time
import urllib.error
import urllib.request
import wave

from .config import settings

log = logging.getLogger("xiaoliu.asr_qwen3")

COOLDOWN_S = 300.0

_poisoned = False
_poisoned_at = 0.0
_poison_reason = ""
_last_ms = 0.0

# /health 探测结果缓存：不能每次 transcribe 都打一发 health，
# 但也不能拿几分钟前的旧状态。30 秒足够新，又不会给 llama-server 添堵。
_health_cached: bool | None = None
_health_at = 0.0
_HEALTH_TTL_S = 30.0

# 模型输出形如 `language Chinese<asr_text>你好</asr_text>`。
# 语种词是英文（Chinese/English/...），转写正文里不会再出现 <asr_text>。
_MARKUP_RE = re.compile(r"^\s*language\s+\S+<asr_text>(.*?)(?:</asr_text>)?\s*$",
                        re.DOTALL)


def strip_markup(text: str) -> str:
    """剥掉语种声明与 asr_text 包装，只留转写正文。"""
    m = _MARKUP_RE.match(text or "")
    return (m.group(1) if m else (text or "")).strip()


def _is_poisoned() -> bool:
    global _poisoned
    if not _poisoned:
        return False
    if time.time() - _poisoned_at >= COOLDOWN_S:
        _poisoned = False
        log.info("asr_qwen3: 冷却结束，重新尝试")
        return False
    return True


def _mark_poisoned(reason: str) -> None:
    global _poisoned, _poisoned_at, _poison_reason
    _poisoned = True
    _poisoned_at = time.time()
    _poison_reason = reason


def _base_url() -> str:
    return (settings.asr_qwen3_base_url or "").rstrip("/")


def available() -> bool:
    """llama-server 是否可达。探测结果缓存 30 秒。"""
    global _health_cached, _health_at
    if not _base_url() or _is_poisoned():
        return False
    now = time.time()
    if _health_cached is not None and now - _health_at < _HEALTH_TTL_S:
        return _health_cached
    ok = False
    try:
        # 代理要显式绕开：沙箱/系统代理会把 127.0.0.1 请求改写成 502。
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with opener.open(_base_url() + "/health", timeout=3) as r:
            ok = r.status == 200
    except Exception:                                    # noqa: BLE001
        ok = False
    _health_cached, _health_at = ok, now
    return ok


def _resample_s16(pcm: bytes, src: int, dst: int = 16000) -> bytes:
    """s16le 线性重采样（与 asr_doubao 同一实现）。"""
    try:
        import numpy as np
    except Exception:                                    # noqa: BLE001
        return b""
    n = len(pcm) // 2
    if n == 0:
        return b""
    a = np.frombuffer(pcm[:n * 2], dtype="<i2").astype("float32")
    out_n = int(round(n * dst / src))
    if out_n <= 0:
        return b""
    idx = np.linspace(0.0, n - 1.0, out_n)
    out = np.interp(idx, np.arange(n, dtype="float32"), a).astype("<i2")
    return out.tobytes()


def _pcm_to_wav_b64(pcm: bytes, sample_rate: int) -> str | None:
    """裸 PCM → 内存 WAV → base64。不落盘（HTTP 传参不需要临时文件）。"""
    try:
        buf = io.BytesIO()
        with wave.open(buf, "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(sample_rate)
            w.writeframes(pcm)
        return base64.b64encode(buf.getvalue()).decode("ascii")
    except Exception:                                    # noqa: BLE001
        return None


def transcribe(pcm: bytes, sample_rate: int = 16000) -> str:
    """裸 PCM → 文本。任何失败返回空串，由上层回退本地识别。"""
    if not pcm or not available():
        return ""

    if sample_rate != 16000:
        pcm = _resample_s16(pcm, sample_rate)
        if not pcm:
            log.warning("asr_qwen3: %dHz 重采样失败（numpy 缺失），回退本地",
                        sample_rate)
            return ""
        sample_rate = 16000

    b64 = _pcm_to_wav_b64(pcm, sample_rate)
    if not b64:
        return ""

    body = json.dumps({
        "messages": [{"role": "user", "content": [
            {"type": "input_audio",
             "input_audio": {"data": b64, "format": "wav"}}]}],
        "max_tokens": 512,
        "temperature": 0,
    }).encode("utf-8")

    t0 = time.time()
    try:
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        req = urllib.request.Request(
            _base_url() + "/v1/chat/completions", data=body,
            headers={"Content-Type": "application/json"})
        with opener.open(req, timeout=settings.asr_qwen3_timeout) as r:
            resp = json.load(r)
    except urllib.error.HTTPError as exc:
        _mark_poisoned(f"HTTP {exc.code}")
        log.warning("asr_qwen3: HTTP %s，%d 秒内不再尝试", exc.code,
                    int(COOLDOWN_S))
        return ""
    except Exception as exc:                             # noqa: BLE001
        _mark_poisoned(repr(exc)[:200])
        log.warning("asr_qwen3: 调用失败（%s），%d 秒内不再尝试", exc,
                    int(COOLDOWN_S))
        return ""

    dt = time.time() - t0
    global _last_ms
    _last_ms = dt * 1000

    try:
        raw = str(resp["choices"][0]["message"]["content"] or "")
    except (KeyError, IndexError, TypeError):
        log.warning("asr_qwen3: 响应形状不对：%s", str(resp)[:300])
        return ""

    text = strip_markup(raw)
    log.info("asr_qwen3: %.2fs 音频 → %d 字，%.0fms",
             len(pcm) / 2 / sample_rate, len(text), dt * 1000)
    return text


def status() -> dict[str, object]:
    return {
        "available": available(),
        "base_url": _base_url(),
        "backend": settings.asr_backend,
        "timeout": settings.asr_qwen3_timeout,
        "poisoned": _poisoned,
        "poison_reason": _poison_reason,
        "last_ms": round(_last_ms),
    }


__all__ = ["available", "transcribe", "status", "strip_markup"]
