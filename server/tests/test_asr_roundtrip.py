"""ASR 闭环自测：SAPI 合成 → 16k PCM → SenseVoice → 文本。

不用麦克风也能验证识别链路：让 SAPI 念一句已知的话，直接当「用户说的」喂给 ASR，
比较输出与输入。这样在自检报告说「本机没拾到真实声音」的情况下也能验收。

用法：python tests/test_asr_roundtrip.py
"""
from __future__ import annotations

import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from app import asr                                       # noqa: E402

# SAFT16kHz16BitMono —— 直接让 SAPI 输出 ASR 要的格式，避免重采样
# （重采样写错了不报错，只会变调，属于静默错误）
_FMT_16K_16BIT_MONO = 9

CASES = [
    "帮我看看屏幕上写了什么",
    "把这个表格里的数字算一下总和",
    "今天天气怎么样",
    "打开浏览器搜索一下",
]


def synth_16k(text: str) -> bytes:
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


def main() -> int:
    st = asr.status()
    print("=" * 70)
    print("ASR 闭环自测")
    print("=" * 70)
    print(f"  available = {st['available']}   model_dir = {st['model_dir']}")
    if st["reason"]:
        print(f"  reason    = {st['reason']}")
    if not st["available"]:
        print("\n模型不可用，跳过。请先下载 SenseVoice 到 models/sense-voice/")
        return 2

    # 首次调用会加载模型，单独计时，别混进识别耗时
    t0 = time.time()
    asr.transcribe(synth_16k("预热"))
    print(f"  模型加载 + 首次识别：{(time.time() - t0) * 1000:.0f}ms\n")

    fails = 0
    for i, text in enumerate(CASES, 1):
        pcm = synth_16k(text)
        dur = len(pcm) / 2 / 16000
        t0 = time.time()
        got = asr.transcribe(pcm)
        dt = (time.time() - t0) * 1000
        # 判据：不算逐字相等（同音字/断句差异正常），
        # 而是「核心字命中率」——识别结果里包含输入中足够多的字。
        # 阈值定 60% 而不是 100%，因为这里的音频是 SAPI 合成的：
        # 合成音的韵律和真人差别不小（「把 这个表格 里的 数字 算一下 总和」
        # 这种长句，SAPI 会在奇怪的地方断，ASR 就跟着错）。
        # 这个测试要证明的是「链路通、速度快」，不是「识别率天花板」。
        core = [c for c in text if "\u4e00" <= c <= "\u9fff"]
        hit = sum(1 for c in core if c in got)
        ratio = hit / len(core) if core else 0
        ok = ratio >= 0.6
        if not ok:
            fails += 1
        print(f"[{i}] {'OK ' if ok else 'BAD'}  {dur:.2f}s 音频  识别 {dt:.0f}ms  命中 {hit}/{len(core)} = {ratio:.0%}")
        print(f"      原文：{text}")
        print(f"      识别：{got}")
        print(f"      实时率：{dur / (dt / 1000):.1f}x")
        print()

    print("=" * 70)
    print("全部通过" if fails == 0 else f"{fails} 项命中率不足")
    print("=" * 70)
    return 0 if fails == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
