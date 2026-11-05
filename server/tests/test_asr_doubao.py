"""验收云端 ASR：arkcli 子进程 + doubao-seed-asr-2.0。

判据：
  1. arkcli 可用（不在 PATH 上就 SKIP，不算失败）
  2. 24k PCM 重采样到 16k 后能识别出与原文一致的句子（命中率 ≥ 80%）
  3. 耗时在合理区间（基线约 2.1s，含 435ms 进程启动）
  4. 静音不送去识别（由 main.py 的 is_silent 前置判断，这里只验证接口）

注意：云端比本地 SenseVoice 慢 35 倍，所以生产默认仍是 local。这个测试
证明「可选后端能用」，不是证明它该做默认。

用法：python tests/test_asr_doubao.py
"""
from __future__ import annotations

import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import ark_common                                          # noqa: E402

KEY = ark_common.ensure_key()

sys.path.insert(0, os.path.dirname(HERE))

from app import ark_tts, asr_doubao                        # noqa: E402

TEXT = "今天天气不错，我们出去走走吧。"

fails: list[str] = []


def check(cond: bool, msg: str) -> None:
    print(f"  {'PASS' if cond else 'FAIL'}  {msg}")
    if not cond:
        fails.append(msg)


def hit_rate(ref: str, got: str) -> float:
    chars = {c for c in ref if c not in "，。！？、 "}
    if not chars:
        return 0.0
    return sum(1 for c in chars if c in got) / len(chars)


def main() -> int:
    if not asr_doubao.available():
        return ark_common.skip("arkcli 不可用（没装或不在 PATH）")
    if not KEY or not ark_tts.available():
        return ark_common.skip("没有 ARK_API_KEY，无法合成测试音频")

    print("=" * 78)
    print("云端 ASR 测试（arkcli + doubao-seed-asr-2.0）")
    print("=" * 78)
    print(f"arkcli: {asr_doubao.status().get('bin')}")

    # 用 TTS 现造一段 24k 音频，再重采样到 16k——避免依赖固定样本文件。
    print("\n[1] 生成测试音频")
    pcm24 = ark_tts.synthesize(TEXT)
    if not pcm24:
        return ark_common.skip("TTS 没拿到音频，无法继续")
    pcm16 = asr_doubao._resample_s16(pcm24, 24000, 16000)
    print(f"     24k {len(pcm24)} 字节 → 16k {len(pcm16)} 字节")
    check(bool(pcm16), "重采样成功")
    check(abs(len(pcm16) / len(pcm24) - 16000 / 24000) < 0.02,
          "重采样比例正确")

    print("\n[2] 云端识别")
    t0 = time.time()
    text = asr_doubao.transcribe(pcm16, 16000)
    dt = (time.time() - t0) * 1000
    print(f"     耗时 {dt:.0f}ms　结果：{text!r}")
    check(bool(text), "识别出文本")
    if text:
        rate = hit_rate(TEXT, text)
        check(rate >= 0.8, f"命中率 {rate:.0%} ≥ 80%")
    check(dt < 15000, f"耗时 {dt:.0f}ms < 15s")

    print("\n[3] 输入规格防护")
    check(asr_doubao.transcribe(b"", 16000) == "", "空 PCM 直接返回空串")

    print("\n" + "=" * 78)
    if fails:
        print(f"FAILED　{len(fails)} 项不通过：")
        for m in fails:
            print(f"  - {m}")
        return 1
    print("ALL PASS　云端 ASR 可选后端可用")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
