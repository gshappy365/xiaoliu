"""上游适配器。

设计取舍：**透传，不重新编码。**

客户端说的就是 Anthropic Messages SSE，主流中转（DeepSeek / one-api / new-api /
LiteLLM / 官方 Anthropic）也都说这一套。所以服务器不解析、不重组内容块，
只做四件事：

1. 剥掉点点私有的 `diandian_credits`、`main_reasoning_effort` 等字段
2. 把 `model` 换成上游真实模型
3. 逐块转发上游 SSE 字节
4. 结束时补一帧 `data: [DONE]`（Anthropic 官方不发这帧，但客户端靠它退出循环）

这样协议兼容性由「同一方言」保证，而不是由我的重新编码正确性保证。
"""
from __future__ import annotations

import asyncio
import hashlib
import json
import logging
from collections import OrderedDict
from typing import Any, AsyncIterator

import httpx

from . import ocr, vision
from .config import settings

log = logging.getLogger("xiaoliu.upstream")

# 点点私有、上游不认识的顶层字段
# 客户端私有字段：上游不认识，一律剥掉。
#   vision_capable = 客户端说「当前选的对话模型自己能看图」，图片块要原样透传，
#                    别再跑 VL/OCR 转文本（见 apply_vision_policy 的 pass_through）。
_DROP_FIELDS = {"diandian_credits", "main_reasoning_effort", "vision_capable"}

DONE = b"data: [DONE]\n\n"


# ---------------------------------------------------------------- 图片处理
# 模板措辞很关键。踩过的坑：原来写「用户提供了一张截图（1280x720），已由视觉
# 模型看过并转成下面的文字」，spark-x2.6 看到「截图」两个字就直接回
# 「我目前无法直接查看图像」，完全无视后面那 680 字真实内容——同一个请求
# 反复测，5 次里只有 1 次能正常回答。
# 所以这里**不出现「图」「截图」「图片」「无法查看」**这些词，
# 只陈述「当前屏幕内容如下」，模型才会把它当成已知事实来用。
_VL_TEMPLATE = (
    "[当前屏幕内容如下（由视觉系统读取，真实准确）：\n"
    "{text}\n"
    "以上是画面上的实际内容，请直接据此回答，不要再要求用户描述。]"
)
_OCR_TEMPLATE = (
    "[当前屏幕上的文字如下（本机 OCR 读取）：\n"
    "{text}\n"
    "以上是画面上的实际文字，请直接据此回答，不要再要求用户描述。]"
)
_STRIP_TEMPLATE = (
    "[用户发来一张画面（{w}x{h}），但当前无法读取其内容，"
    "你只能看到这段说明。不要猜测画面内容；如需了解，请让用户用文字告诉你。]"
)

# 同一张截图会在连续几轮里重复出现：点点每轮重发完整历史，历史里的 tool_result
# 仍带着同一份 base64。实测 evidence/chat-dump：73 个图片实例只有 38 张唯一图片，
# 平均每张被识别 1.9 次，每次约 4 秒、约 2500 token。
#
# 日志里能直接看到（2026-10-28 15:41）：
#   15:41:18 chat 25 条消息，图 hash 2b8029d0abbb
#   15:41:22 vision(ark) 4056ms
#   15:41:43 chat 28 条消息，同一张图
#   15:41:47 vision(ark) 4100ms
#
# 键里带 question：VL 是带着用户的问题去看图的，问题不同描述就该不同，
# 不能只按图片 hash 复用。
_vl_cache: OrderedDict[tuple[str, str], str] = OrderedDict()
_VL_CACHE_MAX = 8


def _describe_cached(data: str, media: str, question: str) -> str:
    key = (hashlib.sha1(data.encode("utf-8")).hexdigest(), question)
    hit = _vl_cache.get(key)
    if hit is not None:
        _vl_cache.move_to_end(key)
        log.info("vision: 命中缓存，跳过 VL 调用（省约 4s、约 2500 token）")
        return hit
    text = vision.describe(data, media, question)
    if text:
        _vl_cache[key] = text
        while len(_vl_cache) > _VL_CACHE_MAX:
            _vl_cache.popitem(last=False)
    return text


