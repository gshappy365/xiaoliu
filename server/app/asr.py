"""本地 ASR：SenseVoice（sherpa-onnx，纯 CPU）。

为什么本地：讯飞 MaaS 只开了 chat 和 embeddings，没有语音端点
（`/v1/audio/transcriptions` 404）。而点点要的是「按住热键说话 → 立刻出字」，
云端往返会明显拖慢首字延迟。

选 SenseVoice 的理由：
  - 中文准确率明显好于 whisper-small，且自带标点（ITN）
  - int8 量化后约 230MB，4 秒音频在本机（i7-12700F）约 0.2-0.4 秒
  - 纯 CPU，不需要 CUDA/cuDNN（Windows 上那套 DLL 很难配）

输入格式（抓包实测）：裸 PCM，16 kHz / 16-bit / mono，无容器头。
"""
from __future__ import annotations

import logging
import os
import re
import threading
import time

log = logging.getLogger("xiaoliu.asr")

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_MODEL_DIR = os.path.join(os.path.dirname(os.path.dirname(HERE)),
                                 "models", "sense-voice")

_lock = threading.Lock()
_recognizer = None
_state: str | None = None          # None=未尝试, "ok", "unavailable"
_reason = ""

# SenseVoice 会在文本里插语言/情绪/事件标签，例如
#   <|zh|><|NEUTRAL|><|Speech|><|withitn|>今天天气不错。
# 这些标签不能喂给模型，必须去掉。
_TAG = re.compile(r"<\|[^|]*\|>")


def model_dir() -> str:
    return os.environ.get("ASR_MODEL_DIR", "").strip() or DEFAULT_MODEL_DIR


def _paths() -> tuple[str, str]:
    d = model_dir()
    return os.path.join(d, "model.int8.onnx"), os.path.join(d, "tokens.txt")


def available() -> bool:
    global _state, _reason
    if _state == "ok":
        return True
    if _state == "unavailable":
        return False
    with _lock:
        if _state is not None:
            return _state == "ok"
        try:
            import sherpa_onnx  # noqa: F401
        except Exception as exc:                         # noqa: BLE001
            _state, _reason = "unavailable", f"sherpa-onnx 未安装：{exc}"
            log.info("asr 不可用：%s", _reason)
            return False
        model, tokens = _paths()
        if not (os.path.exists(model) and os.path.exists(tokens)):
            _state = "unavailable"
            _reason = f"模型文件缺失：{model}"
            log.info("asr 不可用：%s", _reason)
            return False
        _state = "ok"
        return True


