"""上游视觉模型：商汤 SenseNova（OpenAI 兼容 /v1/chat/completions）。

为什么需要这一层：
    文本上游 spark-x2.6 的 `/v1/models` 里只有它自己，**没有任何视觉模型**。
    而且它对图片是静默丢弃——HTTP 200，模型反问「你屏幕上是什么」，
    然后基于「看不见」编造内容。这比报错危险得多。

    本地 Windows OCR 是一条退路，但它认形近字的能力很差。同一张 1280x720
    截图实测：
        OCR  → 「点点 Windows 予 贞 石 开 · M3 OS 集 成 spike」
        VL   → 「点点 Windows 预研 - M3 OS 集成 spike」
    OCR 还把「-22dBFS 的短促测试音」认成「-22dBFS 促测 试 高」，完全不可读。
    VL 甚至能说出「黑色胶囊悬浮窗显示『在听...』并附有音频波形」这种布局信息。

    所以：有 VL 用 VL，没有才退回 OCR。

延迟与准确率的取舍（实测数据，同一张图）：
    reasoning_effort=none     8.1s   推理 478 tok   → 把点点认成「Edge 浏览器」✗
    reasoning_effort=low     10.9s   推理 650 tok   → 认成「Windows 相册」✗
    reasoning_effort=minimal 16.0s   推理 865 tok   → 正确 ✓
    reasoning_effort=medium  31.2s   推理 2048 tok  → 正文为空 ✗
    默认（不传）             19.6s   推理 920 tok   → 完全准确 ✓

    **推理给少了它会瞎猜。** 默认取 minimal，宁慢勿错。
    medium 会吐空正文，因为 max_tokens 被推理吃光——所以下面强制给足
    max_tokens，并在正文为空时明确报错而不是返回半截内容。
"""
from __future__ import annotations

import base64
import io
import logging
import time

import httpx

from .config import settings

log = logging.getLogger("xiaoliu.vision")

# 正文被推理吃光是这个模型最常见的静默失败，所以 max_tokens 必须远大于
# 预期的正文长度。实测推理峰值约 2000，正文 200-400，给 4096 留余量。
_MAX_TOKENS = 4096

_PROMPT = """这是用户电脑屏幕的截图。
{question}

请仔细观察截图后回答，输出两部分：
1. 界面概况：2-4 句话说明这是什么软件、用户正在做什么；
2. 关键文字：逐条列出截图上与问题相关的文字（标题、按钮、菜单、数据、报错、
   文件名等）。数字、英文、路径必须原样照抄，不要改写或翻译。

只输出这两部分，不要复述这段说明，不要加开场白。"""

_NO_Q = "用户没有明确提问，请说明屏幕上是什么内容。"


# 最近一次调用的用量与耗时，供 status 和测试断言（尤其是 reasoning_tokens=0）。
LAST_USAGE: dict[str, object] = {}


def _ark_ok() -> bool:
    return bool(settings.ark_api_key.strip() and settings.ark_plan_base_url.strip())


def _sensenova_ok() -> bool:
    return bool(settings.vision_api_key.strip())


def vl_backend() -> str:
    """VL 档由谁实现：ark / sensenova / none。

    auto 时 ARK 优先：实测 2.1 秒且零推理 token，SenseNova 要 21-48 秒。
    """
    want = (settings.vision_vl_backend or "auto").strip().lower()
    if want == "ark":
        return "ark" if _ark_ok() else ("sensenova" if _sensenova_ok() else "none")
    if want == "sensenova":
        return "sensenova" if _sensenova_ok() else "none"
    if _ark_ok():
        return "ark"
    return "sensenova" if _sensenova_ok() else "none"


def available() -> bool:
    return vl_backend() != "none"