def _last_user_text(msgs: list[Any]) -> str:
    """取最后一条用户消息的文本，作为视觉模型的提问。

    让 VL 带着用户的问题看图，而不是泛泛描述整张图——下游文本模型拿到
    「针对问题的答案」比拿到「图片描述」好用得多。
    """
    for m in reversed(msgs):
        if not isinstance(m, dict) or m.get("role") != "user":
            continue
        c = m.get("content")
        if isinstance(c, str):
            return c.strip()[:500]
        if isinstance(c, list):
            parts = [b.get("text", "") for b in c
                     if isinstance(b, dict) and b.get("type") == "text"]
            t = " ".join(p for p in parts if p).strip()
            if t:
                return t[:500]
    return ""


def _image_to_blocks(block: dict[str, Any],
                     question: str = "") -> list[dict[str, Any]]:
    """把 image 块换成模型能读的 text 块（或诚实占位）。"""
    src = block.get("source") or {}
    data = src.get("data") or ""
    media = src.get("media_type") or "image/jpeg"
    if src.get("type") != "base64" or not data:
        # url 型图片上游也不一定取得到，统一按「看不到」处理
        return [{"type": "text", "text": "[附带了一张图片（非 base64），无法处理]"}]

    w = h = 0
    try:
        import base64

        w, h = ocr.png_or_jpeg_dimensions(base64.b64decode(data, validate=False))
    except Exception:                                    # noqa: BLE001
        pass

    mode = settings.vision_mode
    if mode == "pass":
        return [block]

    # 1) 视觉模型优先。auto 模式下有配置就走，vl 模式强制走。
    if mode in ("auto", "vl") and vision.available():
        text = _describe_cached(data, media, question)
        if text:
            if len(text) > settings.ocr_max_chars:
                text = text[:settings.ocr_max_chars] + "\n[后文已截断]"
            log.info("vision: 图片 %sx%s → VL %d 字符", w or "?", h or "?", len(text))
            return [{"type": "text",
                     "text": _VL_TEMPLATE.format(w=w or "?", h=h or "?", text=text)}]
        if mode == "vl":
            # 用户明确要求用视觉模型，失败就诚实说看不到，不要偷偷换成 OCR
            log.warning("vision: VL 失败，vl 模式不回退 OCR")
            return [{"type": "text", "text": _STRIP_TEMPLATE.format(w=w or "?", h=h or "?")}]
        log.info("vision: VL 未返回内容，回退 OCR")

    # 2) 本地 OCR 兜底（PaddleOCR-VL 优先，其次 Windows OCR）
    if mode in ("auto", "ocr") and ocr.available():
        text = ocr.image_to_text(data, media)
        if text:
            if len(text) > settings.ocr_max_chars:
                text = text[:settings.ocr_max_chars] + "\n[后文已截断]"
            log.info("vision: 图片 %sx%s → OCR %d 字符（原 base64 %d 字符）",
                     w or "?", h or "?", len(text), len(data))
            return [{"type": "text",
                     "text": _OCR_TEMPLATE.format(text=text)}]
        log.info("vision: OCR 没识别出文字，退回占位说明")
        return [{"type": "text", "text": _STRIP_TEMPLATE.format(w=w or "?", h=h or "?")}]

    log.info("vision: 无 OCR 能力，图片按占位说明处理（%sx%s）", w or "?", h or "?")
    return [{"type": "text", "text": _STRIP_TEMPLATE.format(w=w or "?", h=h or "?")}]


