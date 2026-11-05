"""ASR 静音与低电平行为测试。

锁住两条已经踩过坑的结论：

1. **低电平不会让识别崩掉。** 同一句话衰减到 -48dB（rms≈0.004），SenseVoice
   仍然 100% 正确。所以「识别出很少的字」不能归因为「用户说话声音小」。

2. **真静音必须判为静音，且不能让模型幻觉。** 本机的虚拟声卡（VB-Cable、
   AudioRelay）送来的是数字静音，UU远程虚拟设备送来的是 -60dB 底噪。
   这两类输入喂给 SenseVoice 都会输出同一个固定短词「我。」——正是客户端
   日志里「4.27 秒音频 → '我。'」的来源。上层必须靠 is_silent() 拦下来，
   否则这句幻觉会变成一句假的用户输入。

用法：python tests/test_asr_silence.py
"""
from __future__ import annotations

import os
import sys
import wave

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from app import asr                                        # noqa: E402

SENTENCE = "帮我看看屏幕上写了什么"

STEPS = [
    ("原始", 1.0),
    ("-12dB", 0.25),
    ("-24dB", 0.063),
    ("-36dB", 0.016),
    ("-48dB", 0.004),
    ("全静音", 0.0),
]

# 这些短词是 SenseVoice 在无语音输入上的典型幻觉，不应被当成真实输入
HALLUCINATIONS = {"我。", "我", "。", "嗯。", "啊。"}

fails: list[str] = []


def check(cond: bool, msg: str) -> None:
    print(f"  {'PASS' if cond else 'FAIL'}  {msg}")
    if not cond:
        fails.append(msg)


def synth_16k(text: str) -> bytes:
    import comtypes.client as cc

    voice = cc.CreateObject("SAPI.SpVoice")
    try:
        for tok in voice.GetVoices():
            if "Chinese" in tok.GetDescription() or "Huihui" in tok.GetDescription():
                voice.Voice = tok
                break
    except Exception:                                      # noqa: BLE001
        pass
    ms = cc.CreateObject("SAPI.SpMemoryStream")
    ms.Format.Type = 9                                     # 16kHz/16bit/mono
    voice.AudioOutputStream = ms
    voice.Speak(text)
    return bytes(ms.GetData())


def scale(pcm: bytes, factor: float) -> bytes:
    """按比例缩放采样幅度。不用 numpy（venv 里没装，且 asr 本身有兜底）。"""
    import array

    a = array.array("h")
    a.frombytes(pcm[:len(pcm) // 2 * 2])
    out = array.array("h", (max(-32768, min(32767, int(v * factor))) for v in a))
    return out.tobytes()


def hit_rate(ref: str, got: str) -> float:
    chars = {c for c in ref if c not in "，。！？、 "}
    if not chars:
        return 0.0
    return sum(1 for c in chars if c in got) / len(chars)


def main() -> int:
    st = asr.status()
    if not st["available"]:
        print(f"ASR 不可用：{st['reason']}")
        return 2

    print("=" * 78)
    print(f"ASR 静音/低电平行为测试　句子={SENTENCE!r}")
    print("=" * 78)

    base = synth_16k(SENTENCE)
    print(f"合成音频 {len(base) / 2 / 16000:.2f}s\n")

    print(f"{'档位':<10}{'rms':>9}{'peak':>8}{'有声%':>8}  {'判定':<7}识别结果")
    print("-" * 78)
    rows = []
    for label, factor in STEPS:
        pcm = scale(base, factor)
        stats = asr.analyze(pcm)
        silent = asr.is_silent(stats)
        text, _ = asr.transcribe_ex(pcm, 16000)
        rows.append((label, factor, stats, silent, text))
        print(f"{label:<10}{stats['rms']:>9.4f}{stats['peak']:>8.3f}"
              f"{stats['voiced'] * 100:>7.1f}%  {'静音' if silent else '有语音':<7}"
              f"{text or '（空）'}")
    print("-" * 78)

    # ---- 1. 低电平不该判静音，也不该识别失败 ----
    print("\n[1] 低电平（-36dB / -48dB）仍应正常识别")
    for label, factor, stats, silent, text in rows:
        if factor not in (0.016, 0.004):
            continue
        check(not silent, f"{label} 未被误判为静音（rms={stats['rms']:.4f}）")
        check(hit_rate(SENTENCE, text) >= 0.6,
              f"{label} 命中率 {hit_rate(SENTENCE, text):.0%} ≥ 60%")

    # ---- 2. 纯静音必须判静音，且不能输出幻觉词 ----
    print("\n[2] 全静音必须判为静音")
    zero = next(r for r in rows if r[1] == 0.0)
    check(zero[3], "全静音被 is_silent 判定为静音")
    check(zero[4] == "", f"全静音不输出文本（实际 {zero[4]!r}）")

    # ---- 3. 真实虚拟设备录音（本机证据） ----
    mic_dir = os.path.join(os.path.dirname(os.path.dirname(HERE)), "evidence", "mic")
    if os.path.isdir(mic_dir):
        wavs = sorted(f for f in os.listdir(mic_dir) if f.endswith(".wav"))
        if wavs:
            print(f"\n[3] 本机虚拟声卡录音（{len(wavs)} 个）必须全部判为静音")
            for f in wavs:
                p = os.path.join(mic_dir, f)
                with wave.open(p, "rb") as w:
                    pcm = w.readframes(w.getnframes())
                    sr = w.getframerate()
                stats = asr.analyze(pcm)
                silent = asr.is_silent(stats)
                text, _ = asr.transcribe_ex(pcm, sr)
                check(silent, f"{f} 判为静音（rms={stats['rms']:.5f} peak={stats['peak']:.4f}）")
                check(text == "", f"{f} 未把幻觉词当结果（实际 {text!r}）")
    else:
        print("\n[3] 跳过：没有 evidence/mic/ 样本")

    print("\n" + "=" * 78)
    if fails:
        print(f"FAILED　{len(fails)} 项不通过：")
        for m in fails:
            print(f"  - {m}")
        return 1
    print("ALL PASS　静音判定与低电平识别均符合预期")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
