"""自建服务器的响应 stub。

所有形状都来自真实抓包（probe/captures/endpoints.json），只改了额度数字。
客户端对这些字段的解析是强类型的，字段名/类型不能改。
"""
from __future__ import annotations

import json
import time
import uuid
from typing import Any

# 自用：额度给一个大到用不完的数，避免客户端弹「点数不足」
BIG_CREDITS = "999999999"

# 必须与客户端本地已登录的账户一致。
# 依据：抓包里这个 UUID 同时出现在 `limits.userId`、`lifecycle.accountId`
# 和 id_token 的 `sub` 上。若返回别的值，点点会判定「会话不属于本账户」，
# 进而回退到交互式 OIDC 登录（GET /oidc/auth），而那条路自建服务器撑不起来。
ACCOUNT_ID = "b7550dc7-779d-4993-ad4d-7e794f881b09"
ACCOUNT_NAME = "BotLearn User-4f881b09"

MODELS = [
    "aliyun.qwen3.8-flash",
    "aliyun.qwen3.8-max",
    "aliyun.qwen3-vl-flash",
    "aliyun.qwen-vl-max",
    "aliyun.glm-5.3-flash",
    "aliyun.minimax-m3",
    "aliyun.kimi-k3",
    "volcengine.doubao-seed-vision",
    "volcengine.doubao-seed-2.0-pro",
]

VOICES = [
    "volcengine.xiaohe-2.0",
    "volcengine.vivi-2.0",
    "volcengine.xiaotian-2.0",
    "volcengine.yunzhou-2.0",
    "aliyun.longxiaochun-v2",
    "aliyun.longxiaocheng-v2",
    "aliyun.longyumi-v2",
    "aliyun.longanhuan",
    "aliyun.longanyang",
    "aliyun.longshu-v2",
]


def rid() -> str:
    return str(uuid.uuid4())


def b64u(raw: bytes) -> str:
    import base64
    return base64.urlsafe_b64encode(raw).decode().rstrip("=")


def jwt(scope: str = "openid proxy:invoke", ttl: int = 86400,
        aud: str = "botlearn-clicky-proxy", typ: str = "at+jwt") -> str:
    """造一个 JWT 形状的 token。

    为什么要费这个劲：真实服务端发的是 ES256 JWT（`eyJhbGci…`），客户端
    很可能要 base64 解出 payload 读 `exp` 来判断会话有效期。给裸串
    `selfhost-access-token` 会让它解不出来——实测表现为「正常退出、不报错」。

    签名是假的（固定填充）。客户端没有 JWKS 可用，不做验签；若哪天它开始验签，
    这里会立刻暴露成鉴权失败而不是静默退出，反而更好查。
    """
    now = int(time.time())
    header = {"alg": "ES256", "typ": typ, "kid": "prod-cn-2026-08-08-01"}
    payload = {
        "auth_time": now - 3600,
        "acr": "urn:botlearn:acr:otp",
        "amr": ["sms_otp"],
        "botlearn_phone_verification_required": False,
        "botlearn_is_anonymous": False,
        "botlearn_session_version": 1,
        "jti": rid(),
        "sub": ACCOUNT_ID,
        "iat": now,
        "exp": now + ttl,
        "scope": scope,
        "client_id": "diandian-windows",
        "iss": "https://user.botlearn.com.cn/oidc",
        "aud": aud,
    }
    signing_input = (b64u(json.dumps(header, separators=(",", ":")).encode())
                     + "." +
                     b64u(json.dumps(payload, separators=(",", ":")).encode()))
    # 64 字节占位签名（ES256 的 R||S 长度），不是真签名
    sig = b64u(b"\x00" * 64)
    return f"{signing_input}.{sig}"


def envelope(data: Any) -> dict[str, Any]:
    """`/v1/*` 系列统一走 {"data": ..., "requestId": ...} 信封。"""
    return {"data": data, "requestId": rid()}


def iso_now() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%S.000Z", time.gmtime())


# ---------------------------------------------------------------- /proxy/*

def healthz() -> dict[str, Any]:
    return {
        "status": "ok",
        "release_line": "prod",
        "identity_authorities": {"botlearn_user": True, "service_identity": True},
    }