def _transform_blocks(blocks: list[Any], question: str = "") -> list[Any]:
    out: list[Any] = []
    for b in blocks:
        if not isinstance(b, dict):
            out.append(b)
            continue
        t = b.get("type")
        if t == "image":
            out.extend(_image_to_blocks(b, question))
        elif t == "tool_result":
            inner = b.get("content")
            if isinstance(inner, list):
                nb = dict(b)
                nb["content"] = _transform_blocks(inner, question)
                out.append(nb)
            else:
                out.append(b)
        else:
            out.append(b)
    return out


def _count_images(msgs: list[Any]) -> int:
    """数一数这一轮到底带了几张图（含 tool_result 里嵌的）。只用于日志。"""
    total = 0
    for m in msgs:
        c = m.get("content") if isinstance(m, dict) else None
        if not isinstance(c, list):
            continue
        for b in c:
            if not isinstance(b, dict):
                continue
            if b.get("type") == "image":
                total += 1
            elif b.get("type") == "tool_result" and isinstance(b.get("content"), list):
                total += _count_images([{"content": b["content"]}])
    return total


def apply_vision_policy(body: dict[str, Any], pass_through: bool = False) -> dict[str, Any]:
    """按 VISION_MODE 处理所有图片块。

    上游不支持视觉时，这一步把「静默丢弃」变成「可读文本」或「诚实说明」，
    避免模型基于看不见的画面编造内容。

    pass_through=True 表示客户端已经确认「这一轮选的对话模型自己能看图」，
    auto 模式下就原样透传图片块。为什么不让服务端自己判断：模型清单在客户端
    （ModelCatalog），服务端要知道哪个 id 能看图就得再维护一份表，两份必然漂移。
    显式设成 ocr / vl / strip 时不理会这个标记 —— 那是人工排障用的强制档。
    """
    if settings.vision_mode == "pass":
        return body
    msgs = body.get("messages")
    if not isinstance(msgs, list):
        return body
    if pass_through and settings.vision_mode == "auto":
        count = _count_images(msgs)
        if count:
            # 这条日志是排障用的：直连之后 OCR/VL 日志一条都不会有，
            # 看这行才能确认「图到底有没有发出去」。
            log.info("vision: 直连透传 %d 张图（对话模型自己看，跳过 VL/OCR）", count)
        return body
    # 带着用户的问题去看图，比泛泛描述整张图有用
    question = _last_user_text(msgs)
    changed = False
    new_msgs = []
    for m in msgs:
        if not isinstance(m, dict):
            new_msgs.append(m)
            continue
        c = m.get("content")
        if isinstance(c, list) and any(
                isinstance(b, dict) and b.get("type") == "image" for b in c):
            nm = dict(m)
            nm["content"] = _transform_blocks(c, question)
            new_msgs.append(nm)
            changed = True
        elif isinstance(c, list) and any(
                isinstance(b, dict) and b.get("type") == "tool_result" for b in c):
            nm = dict(m)
            nm["content"] = _transform_blocks(c, question)
            new_msgs.append(nm)
            changed = True
        else:
            new_msgs.append(m)
    if not changed:
        return body
    out = dict(body)
    out["messages"] = new_msgs
    return out


def rewrite_request(body: dict[str, Any], model: str = "") -> dict[str, Any]:
    """把点点的请求体改写成上游能吃的 Anthropic Messages 请求。

    model 是 pick_upstream 算出来的最终模型名；不传就沿用客户端原值，
    再不行才落到默认上游的模型。
    """
    out = {k: v for k, v in body.items() if k not in _DROP_FIELDS}
    resolved = (model or "").strip() or (body.get("model") or "").strip() or settings.upstream_model
    out["model"] = resolved
    out["stream"] = True
    out.setdefault("max_tokens", 8192)
    # 顺序有讲究：思考策略要在加提示之前跑。提示里带「截图」「标注」这些词，
    # 先加的话会影响 _tool_call_reason 对下一轮的判断。
    out = apply_thinking_policy(out, body.get("main_reasoning_effort"), is_ark_model(resolved))
    # 客户端声明「这一轮的对话模型自己会看图」时，图片块原样透传，不再 VL/OCR 转文本。
    out = apply_vision_policy(out, pass_through=bool(body.get("vision_capable")))
    return apply_action_nudge(out)