def _get():
    global _recognizer
    if _recognizer is not None:
        return _recognizer
    with _lock:
        if _recognizer is not None:
            return _recognizer
        import sherpa_onnx

        model, tokens = _paths()
        threads = int(os.environ.get("ASR_THREADS", "0") or 0)
        if threads <= 0:
            threads = max(2, min(8, (os.cpu_count() or 4) // 2))
        t0 = time.time()
        _recognizer = sherpa_onnx.OfflineRecognizer.from_sense_voice(
            model=model,
            tokens=tokens,
            num_threads=threads,
            use_itn=True,          # 数字/日期归一化：「一百二十三」→「123」
            debug=False,
            language="zh",
        )
        log.info("asr 模型加载完成 %.0fms（threads=%d）", (time.time() - t0) * 1000, threads)
        return _recognizer


def _to_float(pcm: bytes) -> list[float]:
    """s16le 裸 PCM → [-1,1] float 列表。"""
    n = len(pcm) // 2
    if n == 0:
        return []
    try:
        import numpy as np

        a = np.frombuffer(pcm[:n * 2], dtype="<i2").astype("float32") / 32768.0
        return a
    except Exception:                                    # noqa: BLE001
        import array

        a = array.array("h")
        a.frombytes(pcm[:n * 2])
        return [v / 32768.0 for v in a]


def clean(text: str) -> str:
    return _TAG.sub("", text or "").strip()


# ---------------------------------------------------------------------------
# 音频体检
#
# 起因：日志里出现过「4.27 秒音频只识别出『我。』」。只看文本无法判断是
# 「模型不准」还是「麦克风根本没收到声音」——这两件事的修法完全不同。
# SenseVoice 在近乎静音的输入上会幻觉出一个极短的固定词（实测就是「我。」），
# 看起来像识别失败，其实是**没有语音可识别**。
#
# 所以每次识别都顺手算三个数：
#   rms      —— 整体能量，正常说话约 0.05-0.20，安静底噪约 0.001-0.005
#   peak     —— 峰值，用来判断是否削波（>=0.99 说明增益过大）
#   voiced   —— 幅度超过**绝对**门限的采样点占比，衡量真正有声的时间比例
#
# voiced 必须用绝对门限，不能用「峰值的百分比」。踩过这个坑：虚拟声卡送来的
# -60dB 均匀底噪 peak 只有 0.002，按 peak*15% 算出来的阈值是 0.0003，
# 于是 79.8% 的采样点都"超过"阈值，被判成「有语音」——而这恰恰是最该
# 判定为静音的情况。
#
# 实测数据（本机，evidence/mic/）：
#   VB-Cable / AudioRelay 虚拟设备 → rms=0.00000 peak=0.0000 → 数字静音
#   UU远程虚拟「麦克风阵列」        → rms=0.00099 peak=0.0020 → -60dB 底噪
#   两者喂给 SenseVoice 都会幻觉出同一个词「我。」
#
# 判定：rms 和 peak 都低于门限才算静音。用「且」而不是「或」，
# 是为了别把轻声说话（peak 低但仍有起伏）误杀。
# ---------------------------------------------------------------------------
SILENCE_RMS = 0.0025      # 低于这个整体能量视为静音（约 -52 dBFS）
SILENCE_PEAK = 0.006      # 峰值也低于此才算，正常说话 peak 通常 >0.05
VOICED_FLOOR = 0.010      # voiced 的绝对幅度门限

_DUMP_DIR = os.environ.get("ASR_DUMP_DIR", "").strip()


def analyze(pcm: bytes) -> dict[str, float]:
    """返回音频体检指标。纯统计，不解码，几乎不耗时。"""
    n = len(pcm) // 2
    if n == 0:
        return {"dur": 0.0, "rms": 0.0, "peak": 0.0, "voiced": 0.0}
    try:
        import numpy as np

        a = np.frombuffer(pcm[:n * 2], dtype="<i2").astype("float32") / 32768.0
        rms = float(np.sqrt(np.mean(a * a)))
        peak = float(np.max(np.abs(a)))
    except Exception:                                    # noqa: BLE001
        import array

        arr = array.array("h")
        arr.frombytes(pcm[:n * 2])
        if not arr:
            return {"dur": 0.0, "rms": 0.0, "peak": 0.0, "voiced": 0.0}
        peak = max(abs(v) for v in arr) / 32768.0
        rms = (sum((v / 32768.0) ** 2 for v in arr) / len(arr)) ** 0.5
        a = [v / 32768.0 for v in arr]

    thr = VOICED_FLOOR
    if peak <= 0:
        voiced = 0.0
    else:
        try:
            import numpy as np

            voiced = float(np.mean(np.abs(a) > thr))
        except Exception:                                # noqa: BLE001
            voiced = sum(1 for v in a if abs(v) > thr) / len(a)
    return {"rms": rms, "peak": peak, "voiced": voiced}


def is_silent(stats: dict[str, float]) -> bool:
    """这段音频里有没有人说话。两个门限都低才算静音。"""
    return (stats.get("rms", 0.0) < SILENCE_RMS
            and stats.get("peak", 0.0) < SILENCE_PEAK)


def dump(pcm: bytes, sample_rate: int, tag: str = "asr") -> str | None:
    """把裸 PCM 存成 wav，供离线复现。仅当设置了 ASR_DUMP_DIR 时生效。"""
    if not _DUMP_DIR or not pcm:
        return None
    try:
        import wave

        os.makedirs(_DUMP_DIR, exist_ok=True)
        name = time.strftime(f"{tag}-%Y%m%d-%H%M%S") + ".wav"
        path = os.path.join(_DUMP_DIR, name)
        with wave.open(path, "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(sample_rate)
            w.writeframes(pcm)
        log.info("asr 音频已落盘 %s（%.2fs）", path, len(pcm) / 2 / sample_rate)
        return path
    except Exception as exc:                             # noqa: BLE001
        log.warning("asr 落盘失败：%s", exc)
        return None


def transcribe_ex(pcm: bytes, sample_rate: int = 16000) -> tuple[str, dict[str, float]]:
    """同 transcribe，但同时返回音频体检指标。

    静音判定优先于解码：没有语音就不要让模型去幻觉一个「我。」出来，
    那会变成一句无意义的用户输入，把整轮对话带偏。
    """
    if not pcm or not available():
        return "", {"dur": 0.0, "rms": 0.0, "peak": 0.0, "voiced": 0.0}
    stats = analyze(pcm)
    stats["dur"] = len(pcm) / 2 / sample_rate
    if is_silent(stats):
        log.info("asr 判定静音（rms=%.4f peak=%.3f voiced=%.1f%%）——跳过解码",
                 stats["rms"], stats["peak"], stats["voiced"] * 100)
        return "", stats
    return transcribe(pcm, sample_rate), stats


def transcribe(pcm: bytes, sample_rate: int = 16000) -> str:
    """裸 PCM → 文本。不可用或失败返回空串，由上层决定怎么退化。"""
    if not pcm or not available():
        return ""
    try:
        rec = _get()
        samples = _to_float(pcm)
        if len(samples) < sample_rate // 10:              # 短于 100ms 直接丢
            return ""
        t0 = time.time()
        stream = rec.create_stream()
        stream.accept_waveform(sample_rate, samples)
        rec.decode_stream(stream)
        raw = stream.result.text or ""
        text = clean(raw)
        log.info("asr 识别 %.2fs 音频 → %d 字，耗时 %.0fms",
                 len(pcm) / 2 / sample_rate, len(text), (time.time() - t0) * 1000)
        return text
    except Exception as exc:                             # noqa: BLE001
        log.warning("asr 识别失败：%s", exc)
        return ""


def status() -> dict[str, object]:
    model, tokens = _paths()
    return {
        "available": available(),
        "reason": _reason,
        "model_dir": model_dir(),
        "model_exists": os.path.exists(model),
        "tokens_exists": os.path.exists(tokens),
        "loaded": _recognizer is not None,
    }


__all__ = ["available", "transcribe", "transcribe_ex", "analyze", "is_silent",
           "dump", "clean", "status", "model_dir"]
