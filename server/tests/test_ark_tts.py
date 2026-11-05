"""验收 Agent Plan TTS 直连：真人短句、音色映射、错误体识别。

判据（不靠耳朵）：
  1. 正常短句拿到 PCM：字节数为偶数、长度足够、开头不是 JSON 错误体
  2. 时长与字数同量级（15 字约 2-3 秒），不是空音频
  3. 无效音色返回 HTTP 200 + code 55000000 —— 必须被当成失败，不能当成功
  4. 点点的 voice_id 能映射到 speaker，未登记的落默认音色
  5. tts.synthesize(voice_id=...) 走通 doubao 分支

基线（2026-10-28 实测）：首包 0.60s、全文 0.87s、114KB / 2.38s。

用法：python tests/test_ark_tts.py
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

from app import ark_tts, tts, voice_map                     # noqa: E402

TEXT = "今天天气不错，我们出去走走吧。"
fails: list[str] = []


def check(cond: bool, msg: str) -> None:
    print(f"  {'PASS' if cond else 'FAIL'}  {msg}")
    if not cond:
        fails.append(msg)


def main() -> int:
    if not KEY:
        return ark_common.skip("没有 ARK_API_KEY，且 arkcli 取不到套餐 Key")
    if not ark_tts.available():
        return ark_common.skip("ark_tts 不可用（检查 ARK_API_KEY / ARK_TTS_URL）")

    print("=" * 78)
    print("Agent Plan TTS 直连测试")
    print("=" * 78)

    # ---- 1. 正常合成 ----
    print("\n[1] 正常合成")
    t0 = time.time()
    pcm = ark_tts.synthesize(TEXT)
    dt = (time.time() - t0) * 1000
    dur = len(pcm) / 2 / ark_tts.SAMPLE_RATE
    print(f"     {len(pcm)} 字节 / {dur:.2f}s / {dt:.0f}ms")
    check(bool(pcm), "拿到 PCM")
    check(len(pcm) % 2 == 0, "PCM 字节数为偶数（s16le 对齐）")
    check(not pcm.startswith(b"{"), "不是 JSON 错误体")
    check(1.0 <= dur <= 8.0, f"时长 {dur:.2f}s 在合理区间")
    check(dt < 8000, f"耗时 {dt:.0f}ms < 8s")

    # ---- 2. 音色映射 ----
    print("\n[2] 点点 voice_id 映射")
    check(voice_map.speaker_for("volcengine.vivi-2.0")
          == "zh_female_vv_uranus_bigtts", "vivi-2.0 → vv")
    check(voice_map.speaker_for("volcengine.xiaohe-2.0")
          == "zh_female_xiaohe_uranus_bigtts", "xiaohe-2.0 → xiaohe")
    check(voice_map.speaker_for("volcengine.yunzhou-2.0")
          == voice_map.default_speaker(), "未登记的 yunzhou → 默认音色")
    check(voice_map.speaker_for(None) == voice_map.default_speaker(),
          "空 voice_id → 默认音色")

    # ---- 3. 无效音色必须被识别为失败 ----
    print("\n[3] 无效音色的错误体识别")
    code = None
    try:
        ark_tts._request("你好", "bogus_speaker_not_exist", 10.0)
    except ark_tts.ArkTTSError as exc:
        code = exc.code
    check(code == ark_tts.ERR_SPEAKER_MISMATCH,
          f"bogus speaker 抛出 code={ark_tts.ERR_SPEAKER_MISMATCH}（实际 {code}）")

    # ---- 4. tts.py 的 doubao 分支 ----
    print("\n[4] tts.synthesize(voice_id=...) 走 doubao")
    os.environ["TTS_BACKEND"] = "doubao"
    check(tts.backend() == "doubao", f"backend() = {tts.backend()}")
    pcm2 = tts.synthesize("你好", "volcengine.xiaohe-2.0")
    check(bool(pcm2), f"拿到 PCM（{len(pcm2)} 字节）")
    frames = list(tts.frames(pcm2))
    check(len(frames) > 0, f"切成 {len(frames)} 帧 20ms PCM")

    print("\n" + "=" * 78)
    if fails:
        print(f"FAILED　{len(fails)} 项不通过：")
        for m in fails:
            print(f"  - {m}")
        return 1
    print("ALL PASS　TTS 直连、音色映射与错误体识别均符合预期")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
