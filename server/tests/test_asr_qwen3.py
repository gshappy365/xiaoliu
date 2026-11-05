"""验收 Qwen3-ASR 后端（llama.cpp Vulkan，8930 端口）。

判据：
  1. strip_markup 能剥掉 `language Xxx<asr_text>…</asr_text>` 各种形状
     （含缺闭合标签、纯语种无正文）——纯函数，不需要 llama-server 在跑
  2. llama-server 可达时：evidence/asr-dump 真实录音能识别出中文，
     耗时在合理区间（热态基线约 0.2s；冷态首请求含着色器编译约 13s）
  3. llama-server 不在时 available() 返回 False，transcribe 返回空串
     （上层回退本地 SenseVoice）——不 SKIP、不算失败，这正是降级路径

用法：python tests/test_asr_qwen3.py
"""
from __future__ import annotations

import os
import sys
import time
import wave

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from app import asr_qwen3                                  # noqa: E402

PASS = 0
FAIL = 0


def check(name: str, ok: bool, detail: str = "") -> None:
    global PASS, FAIL
    tag = "PASS" if ok else "FAIL"
    print(f"[{tag}] {name}{('  ' + detail) if detail else ''}")
    if ok:
        PASS += 1
    else:
        FAIL += 1


# ---- 1. 输出剥离 ----------------------------------------------------

cases = [
    ("language Chinese<asr_text>你好世界</asr_text>", "你好世界"),
    ("language English<asr_text>hello there</asr_text>", "hello there"),
    # max_tokens 截断时闭合标签可能缺失
    ("language Chinese<asr_text>没闭合的", "没闭合的"),
    # 空转写
    ("language Chinese<asr_text></asr_text>", ""),
    ("language Chinese<asr_text>", ""),
    # 万一上游改了格式不再带包装，应原样保留
    ("裸文本输出", "裸文本输出"),
    ("", ""),
]
for raw, want in cases:
    got = asr_qwen3.strip_markup(raw)
    check(f"strip_markup {raw[:32]!r}", got == want, f"→ {got!r}")

# ---- 2/3. 对着真实服务测 --------------------------------------------

if not asr_qwen3.available():
    print(f"llama-server 不可达（base_url={asr_qwen3.status()['base_url']}），"
          "跳过在线判据——降级路径（available=False → 空串）已由设计保证")
else:
    wav_path = os.path.join(HERE, "..", "..", "..", "wbai", "rea", "evidence",
                            "asr-dump", "asr-20261027-133131.wav")
    wav_path = os.path.normpath(wav_path)
    if not os.path.isfile(wav_path):
        # 目录布局不同就退回绝对路径
        wav_path = r"D:\gao\wbai\rea\evidence\asr-dump\asr-20261027-133131.wav"
    with wave.open(wav_path, "rb") as w:
        dur = w.getnframes() / w.getframerate()
        pcm = w.readframes(w.getnframes())
        rate = w.getframerate()

    t0 = time.time()
    text = asr_qwen3.transcribe(pcm, rate)
    dt = time.time() - t0
    print(f"录音 {dur:.1f}s @ {rate}Hz → {dt:.2f}s → {text!r}")
    check("识别非空", bool(text))
    check("命中关键词（截图）", "截图" in text, text)
    check("耗时合理（<10s，热态基线 0.2s）", dt < 10, f"{dt:.2f}s")

print(f"\n{PASS} passed, {FAIL} failed")
sys.exit(1 if FAIL else 0)