def capabilities(models: list[str] | None = None) -> dict[str, Any]:
    return {
        "transcription": [{"id": "volcengine", "ready": True}],
        "chat_models": [{"id": m, "ready": True} for m in (models or MODELS)],
        "speech_voices": [{"id": v, "ready": True} for v in VOICES],
    }


# ---------------------------------------------------------------- /v1/*

def _policy(models: list[str] | None = None) -> dict[str, Any]:
    ms = models or MODELS
    common = {"models": ms, "maxSeconds": 240, "maxRequestCharacters": 8388608}
    return {
        "version": 2,
        "advanced": {**common, "price": "50", "maxChatCalls": 16,
                     "maxOutputTokens": 16384},
        "standard": {**common, "price": "10", "maxChatCalls": 8,
                     "maxOutputTokens": 8192},
        "course_teaching": {**common, "price": "5", "maxChatCalls": 8,
                            "maxOutputTokens": 8192},
        "dailyCredits": BIG_CREDITS,
    }


def credits(models: list[str] | None = None) -> dict[str, Any]:
    now = iso_now()
    return envelope({
        "unit": "credit",
        "available": BIG_CREDITS,
        "held": "0",
        "revision": "1",
        "serverTime": now,
        "policy": _policy(models),
        "limits": {
            "userId": ACCOUNT_ID,
            "revision": "0",
            "defaultsRevision": "1",
            "anchorAt": now,
            "period24h": {
                "limit": BIG_CREDITS, "configuredLimit": None,
                "platformLimit": BIG_CREDITS, "inherited": True,
                "source": "platform", "used": "0", "held": "0",
                "remaining": BIG_CREDITS, "startsAt": now, "resetsAt": now,
            },
            "period30d": {
                "limit": BIG_CREDITS, "configuredLimit": None,
                "platformLimit": BIG_CREDITS, "inherited": True,
                "source": "platform", "used": "0", "held": "0",
                "remaining": BIG_CREDITS, "startsAt": now, "resetsAt": now,
            },
            "asOf": now,
            "coverage": "admitted_interactions",
        },
        "grants": [],
    })


def quote(model: str = "aliyun.qwen3.8-flash", tier: str = "standard") -> dict[str, Any]:
    return envelope({
        "quoteId": rid(),
        "price": "10" if tier == "standard" else "50",
        "tier": tier,
        "model": model,
        "policyVersion": 2,
        "limits": {"maxChatCalls": 8, "maxOutputTokens": 8192,
                   "maxRequestCharacters": 8388608, "maxSeconds": 240},
        "expiresAt": iso_now(),
    })


def activation() -> dict[str, Any]:
    return envelope({
        "status": "active",
        "source": "selfhost",
        "activatedAt": iso_now(),
        "welcomeGrant": {"id": rid(), "amount": BIG_CREDITS, "expiresAt": iso_now()},
        "allowance": {"limit": 9999, "generated": 0, "remaining": 9999,
                      "paused": False, "revision": "0"},
        "policy": {"configured": True, "enabled": True, "mode": "selfhost",
                   "acquisitionInstructions": "", "acquisitionUrl": "",
                   "supportUrl": "", "revision": "1", "allowance": 9999,
                   "welcomeCredits": BIG_CREDITS, "rewardCredits": BIG_CREDITS,
                   "welcomeMonths": 120, "rewardMonths": 120,
                   "cutoverAt": iso_now()},
        "serverTime": iso_now(),
    })


def lifecycle() -> dict[str, Any]:
    return envelope({
        "accountId": ACCOUNT_ID,
        "generation": 1,
        "state": "active",
        "closedAt": None,
    })


def profile() -> dict[str, Any]:
    return envelope({"displayName": ACCOUNT_NAME, "avatarUrl": None})


def payments_orders() -> dict[str, Any]:
    return envelope({"items": [], "nextCursor": None})


def payments_credits() -> dict[str, Any]:
    return envelope({"items": [], "nextCursor": None})


def payments_products() -> dict[str, Any]:
    return envelope({"items": [], "nextCursor": None})