def _post(base_url: str, api_key: str, body: dict, timeout: float,
          label: str) -> str:
    headers = {
        "Authorization": f"Bearer {api_key}",
        "Content-Type": "application/json",
    }
    t0 = time.time()
    try:
        r = httpx.post(base_url.rstrip("/") + "/chat/completions",
                       headers=headers, json=body, timeout=timeout)
    except Exception as exc:                             # noqa: BLE001
        log.warning("vision(%s): 请求失败（%.0fms）：%s",
                    label, (time.time() - t0) * 1000, exc)
        return ""

    dt = (time.time() - t0) * 1000
    if r.status_code != 200:
        log.warning("vision(%s): HTTP %s（%.0fms）%s",
                    label, r.status_code, dt, r.text[:200])
        return ""
    try:
        d = r.json()
    except Exception:                                    # noqa: BLE001
        log.warning("vision(%s): 响应不是 JSON（%.0fms）", label, dt)
        return ""
    if "error" in d:
        log.warning("vision(%s): 上游报错 %s", label, str(d["error"])[:200])
        return ""

    try:
        msg = d["choices"][0]["message"]
    except (KeyError, IndexError, TypeError):
        log.warning("vision(%s): 响应结构异常（%.0fms）", label, dt)
        return ""

    text = (msg.get("content") or "").strip()
    usage = d.get("usage") or {}
    rt = (usage.get("completion_tokens_details") or {}).get("reasoning_tokens", 0)
    LAST_USAGE.clear()
    LAST_USAGE.update({
        "backend": label, "model": body.get("model"), "ms": round(dt),
        "reasoning_tokens": rt, "prompt_tokens": usage.get("prompt_tokens"),
        "completion_tokens": usage.get("completion_tokens"),
    })
    if not text:
        # 推理把 max_tokens 吃光：正文为空但不报错。必须显式记下来，
        # 否则上层会以为「模型说图里没内容」。
        log.warning("vision(%s): 正文为空（%.0fms，推理 %s tok，finish=%s）——"
                    "推理吃光了 max_tokens", label, dt, rt,
                    (d["choices"][0].get("finish_reason") if d.get("choices") else "?"))
        return ""

    log.info("vision(%s): 识图完成 %.0fms（推理 %s tok，正文 %d 字）",
             label, dt, rt, len(text))
    return text


def _compress(raw: bytes) -> tuple[bytes, str]:
    """压到合理尺寸再上传。

    实测瓶颈是推理不是传输（196KB→35KB 延迟几乎不变），但压缩能避免
    超出上游的图片大小限制，也顺手把超大 4K 截图降下来。
    """
    try:
        from PIL import Image

        img = Image.open(io.BytesIO(raw)).convert("RGB")
        w, h = img.size
        limit = settings.vision_max_side
        if limit > 0 and max(w, h) > limit:
            r = limit / max(w, h)
            img = img.resize((max(1, int(w * r)), max(1, int(h * r))), Image.LANCZOS)
        buf = io.BytesIO()
        img.save(buf, "JPEG", quality=75)
        return buf.getvalue(), "image/jpeg"
    except Exception as exc:                             # noqa: BLE001
        log.warning("vision: 图片压缩失败，按原图上传（%s）", exc)
        return raw, "image/jpeg"


def describe(image_b64: str, media_type: str = "image/jpeg",
             question: str = "") -> str:
    """让视觉模型看图，返回文字描述。失败返回空串，由上层退回 OCR。"""
    if not available():
        return ""
    try:
        raw = base64.b64decode(image_b64, validate=False)
    except Exception:                                    # noqa: BLE001
        return ""
    if not raw:
        return ""

    data, mime = _compress(raw)
    b64 = base64.b64encode(data).decode()
    q = (question or "").strip()
    prompt = _PROMPT.format(question=q if q else _NO_Q)

    b = vl_backend()
    if b == "none":
        return ""

    body = {
        "messages": [{
            "role": "user",
            "content": [
                {"type": "image_url",
                 "image_url": {"url": f"data:{mime};base64,{b64}"}},
                {"type": "text", "text": prompt},
            ],
        }],
        "max_tokens": _MAX_TOKENS,
    }

    if b == "ark":
        # 套餐的 doubao-seed-2-0-mini：minimal = 不思考（实测 reasoning_tokens=0）。
        # 这条链路只要截图描述，不需要推理链；开思考纯属加延迟。
        body["model"] = settings.ark_vision_model
        effort = (settings.ark_vision_effort or "").strip()
        if effort:
            body["reasoning_effort"] = effort
        return _post(settings.ark_plan_base_url, settings.ark_api_key, body,
                     settings.ark_vision_timeout, "ark")

    # SenseNova：推理预算只在显式配置时才传。空串 = 让模型自己决定。
    # 实测手动压低推理（none/low/minimal）会让它认错界面——把点点认成
    # Windows 相册、Edge、甚至 VS Code 里的文本文件。宁慢勿错。
    body["model"] = settings.vision_model
    effort = (settings.vision_effort or "").strip()
    if effort:
        body["reasoning_effort"] = effort
    return _post(settings.vision_base_url, settings.vision_api_key, body,
                 settings.vision_timeout, "sensenova")


def status() -> dict[str, object]:
    b = vl_backend()
    out: dict[str, object] = {
        "available": b != "none",
        "backend": b,
        "last_usage": dict(LAST_USAGE),
        "sensenova": {
            "model": settings.vision_model,
            "base_url": settings.vision_base_url,
            "effort": settings.vision_effort,
            "timeout": settings.vision_timeout,
        },
        "ark": {
            "model": settings.ark_vision_model,
            "base_url": settings.ark_plan_base_url,
            "effort": settings.ark_vision_effort,
            "timeout": settings.ark_vision_timeout,
        },
    }
    return out


__all__ = ["available", "describe", "status", "vl_backend", "LAST_USAGE"]
