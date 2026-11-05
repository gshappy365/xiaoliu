"""云端 ASR：doubao-seed-asr-2.0，走 arkcli 子进程。

为什么是子进程而不是直连 HTTP：

    `api/v3/plan/sauc/bigmodel_*` 三个端点都只接受 GET（WebSocket upgrade），
    没有 HTTP 提交入口。要直连就得复刻火山的二进制分帧（gzip + 自定义包头），
    风险远大于收益。arkcli 已经把这条协议封装好了。

代价与限制：

  - 每次调用有约 435ms 的进程启动开销，整段识别约 2.1s（2.4s 音频）。
    本地 SenseVoice 热态只要 58ms，所以**默认后端仍是 local**。
  - arkcli 只能吃整段文件，做不了边录边出字。doubao 模式下没有 partial。
  - 绝对路径的盘符会被当成 URI scheme（`unsupported audio source scheme "c"`），
    所以必须把 wav 写进临时目录、以该目录为 cwd 传**相对文件名**。

失败策略：超时/非零退出/解析失败都算失败，冷却 5 分钟后重试；上层回退本地。
"""
from __future__ import annotations

import json
import logging
import os
import shutil
import subprocess
import tempfile
import time
import wave

from .config import settings

log = logging.getLogger("xiaoliu.asr_doubao")

COOLDOWN_S = 300.0

_poisoned = False
_poisoned_at = 0.0
_poison_reason = ""
_last_ms = 0.0


def _bin() -> str | None:
    name = (settings.arkcli_bin or "arkcli").strip()
    hit = shutil.which(name)
    if hit:
        return hit
    # Windows 上 npm 的 shim 是 arkcli.CMD，which 一般能找到；再兜一层
    if os.name == "nt" and not name.lower().endswith((".cmd", ".bat", ".exe")):
        hit = shutil.which(name + ".cmd")
    return hit


def _is_poisoned() -> bool:
    global _poisoned
    if not _poisoned:
        return False
    if time.time() - _poisoned_at >= COOLDOWN_S:
        _poisoned = False
        log.info("asr_doubao: 冷却结束，重新尝试")
        return False
    return True


def _mark_poisoned(reason: str) -> None:
    global _poisoned, _poisoned_at, _poison_reason
    _poisoned = True
    _poisoned_at = time.time()
    _poison_reason = reason


def available() -> bool:
    return _bin() is not None and not _is_poisoned()


def _resample_s16(pcm: bytes, src: int, dst: int = 16000) -> bytes:
    """s16le 重采样。numpy 不在就返回空串（上层回退本地）。"""
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


def _extract_json(text: str) -> dict | None:
    """从 stdout 里捞出第一个 JSON 对象。

    CLI 可能先打一行 `warn: ...` 再打 JSON（+chat 上实见过），不能直接
    json.loads 整个输出。
    """
    start = text.find("{")
    while start != -1:
        try:
            obj, _ = json.JSONDecoder().raw_decode(text[start:])
            if isinstance(obj, dict):
                return obj
        except json.JSONDecodeError:
            pass
        start = text.find("{", start + 1)
    return None


def transcribe(pcm: bytes, sample_rate: int = 16000) -> str:
    """裸 PCM → 文本。任何失败返回空串，由上层回退本地识别。"""
    if not pcm or not available():
        return ""

    if sample_rate != 16000:
        pcm = _resample_s16(pcm, sample_rate)
        if not pcm:
            log.warning("asr_doubao: %dHz 重采样失败（numpy 缺失），回退本地",
                        sample_rate)
            return ""

    exe = _bin()
    if exe is None:
        return ""

    t0 = time.time()
    try:
        with tempfile.TemporaryDirectory(prefix="arkasr-") as tmp:
            wav = os.path.join(tmp, "speech.wav")
            with wave.open(wav, "wb") as w:
                w.setnchannels(1)
                w.setsampwidth(2)
                w.setframerate(16000)
                w.writeframes(pcm)

            # 必须相对路径：@C:/... 会被解析成 scheme "c" 而报错。
            # shell=True 是因为 Windows 上 npm 的入口是 .cmd，CreateProcess
            # 不能直接执行批处理。
            cmd = (f'"{exe}" +understand asr --input @speech.wav '
                   f'--audio-format wav --audio-protocol ws-unidirectional '
                   f'--timeout {int(settings.asr_doubao_timeout)}s')
            # 关键：服务进程里有 ARK_API_KEY（直连 TTS/识图用），而 arkcli 会
            # 把它当成显式的 --api-key 覆盖。plan 的语音命令拒绝这种覆盖，
            # 会直接失败（"不支持 --api-key / --base-url 覆盖"）。
            # 所以子进程里必须把它摘掉，让 CLI 走已登录的 profile。
            env = os.environ.copy()
            env.pop("ARK_API_KEY", None)
            env.pop("ARK_BASE_URL", None)
            p = subprocess.run(cmd, cwd=tmp, shell=True, env=env,
                               capture_output=True, text=True,
                               encoding="utf-8", errors="replace",
                               timeout=settings.asr_doubao_timeout)
    except subprocess.TimeoutExpired:
        _mark_poisoned(f"超过 {settings.asr_doubao_timeout:.0f}s 未返回")
        log.warning("asr_doubao: 识别超时（%.0fs），%d 秒内不再尝试",
                    settings.asr_doubao_timeout, int(COOLDOWN_S))
        return ""
    except Exception as exc:                             # noqa: BLE001
        log.warning("asr_doubao: 调用失败：%s", exc)
        return ""

    dt = time.time() - t0
    global _last_ms
    _last_ms = dt * 1000
    out = (p.stdout or "") + "\n" + (p.stderr or "")
    # CLI 的成功 JSON 在 stdout、校验失败（rc=2）的 JSON 在 stderr，
    # 所以两边都要找。
    obj = _extract_json(out)
    if obj is None:
        log.warning("asr_doubao: 输出里没有 JSON（rc=%s）：%s",
                    p.returncode, out.strip()[:300])
        return ""
    if obj.get("ok") is False or "error" in obj:
        err = obj.get("error") or obj
        log.warning("asr_doubao: 上游报错（%.0fms）：%s",
                    dt * 1000, str(err)[:300])
        # 认证/额度类错误冷却久一点没意义，但不冷却会每句话都打一遍日志。
        return ""

    text = str(obj.get("content") or "").strip()
    log.info("asr_doubao: %.2fs 音频 → %d 字，%.0fms",
             len(pcm) / 2 / 16000, len(text), dt * 1000)
    return text


def status() -> dict[str, object]:
    return {
        "available": available(),
        "bin": _bin(),
        "backend": settings.asr_backend,
        "timeout": settings.asr_doubao_timeout,
        "poisoned": _poisoned,
        "poison_reason": _poison_reason,
        "last_ms": round(_last_ms),
    }


__all__ = ["available", "transcribe", "status"]
