"""点点的 `voice_id` → 火山套餐 speaker ID。

点点在 TTS 握手时会带 `voice_id`（形如 `volcengine.vivi-2.0`），而套餐的
TTS 端点是 speaker ID（形如 `zh_female_vv_uranus_bigtts`）。两套命名没有
自动对应关系，只能硬映射。

实测（2026-10-28，`seed-tts-2.0` 资源）：

    zh_female_vv_uranus_bigtts        200，有音频
    zh_female_xiaohe_uranus_bigtts    200，有音频
    zh_female_xiaotian_uranus_bigtts  200 + {"code":55000000}（资源不匹配）
    zh_male_yunzhou_uranus_bigtts     200 + {"code":55000000}
    zh_female_vivi_uranus_bigtts      200 + {"code":55000000}

也就是说点点六个 voice_id 里目前只有两个能对上：

    vivi-2.0   → vv（同名）
    xiaohe-2.0 → xiaohe

其余（xiaotian / yunzhou / doubao-seed-*）这个资源下没有对应音色，统一回退
默认音色。**不要凭名字猜 speaker ID**：猜错的返回值是 HTTP 200 + JSON
错误体，不是 4xx，很容易被当成合成成功。
"""
from __future__ import annotations

import logging

from .config import settings

log = logging.getLogger("xiaoliu.voice_map")

DEFAULT_SPEAKER = "zh_female_vv_uranus_bigtts"

# 键一律小写：点点的 voice_id 大小写不保证稳定。
VOICE_MAP: dict[str, str] = {
    "volcengine.vivi-2.0": "zh_female_vv_uranus_bigtts",
    "volcengine.xiaohe-2.0": "zh_female_xiaohe_uranus_bigtts",
}

_unknown: set[str] = set()


def default_speaker() -> str:
    return (settings.ark_tts_speaker or DEFAULT_SPEAKER).strip()


def speaker_for(voice_id: str | None) -> str:
    """把点点的 voice_id 翻成 speaker。未登记的回退默认音色。"""
    vid = (voice_id or "").strip().lower()
    if not vid:
        return default_speaker()
    hit = VOICE_MAP.get(vid)
    if hit:
        return hit
    # 允许直接传 speaker ID（调试/测试用）
    if "_bigtts" in vid:
        return vid
    if vid not in _unknown:
        _unknown.add(vid)
        log.info("voice_map: 未登记的 voice_id %r，回退默认音色 %s",
                 voice_id, default_speaker())
    return default_speaker()


def status() -> dict[str, object]:
    return {
        "default": default_speaker(),
        "mapped": dict(VOICE_MAP),
        "unknown_seen": sorted(_unknown),
    }


__all__ = ["VOICE_MAP", "DEFAULT_SPEAKER", "default_speaker", "speaker_for", "status"]