# 客户端传的推理强度 → 上游 thinking.budget_tokens
_EFFORT_BUDGET = {"none": 0, "minimal": 0, "low": 512, "medium": 2048, "high": 8192}

# 客户端在「本轮没有附带截图」时会注入这句运行时状态。它同时意味着：模型
# 必须自己调 capture_screen 才能看到画面。
#
# 实测 spark-x2.6（同一份 14 条历史的请求，各跑 5 次）：
#   thinking disabled → capture_screen 只命中 1/5，其余回合只叙述「我去截一张」
#                       然后 end_turn，客户端拿不到工具调用，表现就是「没回信」
#   thinking enabled(budget 1024) → 5/5
#   thinking 不干预               → 4/5
#
# 所以思考不能一刀切关掉：普通聊天关掉保首字延迟，需要模型主动动手的回合必须开。
_NEED_TOOL_HINT = "本轮没有附带屏幕截图"
_SMART_BUDGET = 1024

# 方舟模型把推理强度拉到「深度」时给的思考预算。比 spark 的 8192 小一档：
# 客户端 max_tokens 也是 8192，budget 顶到上限会和输出额度打架。
_ARK_HIGH_BUDGET = 4096

# 指点/圈选这类工具被客户端拒绝时，返回体会带 recovery/refresh_capture
# （画面已变化，请重新截图）。模型若读不懂这条提示，会在旧截图上反复重试，
# 把客户端每回合的工具预算烧光，最后只能告诉用户「预算不够」。
# 这两类回合同样需要一点推理，所以也开思考。
_RECOVERY_HINTS = ("refresh_capture", "stale_capture")


def _message_text(m: dict[str, Any]) -> str:
    """只取消息里的文字部分，跳过 base64 图片，避免为了匹配字符串序列化整张图。"""
    c = m.get("content")
    if isinstance(c, str):
        return c
    parts: list[str] = []
    for b in c or []:
        if not isinstance(b, dict):
            continue
        t = b.get("type")
        if t == "text":
            parts.append(b.get("text") or "")
        elif t == "tool_result":
            cc = b.get("content")
            if isinstance(cc, str):
                parts.append(cc)
            elif isinstance(cc, list):
                for x in cc:
                    if isinstance(x, dict) and x.get("type") == "text":
                        parts.append(x.get("text") or "")
        elif t == "tool_use":
            parts.append(json.dumps(b.get("input") or {}, ensure_ascii=False))
    return "\n".join(parts)


def _tool_call_reason(body: dict[str, Any]) -> str:
    """这一轮是否必须由模型主动调工具；返回命中原因，空串表示不需要。

    只看最后几条消息，避免被历史里旧的状态串长期点亮。匹配不加引号，
    因为客户端有时会把 tool_result 再包一层字符串（引号会被转义）。
    """
    text = "\n".join(_message_text(m) for m in (body.get("messages") or [])[-4:])
    if _NEED_TOOL_HINT in text:
        return "本轮没有附带截图"
    for hint in _RECOVERY_HINTS:
        if hint in text:
            return f"上一轮工具被拒（{hint}）"
    return ""


def _needs_tool_call(body: dict[str, Any]) -> bool:
    return bool(_tool_call_reason(body))


