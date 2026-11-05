#!/usr/bin/env python3
"""以「点点的视角」做端到端：真域名 + 真 TLS + 真证书链 + 原生解码器。

hosts 已把 api.botlearn.com.cn 指到 127.0.0.1，所以这里不伪造 Host，
用的就是点点将来会走的同一条路。

    python tests/e2e_tls.py [port]
"""
from __future__ import annotations

import json
import os
import ssl
import sys

import httpx

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, ROOT)

from tests.test_against_native_decoder import feed  # noqa: E402

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8443
HOST = "api.botlearn.com.cn"
CA = os.path.join(os.path.dirname(ROOT), "probe", "certs", "ca.crt")


def strict_off_context() -> ssl.SSLContext:
    """用本地 CA 验链，但关掉 Python 3.13 默认开启的 X.509 严格模式。

    严格模式要求叶子证书有 AKI、根证书有 SKI。我们的根证书是第一版生成的，
    没有 SKI；补上它就得重签根证书，那样已经装进受信任存储的那份就作废了。

    取舍：**根证书不动**（点点用 .NET/schannel，本来就宽容，实测通过），
    只在测试端放宽。服务端叶子证书已补 AKI（probe/reissue_server_cert.py）。
    """
    ctx = ssl.create_default_context(cafile=CA)
    ctx.verify_flags &= ~ssl.VERIFY_X509_STRICT
    ctx.check_hostname = True
    ctx.verify_mode = ssl.CERT_REQUIRED
    return ctx

BODY = {
    "model": "aliyun.qwen3.8-flash",
    "max_tokens": 8192,
    "stream": True,
    "system": "你是小刘。",
    "messages": [{"role": "user", "content": [{"type": "text", "text": "看看我的屏幕"}]}],
    "tools": [{"name": "capture_screen", "description": "截屏",
               "input_schema": {"type": "object",
                                "properties": {"reason": {"type": "string"}},
                                "required": ["reason"]}}],
}


def main() -> int:
    base = f"https://{HOST}:{PORT}"
    print(f"目标 {base}（hosts 指向 127.0.0.1，证书由本地 CA 签）")

    with httpx.Client(verify=strict_off_context(), timeout=60) as c:
        r = c.get(f"{base}/diandian/proxy/healthz")
        print(f"  healthz      -> {r.status_code} {r.json()['status']}")

        r = c.get(f"{base}/diandian/proxy/capabilities")
        print(f"  capabilities -> {r.status_code} {len(r.json()['chat_models'])} 个模型")

        r = c.post(f"{base}/diandian/proxy/chat", json=BODY,
                   headers={"Authorization": "Bearer selfhost",
                            "Accept": "text/event-stream"})
        print(f"  chat         -> {r.status_code} {r.headers.get('content-type')} "
              f"{len(r.content)}B")
        raw = r.content

    outs, finished = feed([raw], "sse")
    text = "".join(s for o in outs for s in o)
    print(f"\n[无工具] finished={finished} text={text!r}")

    outs, _ = feed([raw], "agent")
    snap = outs[-1]
    tools = [b for b in snap["content"] if b["type"] == "tool_use"]
    print(f"[有工具] finished={snap['finished']} stopReason={snap['stopReason']} "
          f"error={snap['error']} tools={[(t['name'], t['input']) for t in tools]}")

    assert finished is True and text, "sse 解码器没吃下"
    assert snap["finished"] is True and snap["error"] is None, snap
    print("\n以点点视角通过：真域名 + 真 TLS + 原生解码器全部接受。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
