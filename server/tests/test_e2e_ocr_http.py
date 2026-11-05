"""真实 HTTP 全链路：截图 → 服务端 OCR → spark-x2.6 → SSE 回答。

前面的测试都在进程内，这个脚本走完整的网络栈：
    POST https://127.0.0.1/diandian/proxy/chat
    （Anthropic Messages + tool_result 里塞 image 块，完全模拟点点）

需要服务端以 VISION_MODE=ocr 启动才能验证 paddle 路径：
    VISION_MODE=ocr bash server/start-real.sh
    python tests/test_e2e_ocr_http.py

断言重点不是「模型答得好不好」，而是**模型有没有看到真实画面内容**：
如果 OCR 失败，spark-x2.6 会反问「你屏幕上是什么」——这正是要避免的。
"""
from __future__ import annotations

import base64
import json
import os
import ssl
import sys
import time
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

IMG = os.path.join(os.path.dirname(os.path.dirname(HERE)),
                   "evidence", "selfcheck-capture-1931.jpg")

URL = os.environ.get("E2E_URL", "https://127.0.0.1/diandian/proxy/chat")
TOKEN = os.environ.get("E2E_TOKEN", "e2e-test-token")

# 图上的**特征词**。不要用「点点」这种软件名 —— 实测 spark-x2.6 拿到 353 字
# 准确的 OCR 文本后，仍然会说「无法判断是什么软件」，但会老老实实把四个测试项
# 列出来。所以判断「模型有没有看到画面」要看这些专有名词，而不是软件名。
EXPECT = ["WGC", "WASAPI", "Overlay", "热键"]
# 这些是模型"看不见"时的典型反应，出现任何一条就是 OCR 链断了
BLIND = ["无法查看", "无法看到图片", "请描述", "我无法读取", "你屏幕上是什么"]

fails = 0


def check(name: str, cond: bool, detail: str = "") -> None:
    global fails
    if not cond:
        fails += 1
    print(f"  [{'PASS' if cond else 'FAIL'}] {name}" + (f"  {detail}" if detail else ""))


def build(b64: str, question: str) -> dict:
    return {
        "model": "claude-sonnet-4",
        "max_tokens": 1024,
        "stream": True,
        "messages": [
            {"role": "user", "content": question},
            {"role": "assistant", "content": [
                {"type": "tool_use", "id": "tu_1", "name": "capture_screen",
                 "input": {}}]},
            {"role": "user", "content": [
                {"type": "tool_result", "tool_use_id": "tu_1", "content": [
                    {"type": "image", "source": {
                        "type": "base64", "media_type": "image/jpeg", "data": b64}},
                ]}]},
        ],
    }


def stream_text(body: dict, timeout: float = 180.0) -> tuple[str, float]:
    """发请求，把 SSE 里的 text_delta 拼起来。"""
    data = json.dumps(body).encode()
    req = urllib.request.Request(URL, data=data, method="POST", headers={
        "Content-Type": "application/json",
        "Authorization": f"Bearer {TOKEN}",
        "Accept": "text/event-stream",
    })
    ctx = ssl._create_unverified_context()
    out: list[str] = []
    t0 = time.time()
    with urllib.request.urlopen(req, context=ctx, timeout=timeout) as resp:
        for raw in resp:
            line = raw.decode("utf-8", "replace").strip()
            if not line.startswith("data:"):
                continue
            payload = line[5:].strip()
            if not payload or payload == "[DONE]":
                continue
            try:
                ev = json.loads(payload)
            except json.JSONDecodeError:
                continue
            if ev.get("type") == "content_block_delta":
                d = ev.get("delta") or {}
                if d.get("type") == "text_delta":
                    out.append(d.get("text") or "")
    return "".join(out), time.time() - t0


def main() -> int:
    print("=" * 70)
    print("真实 HTTP 全链路（OCR 模式）")
    print("=" * 70)
    print(f"  {URL}")

    if not os.path.isfile(IMG):
        print(f"找不到测试图片：{IMG}")
        return 2

    # 先确认服务端确实是 ocr 模式
    try:
        ctx = ssl._create_unverified_context()
        with urllib.request.urlopen("https://127.0.0.1/", context=ctx, timeout=20) as r:
            health = json.load(r)
        v = health.get("vision") or {}
        print(f"  vision.mode = {v.get('mode')}")
        print(f"  paddle = {v.get('paddle')}")
        check("服务端是 ocr 模式", v.get("mode") == "ocr", str(v.get("mode")))
        # worker 是独立子进程，running 才说明模型真的在
        p = v.get("paddle") or {}
        check("OCR worker 在跑", p.get("worker") == "running", str(p.get("worker")))
        check("没有降级到 Windows OCR", not p.get("poisoned"),
              str(p.get("poison_reason") or ""))
    except Exception as exc:                             # noqa: BLE001
        print(f"  连不上服务端：{exc}")
        return 2

    b64 = base64.b64encode(open(IMG, "rb").read()).decode()
    question = "帮我看看这个界面上是什么软件，有哪些测试项"

    print("\n[1] 发请求（含 image 块）")
    text, dt = stream_text(build(b64, question))
    print(f"  （{dt:.1f}s，回答 {len(text)} 字）")

    check("拿到回答", len(text) > 10, f"{len(text)} 字")
    hits = [w for w in EXPECT if w in text]
    check("回答里命中图上真实内容", len(hits) >= 2, f"{hits}")
    blind = [b for b in BLIND if b in text]
    check("没有「看不见」的典型反应", not blind, f"{blind or '无'}")
    check("端到端 < 60s", dt < 60, f"{dt:.1f}s")

    print("\n  ---- 回答（前 400 字）----")
    print("  " + text[:400].replace("\n", "\n  "))

    print("\n" + "=" * 70)
    print("ALL PASS" if fails == 0 else f"{fails} 项失败")
    return 1 if fails else 0


if __name__ == "__main__":
    raise SystemExit(main())