# ---------------------------------------------------------------- 动作提示
# 目的：把「只说不做」拉回「动手」。
#
# 实测（2026-10-28，回放 evidence/chat-dump#160「那你能通过什么方式能够在我屏幕上
# 画一个正方形呢？」，同一请求各跑 5 次，只改这一句）：
#
#   原样              0/5 命中 capture_screen，思考 1470–20211 字，最长 107.5s
#   末尾加这句         5/5 命中，思考 231–862 字，最长 7.0s
#
# MISS 的正文能直接看到模型已经放弃：
#   「我没办法在你屏幕上永久画一个正方形，只能说点实话。」
#   「我能用的标注工具有四个：point_at_screen 能打点，highlight_screen_region 能画框…」
#
# 放弃的原因在历史里：那条会话有 8 次标注被客户端拒绝，错误码都是
# stale_capture（capture_expired / screen_changed）。模型据此归纳出「画不了」，
# 于是改成讲解工具。而它纠结得越久，截图越容易过期、又被拒一次，形成正反馈。
# 所以提示要同时说三件事：要演示不要解释、被拒是过期不是不能、别改口。
_ACTION_NUDGE = (
    "[用户要的是当场演示，不是解释工具能力。需要画面就先 capture_screen；"
    "标注被拒是因为截图过期，重新截图再标注即可，不要因此改口说画不了。"
    "不要只用文字描述工具能做什么，也不要用「已经画过」「只能做到这样」收尾——"
    "本轮必须真的发出一次工具调用。]"
)


def apply_action_nudge(body: dict[str, Any]) -> dict[str, Any]:
    """本轮需要模型主动动手时，在最后一条用户消息末尾加一句。

    只在 `_tool_call_reason` 命中时才加：那两种情况（本轮没有附带截图、
    上一轮工具被拒）正是模型最容易从「动手」滑向「讲解」的时刻。
    用户自己圈选带了画面的回合不加——那些回合模型看得到东西，催它反而多余。
    """
    if not _tool_call_reason(body):
        return body
    msgs = body.get("messages")
    if not isinstance(msgs, list):
        return body

    for i in range(len(msgs) - 1, -1, -1):
        m = msgs[i]
        if not isinstance(m, dict) or m.get("role") != "user":
            continue
        c = m.get("content")
        if isinstance(c, str):
            if _ACTION_NUDGE in c:          # 幂等：客户端重发同一轮不叠加
                return body
            new_content: Any = c + "\n\n" + _ACTION_NUDGE
        elif isinstance(c, list):
            if any(isinstance(b, dict) and b.get("type") == "text"
                   and _ACTION_NUDGE in (b.get("text") or "") for b in c):
                return body
            # tool_result 之后接一个 text 块是合法的 user 消息形状
            new_content = list(c) + [{"type": "text", "text": _ACTION_NUDGE}]
        else:
            return body
        out = dict(body)
        out["messages"] = list(msgs)
        nm = dict(m)
        nm["content"] = new_content
        out["messages"][i] = nm
        # 排障用：用户说「它光说不做」时，先确认这条到底有没有加进去
        log.info("action: 已加动作提示（%s）", _tool_call_reason(body))
        return out
    return body