def payments_capabilities() -> dict[str, Any]:
    return envelope({"channels": [], "enabled": False})


def transactions() -> dict[str, Any]:
    return envelope({"items": [], "nextCursor": None})


# ---------------------------------------------------------------- 运行时参数

# 这两个必须是**常量**，不能是「现在」。
#
# 依据：客户端 NativeRuntimeParameterStore.RefreshAsync 在拿到新响应后做一致性校验
# （Diandian.Windows.Core.dll，<RefreshAsync>d__11::MoveNext，IL 0x014a-0x01ac）：
#
#     if (fresh.Version != previous.Policy.Version)  → 接受
#     else if (fresh.UpdatedAt != previous.Policy.UpdatedAt
#              || fresh.Values   != previous.Policy.Values) → throw NativeParameterException
#
# 也就是同一个 version 下，updated_at 和 values 必须逐字稳定。
# 之前这里返回 iso_now()，于是客户端每 5 秒一次的刷新都会命中「version 相同但
# updated_at 变了」→ 抛 NativeParameterException → UI 显示
# 「身份参数暂时不可用，请稍后重试」。启动后约 57 秒（快照过期）开始全面失败。
#
# 真实服务端返回的 updated_at 是参数集发布时间（抓包里是 2026-09-16T14:49:13.246Z），
# 天然稳定。这里照抄。
PARAMETER_UPDATED_AT = "2026-09-16T14:49:13.246Z"
PARAMETER_VERSION = 2
PARAMETER_VALID_FOR_MS = 59073


def runtime_parameters() -> dict[str, Any]:
    return {
        "scope": "diandian",
        "version": PARAMETER_VERSION,
        "updated_at": PARAMETER_UPDATED_AT,
        "valid_for_ms": PARAMETER_VALID_FOR_MS,
        "values": {
            "PUBLIC_CLIENT_REQUEST_TIMEOUT_MS": 15000,
            "PUBLIC_CLIENT_RESOURCE_TIMEOUT_MS": 30000,
            "USER_TOKEN_REFRESH_SKEW_SECONDS": 30,
            "AUTHORIZATION_TRANSACTION_TTL_SECONDS": 300,
            "PARAMETER_REFRESH_INTERVAL_MS": 5000,
            "PARAMETER_MAX_STALE_MS": 60000,
        },
    }


def settlement(interaction_id: str, state: str = "settled") -> dict[str, Any]:
    """结算回执。

    形状来自客户端 `CreditSettlementResponse.Validate`（IL 0x0000-0x008e）：

        data.interactionId  必须能被 Guid.Parse，且等于本轮交互的 id
        data.state          必须是 "settled" 或 "released"
        data.reason         不能是 "settlement_pending"
                            （否则客户端判「结算未确认」，抛 credits_settlement_pending）

    为什么不像抓包那样返回 404 `credits_write_rejected`：那个 404 是真实服务端在
    「这次写入被拒」时的错误分支（抓包恰好撞上了）。客户端拿到 404 会走到
    `Validate` 里的 `root.GetProperty("data")` 抛 KeyNotFound，再被上层兜成
    `CreditsException`，而错误码 `credits_write_rejected` 不在 Describe 的映射表里，
    于是显示兜底文案「额度服务暂不可用，请稍后重试。」——正是我们遇到的那条。
    自建服务器没有真实计费，直接给一份合法的成功回执最干净。
    """
    return envelope({
        "interactionId": interaction_id,
        "state": state,
        "reason": "selfhost",
    })


def find_key(obj: Any, key: str) -> str | None:
    """在任意嵌套的 JSON 里找第一个名为 key 的字符串值。

    结算请求体的确切形状没抓到（只抓了响应），与其猜是顶层还是包在 data 里，
    不如递归找——多花几微秒，换掉一整类猜测。
    """
    if isinstance(obj, dict):
        v = obj.get(key)
        if isinstance(v, str):
            return v
        for v in obj.values():
            got = find_key(v, key)
            if got:
                return got
    elif isinstance(obj, list):
        for v in obj:
            got = find_key(v, key)
            if got:
                return got
    return None


def metrics_ack() -> dict[str, Any]:
    return {"accepted": 1}
