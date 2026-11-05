#!/usr/bin/env python3
"""桩契约测试：验证响应体满足客户端 NativeRuntimeParameters.Decode 的校验。

这些校验是从客户端 IL 里读出来的，不是猜的：

  Diandian.Windows.Core.Authentication.NativeRuntimeParameters::Decode
    - body 长度 <= 8192
    - 顶层有 "values"
    - values 里 6 个键都存在且是 int
    - scope == "diandian"
    - version >= 1
    - updated_at 能按 "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK" 解析
    - 1000 <= PUBLIC_CLIENT_REQUEST_TIMEOUT_MS <= 120000
    - 1000 <= PUBLIC_CLIENT_RESOURCE_TIMEOUT_MS <= 120000
    - （其余几个键有各自的区间校验）

  NativeRuntimeParameterStore+<RefreshAsync>d__11::MoveNext
    - 同一个 version 下，updated_at 与 values 必须与上一次响应完全一致，
      否则抛 NativeParameterException（UI：「身份参数暂时不可用，请稍后重试」）

最后这条是最容易踩的：updated_at 用「当前时间」会让客户端每 5 秒刷新一次就崩一次。

    python tests/test_stub_contract.py
"""
from __future__ import annotations

import datetime as dt
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from app import stubs  # noqa: E402

FAILED: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    if cond:
        print(f"  [ok]   {name}")
    else:
        print(f"  [FAIL] {name}  {detail}")
        FAILED.append(name)


# 客户端用的 .NET 格式串：yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK
def parse_net_datetime(text: str) -> dt.datetime:
    """按 .NET 的 FFFFFFFK 语义解析。

    FFFFFFF = 1~7 位小数且可整体省略；K = 'Z' / '+08:00' / 空。
    Python 的 %f 要求正好 6 位，所以手工拆。
    """
    core, _, tz = text.partition(".")
    if tz:
        frac, _, rest = tz.partition("Z") if "Z" in tz else (tz, "", "")
        frac = frac[:6].ljust(6, "0")
    else:
        frac, rest = "000000", ""
    base = dt.datetime.strptime(core, "%Y-%m-%dT%H:%M:%S")
    return base.replace(microsecond=int(frac))


def test_runtime_parameters() -> None:
    print("\n== runtime-parameters ==")
    p = stubs.runtime_parameters()
    raw = json.dumps(p, ensure_ascii=False).encode()

    check("响应体 <= 8192 字节", len(raw) <= 8192, f"{len(raw)} 字节")
    check("scope == 'diandian'", p.get("scope") == "diandian", repr(p.get("scope")))
    check("version >= 1", isinstance(p.get("version"), int) and p["version"] >= 1,
          repr(p.get("version")))
    try:
        parse_net_datetime(p["updated_at"])
        ok = True
    except Exception as exc:                              # noqa: BLE001
        ok = False
        check("updated_at 可解析", False, f"{exc} :: {p.get('updated_at')!r}")
    if ok:
        check("updated_at 可解析", True)

    vals = p.get("values") or {}
    check("values 存在且是对象", isinstance(vals, dict))

    keys = [
        "PUBLIC_CLIENT_REQUEST_TIMEOUT_MS",
        "PUBLIC_CLIENT_RESOURCE_TIMEOUT_MS",
        "USER_TOKEN_REFRESH_SKEW_SECONDS",
        "AUTHORIZATION_TRANSACTION_TTL_SECONDS",
        "PARAMETER_REFRESH_INTERVAL_MS",
        "PARAMETER_MAX_STALE_MS",
    ]
    for k in keys:
        v = vals.get(k)
        check(f"values.{k} 是 int", isinstance(v, int), repr(v))

    for k in ("PUBLIC_CLIENT_REQUEST_TIMEOUT_MS", "PUBLIC_CLIENT_RESOURCE_TIMEOUT_MS"):
        v = vals.get(k, 0)
        check(f"{k} ∈ [1000, 120000]", isinstance(v, int) and 1000 <= v <= 120000, repr(v))

    # 刷新间隔必须小于有效期，否则客户端来不及刷新快照
    check("RefreshIntervalMs < valid_for_ms",
          vals.get("PARAMETER_REFRESH_INTERVAL_MS", 10 ** 9) < p.get("valid_for_ms", 0))


def test_stability() -> None:
    """同一个 version 下必须逐字节稳定 —— 这是 NativeParameterException 的触发点。"""
    print("\n== 稳定性（同 version 下必须不变）==")
    a = stubs.runtime_parameters()
    b = stubs.runtime_parameters()
    check("两次调用 updated_at 相同", a["updated_at"] == b["updated_at"],
          f"{a['updated_at']!r} vs {b['updated_at']!r}")
    check("两次调用 values 相同", a["values"] == b["values"])
    check("两次调用 version 相同", a["version"] == b["version"])
    check("两次调用整体序列化相同",
          json.dumps(a, sort_keys=True) == json.dumps(b, sort_keys=True))


def test_account_identity() -> None:
    """token 的 sub 必须和 lifecycle/limits 里的账户 id 一致，否则会话被判无效。"""
    print("\n== 账户身份一致性 ==")
    import base64

    tok = stubs.jwt()
    payload_b64 = tok.split(".")[1]
    payload_b64 += "=" * (-len(payload_b64) % 4)
    payload = json.loads(base64.urlsafe_b64decode(payload_b64))
    check("access_token 的 sub == ACCOUNT_ID", payload.get("sub") == stubs.ACCOUNT_ID,
          repr(payload.get("sub")))
    check("lifecycle.accountId == ACCOUNT_ID",
          stubs.lifecycle()["data"]["accountId"] == stubs.ACCOUNT_ID)
    check("credits.limits.userId == ACCOUNT_ID",
          stubs.credits()["data"]["limits"]["userId"] == stubs.ACCOUNT_ID)


def test_matches_real_capture() -> None:
    """和真实服务端的响应逐字段比对。

    这是最强的一条：只要和抓包完全一致，客户端的任何校验都不可能拦住我们。
    抓包在 probe/captures/endpoints.json（由 probe/extract_endpoints.py 生成）。
    """
    print("\n== 与真实抓包比对 ==")
    here = os.path.dirname(os.path.abspath(__file__))
    cap_path = os.path.join(here, "..", "..", "probe", "captures", "endpoints.json")
    cap_path = os.path.normpath(cap_path)
    if not os.path.exists(cap_path):
        print("  [skip] 找不到抓包文件")
        return
    cap = json.load(open(cap_path, encoding="utf-8"))
    real = json.loads(cap["GET /diandian/v1/public/runtime-parameters"]["body"])
    ours = stubs.runtime_parameters()
    check("runtime-parameters 与真实响应完全一致", real == ours,
          f"\n    真实: {json.dumps(real, sort_keys=True, ensure_ascii=False)}"
          f"\n    我方: {json.dumps(ours, sort_keys=True, ensure_ascii=False)}")


def main() -> int:
    test_runtime_parameters()
    test_stability()
    test_account_identity()
    test_matches_real_capture()
    print()
    if FAILED:
        print(f"失败 {len(FAILED)} 项：{FAILED}")
        return 1
    print("全部通过")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