def apply_thinking_policy(body: dict[str, Any], effort: Any, ark: bool = False) -> dict[str, Any]:
    """控制上游是否「先思考再回答」。

    推理模型默认会先吐一大段 thinking，正文要等几秒才出来。
    实测 spark-x2.6：开思考首正文 4716ms，关掉 1112ms。
    语音场景下用户等的就是「什么时候开口」，所以普通回合默认关掉。

    但「关掉」会让模型不肯调工具（见 _NEED_TOOL_HINT 处的实测），所以提供：
      off   = 一律关（旧行为）
      smart = 需要主动调工具的回合开 1024 token，其余关（推荐）
      on    = 不干预
      auto  = 按客户端 main_reasoning_effort 映射

    方舟的模型单独走一条：默认关掉（首字从 5s 降到 1.8s 左右），只有客户端把推理
    强度拉到「深度」才开。

    别学 smart 那条给方舟也开思考 —— 实测过，没用还慢：同一个「看看我屏幕上现在是
    什么」的回合，doubao-seed-2-1-pro 开 1024 思考首字 9527ms、关思考 2064ms，两次
    都不调工具、都回「我看不到你的屏幕，你可以圈选」。原因是**小六的客户端在运行时
    状态里明确写了「不允许模型自主抓屏，不要调用 capture_screen」**（见
    Xiaoliu.Core/Capture/VisualEvidence.cs），模型不调工具是照客户端的要求做，
    不是退化。smart 那条拐杖是给 spark 补「只说不做」的，方舟这边没有这个病。
    """
    if ark:
        out = dict(body)
        if str(effort or "").strip().lower() == "high":
            out["thinking"] = {"type": "enabled", "budget_tokens": _ARK_HIGH_BUDGET}
        else:
            out["thinking"] = {"type": "disabled"}
        return out

    mode = settings.upstream_thinking
    if mode == "on":
        return body
    out = dict(body)
    if mode == "smart":
        reason = _tool_call_reason(body)
        if reason:
            out["thinking"] = {"type": "enabled", "budget_tokens": _SMART_BUDGET}
            # 这条日志是排障用的：截图/指点类回合「模型只说不做」时，
            # 先确认思考到底有没有被打开、以及为什么打开。
            log.info("thinking: smart → enabled(%d)（%s）", _SMART_BUDGET, reason)
        else:
            out["thinking"] = {"type": "disabled"}
        return out
    if mode == "off":
        out["thinking"] = {"type": "disabled"}
        return out
    # auto：按客户端的 main_reasoning_effort 映射
    budget = _EFFORT_BUDGET.get(str(effort or "").strip().lower())
    if budget == 0:
        out["thinking"] = {"type": "disabled"}
    elif budget:
        out["thinking"] = {"type": "enabled", "budget_tokens": budget}
    return out


def _headers(api_key: str = "") -> dict[str, str]:
    h = {
        "Content-Type": "application/json",
        "Accept": "text/event-stream",
        "anthropic-version": "2023-06-01",
    }
    key = (api_key or settings.upstream_api_key or "").strip()
    if key:
        # 同时给两种头，兼容 Anthropic 官方与 OpenAI 风格中转
        h["x-api-key"] = key
        h["Authorization"] = f"Bearer {key}"
    h.update(settings.upstream_extra_headers)
    return h



# 方舟的模型 id 前缀。客户端设置里选的是 arkcli 列出来的 callable_model_id，
# 长得就是这几个样子；spark-x2.6 这类讯飞模型不在这里面，继续走默认上游。
_ARK_MODEL_PREFIXES = ("doubao-", "deepseek-", "qwen", "glm-", "kimi-")

# 点点（Diandian）客户端的对话模型 id 全部是「厂商名.模型名」这种形态，编译在
# 客户端 DLL 里（AiServiceCatalog.ChatModels），服务端改不了：
#     aliyun.qwen3.8-flash / aliyun.qwen3.8-max / aliyun.qwen3.7-plus
#     aliyun.glm-5.3-flash / aliyun.minimax-m3 / aliyun.kimi-k3
#     volcengine.doubao-seed-2.0-pro / deepseek.v4-flash-vision-exp
# 这些 id 在方舟上不存在，一个也匹配不上 _ARK_MODEL_PREFIXES，于是全部落到默认
# 上游（讯飞星火 spark-x2.6）。
#
# 为什么不能就这么用着（2026-11-04 实测，用户报「点点让它截图，一直没反应」）：
#   spark-x2.6 那一轮上游响应头就等了 36.0 秒、总耗时 44.8 秒，工具调用只命中
#   1/3；点点自己先超时把交互释放了，用户看到的就是「没反应」。
#   同一份请求体换成方舟 doubao：响应头 1.38 秒、总耗时 3.9 秒，工具调用稳定命中。
#   注意 httpx 的 "200 OK" 日志打的是**收到响应头**的时刻，响应体可能还在流式
#   传输里卡着，所以「上游返回了 200」不等于「客户端拿到内容了」。
#
# 所以这里把点点这一族模型统一映射到方舟的 doubao —— 跟小六用的是同一个模型，
# 方舟分支的 thinking 默认关掉、工具调用反而更稳（见 apply_thinking_policy 的
# 注释：spark 那条拐杖是给 spark 补「只说不做」的，方舟这边没有这个病）。
# ARK_API_KEY 失效时（套餐到期，当前的 profile 显示 2026-11-28）自动回落默认
# 上游，不会彻底不可用。
_POINT_ALIAS_PREFIXES = ("aliyun.", "volcengine.", "deepseek.")
_POINT_ALIAS_TARGET = "doubao-seed-2-1-pro-260915"


def is_ark_model(model: str) -> bool:
    m = (model or "").strip().lower()
    return bool(m) and m.startswith(_ARK_MODEL_PREFIXES)


def pick_upstream(model: str) -> tuple[str, str, str]:
    """按客户端选的模型决定上游，返回 (url, api_key, model)。

    为什么要有这一步：客户端设置页里列的是方舟的模型，而默认上游是讯飞星火，
    两家模型 id 互不相认。所以只要客户端明确点了方舟的模型，就把请求转到方舟的
    Anthropic 兼容端点，并把 model 原样发过去 —— 用户在界面上选了什么，真的就用什么。

    兜底：方舟没配 key（或端点被清空）时仍走默认上游，并记一条日志。
    这样删掉 ARK_API_KEY 就能一键退回旧行为，不用改代码。

    点点的模型 id 是另一族（aliyun.* 之类，见 _POINT_ALIAS_PREFIXES），方舟上
    没有，先映射到方舟的 doubao 再走同一套逻辑。
    """
    requested = (model or "").strip()
    ark_key = settings.ark_api_key.strip()
    ark_base = settings.ark_anthropic_base_url.strip()
    ark_ready = bool(ark_key and ark_base)

    # 点点这一族：先做别名映射，再按方舟模型处理。
    alias_target = ""
    if requested.lower().startswith(_POINT_ALIAS_PREFIXES):
        if ark_ready:
            alias_target = _POINT_ALIAS_TARGET
        else:
            log.warning("客户端要 %s，但没配 ARK_API_KEY / ARK_ANTHROPIC_BASE_URL，回落到 %s",
                        requested, settings.upstream_model)

    if alias_target:
        log.info("上游：方舟 %s（客户端的 %s 映射而来）", alias_target, requested)
        return (ark_base.rstrip("/") + "/v1/messages", ark_key, alias_target)

    if is_ark_model(requested):
        if ark_ready:
            return (ark_base.rstrip("/") + "/v1/messages", ark_key, requested)
        log.warning("客户端要 %s，但没配 ARK_API_KEY / ARK_ANTHROPIC_BASE_URL，回落到 %s",
                    requested, settings.upstream_model)
    return (settings.upstream_base_url.rstrip("/") + "/v1/messages",
            settings.upstream_api_key,
            settings.upstream_model)


async def stream_chat(body: dict[str, Any]) -> AsyncIterator[bytes]:
    """转发上游流式响应。上游已经发过 [DONE] 就不重复补。

    注意：`rewrite_request` 里有 OCR（本地同步调用 WinRT，约 200ms），
    必须在工作线程里跑 —— 一是别卡事件循环，二是 OCR 内部用
    `asyncio.run()`，在已有事件循环里直接调会抛
    `asyncio.run() cannot be called from a running event loop`。

    连接级失败会重试一次：这台机器经 `HTTPS_PROXY=127.0.0.1:7897` 出去，
    代理瞬断时 httpx 在代理 tunnel 的 `start_tls` 阶段直接抛 ConnectError，
    客户端那边只看到「模型流无效：provider_error」。这类错误在**还没向客户端
    吐出任何字节**时重试是安全的；一旦已经开始 yield，就不重试（否则会重播内容）。
    """
    # 客户端在设置里选的模型决定走哪家：方舟的 id 走方舟，其余走默认上游。
    url, api_key, upstream_model = pick_upstream(body.get("model", ""))
    if is_ark_model(upstream_model):
        log.info("上游：方舟 %s", upstream_model)
    else:
        log.info("上游：默认 %s（客户端要 %s）", upstream_model, body.get("model") or "-")

    payload = await asyncio.to_thread(rewrite_request, body, upstream_model)

    transient = (httpx.ConnectError, httpx.ConnectTimeout,
                 httpx.ReadError, httpx.ReadTimeout, httpx.RemoteProtocolError)
    last_exc: Exception | None = None
    for attempt in range(2):
        sent_any = False
        saw_done = False
        try:
            async with httpx.AsyncClient(timeout=settings.upstream_timeout) as client:
                async with client.stream("POST", url, json=payload,
                                         headers=_headers(api_key)) as resp:
                    if resp.status_code >= 400:
                        detail = (await resp.aread()).decode("utf-8", "replace")[:800]
                        raise UpstreamError(resp.status_code, detail)
                    async for chunk in resp.aiter_bytes():
                        if not chunk:
                            continue
                        sent_any = True
                        if b"[DONE]" in chunk:
                            saw_done = True
                        yield chunk
            if not saw_done:
                yield DONE
            return
        except transient as exc:
            last_exc = exc
            if sent_any or attempt >= 1:
                raise
            log.warning("upstream 连接失败（%s: %s），400ms 后重试一次",
                        type(exc).__name__, exc)
            await asyncio.sleep(0.4)

    if last_exc is not None:                             # pragma: no cover
        raise last_exc


class UpstreamError(RuntimeError):
    def __init__(self, status: int, detail: str):
        super().__init__(f"upstream {status}: {detail}")
        self.status = status
        self.detail = detail


def summarize(body: dict[str, Any]) -> dict[str, Any]:
    """只记结构，不记内容——自用也别把屏幕内容写进日志。"""
    msgs = body.get("messages") or []
    tools = body.get("tools") or []
    kinds: list[str] = []
    for m in msgs:
        c = m.get("content")
        if isinstance(c, str):
            kinds.append("str")
        elif isinstance(c, list):
            kinds.extend(str(b.get("type")) for b in c if isinstance(b, dict))
    return {
        "model": body.get("model"),
        "messages": len(msgs),
        "blocks": kinds,
        "tools": len(tools),
        "tool_names": [t.get("name") for t in tools if isinstance(t, dict)],
        "system_chars": len(body.get("system") or ""),
        "max_tokens": body.get("max_tokens"),
        "stream": body.get("stream"),
    }


def dumps(obj: Any) -> str:
    return json.dumps(obj, ensure_ascii=False, separators=(",", ":"))


def dump_body(body: dict[str, Any]) -> None:
    """把 /chat 请求体追加到 jsonl，用于排查客户端回传了什么。

    `summarize()` 只看结构（自用也不该把屏幕内容写进日志）；真出问题时又必须
    看到 tool_result 的原文，所以单独给一个显式开关 CHAT_DUMP_DIR，
    默认关闭，只在排查时打开。
    """
    import os
    import time

    d = os.environ.get("CHAT_DUMP_DIR", "").strip()
    if not d:
        return
    try:
        os.makedirs(d, exist_ok=True)
        path = os.path.join(d, "chat-bodies.jsonl")
        with open(path, "a", encoding="utf-8") as f:
            f.write(dumps({"ts": time.strftime("%H:%M:%S"), "body": body}) + "\n")
    except Exception:                                      # noqa: BLE001
        pass


def tool_results(body: dict[str, Any]) -> list[dict[str, Any]]:
    """抽出请求体里所有 tool_result 块，便于日志里直接看客户端回了什么。"""
    out = []
    for m in body.get("messages") or []:
        c = m.get("content")
        if not isinstance(c, list):
            continue
        for b in c:
            if isinstance(b, dict) and b.get("type") == "tool_result":
                out.append(b)
    return out
