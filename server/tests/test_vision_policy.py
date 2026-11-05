"""视觉策略回归测试。

核心断言：**图片绝不能静默消失**。
要么变成 OCR 文本（模型能读），要么变成一句诚实的「看不到」——
两者都必须留下痕迹，否则模型会基于看不见的画面编造屏幕内容。

用法：python tests/test_vision_policy.py
"""
from __future__ import annotations

import asyncio
import base64
import copy
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from app import ocr, upstream                        # noqa: E402
from app.config import settings                      # noqa: E402

IMG = os.path.join(os.path.dirname(os.path.dirname(HERE)),
                   "evidence", "selfcheck-capture-1931.jpg")

fails = 0


def check(name: str, cond: bool, detail: str = "") -> None:
    global fails
    mark = "PASS" if cond else "FAIL"
    if not cond:
        fails += 1
    print(f"  [{mark}] {name}" + (f"  {detail}" if detail else ""))


def body_with_image(b64: str) -> dict:
    return {
        "model": "aliyun.qwen3.8-flash",
        "max_tokens": 8192,
        "stream": True,
        "system": "你是桌面助手",
        "main_reasoning_effort": "medium",
        "diandian_credits": {"interactionId": "x", "quoteId": "y"},
        "messages": [
            {"role": "user", "content": "看看我的屏幕"},
            {"role": "assistant", "content": [
                {"type": "tool_use", "id": "call_1", "name": "capture_screen",
                 "input": {"source": "current", "purpose": "visible_content"}}]},
            {"role": "user", "content": [
                {"type": "tool_result", "tool_use_id": "call_1", "content": [
                    {"type": "text", "text": "capture_call_id=1, 1280x720 像素。"},
                    {"type": "image", "source": {"type": "base64",
                                                 "media_type": "image/jpeg",
                                                 "data": b64}},
                ]}]},
        ],
    }


def count_images(o) -> int:
    if isinstance(o, dict):
        return (1 if o.get("type") == "image" else 0) + sum(
            count_images(v) for v in o.values())
    if isinstance(o, list):
        return sum(count_images(v) for v in o)
    return 0


def collect_text(o, acc: list[str]) -> None:
    if isinstance(o, dict):
        if o.get("type") == "text":
            acc.append(o.get("text") or "")
        for v in o.values():
            collect_text(v, acc)
    elif isinstance(o, list):
        for v in o:
            collect_text(v, acc)


def main() -> int:
    print("=" * 70)
    print("视觉策略：图片不能被静默丢弃")
    print("=" * 70)
    print(f"ocr.available() = {ocr.available()}   VISION_MODE = {settings.vision_mode}")

    if not os.path.exists(IMG):
        print(f"缺少测试图 {IMG}")
        return 2
    b64 = base64.b64encode(open(IMG, "rb").read()).decode()
    src = body_with_image(b64)

    # ---- 1. 私有字段剥离（与视觉无关，但同一条路径）----
    print("\n1. 顶层私有字段剥离")
    out = upstream.rewrite_request(copy.deepcopy(src))
    check("diandian_credits 已剥掉", "diandian_credits" not in out)
    check("main_reasoning_effort 已剥掉", "main_reasoning_effort" not in out)
    # 客户端选的模型要原样带到上游：走哪家由 pick_upstream 按前缀决定，
    # 这里再覆盖一次会让「设置里选了 A、实际用 B」重新出现。
    check("model 原样保留（路由交给 pick_upstream）",
          out["model"] == src["model"], out["model"])
    check("stream 强制为 True", out["stream"] is True)
    check("system 原样保留", out["system"] == "你是桌面助手")
    check("messages 数量不变", len(out["messages"]) == 3)

    # ---- 2. 图片必须变成文字 ----
    print("\n2. 图片 → 文本（auto / ocr 模式）")
    n_img = count_images(out)
    texts: list[str] = []
    collect_text(out, texts)
    joined = "\n".join(texts)
    if settings.vision_mode in ("auto", "ocr"):
        check("请求体里已无 image 块", n_img == 0, f"剩余 {n_img} 个")
        has_marker = "[当前屏幕上的文字如下" in joined or "无法读取其内容" in joined
        check("留了可读说明（OCR 文本或诚实占位）", has_marker)
        if "[当前屏幕上的文字如下" in joined:
            check("OCR 文本有实质内容", len(joined) > 200, f"{len(joined)} 字符")
            check("保留了原有的图注文本", "capture_call_id=1" in joined)
            # 抽查几个应该被识别到的词。
            # 注意：Windows OCR 会把中文按字切分并插空格（「点点」→「点 点」），
            # 所以比对前先去掉所有空白。
            compact = "".join(joined.split())
            for kw in ("点点", "spike", "Windows"):
                check(f"OCR 认出 {kw!r}", kw in compact)
        else:
            print("    （OCR 不可用，走的是诚实占位）")
    else:
        print(f"    VISION_MODE={settings.vision_mode}，跳过")

    # ---- 3. strip 模式：必须诚实说明，不能留空 ----
    print("\n3. strip 模式：明确告知看不到")
    old = settings.vision_mode
    try:
        settings.vision_mode = "strip"
        out2 = upstream.rewrite_request(copy.deepcopy(src))
        t2: list[str] = []
        collect_text(out2, t2)
        j2 = "\n".join(t2)
        check("无 image 块", count_images(out2) == 0)
        check("有「无法读取」提示", "无法读取其内容" in j2)
        check("提示里带分辨率", "1280x720" in j2, j2[:80])
        check("要求不要猜测", "不要猜测" in j2)
    finally:
        settings.vision_mode = old

    # ---- 4. pass 模式：原样透传 ----
    print("\n4. pass 模式：原样透传（给真视觉模型用）")
    try:
        settings.vision_mode = "pass"
        out3 = upstream.rewrite_request(copy.deepcopy(src))
        check("image 块仍在", count_images(out3) == 1)
        blk = out3["messages"][2]["content"][0]["content"][1]
        check("base64 未被改动", blk["source"]["data"] == b64)
    finally:
        settings.vision_mode = old

    # ---- 5. vision_capable：客户端说「这个模型自己能看图」→ 原样透传 ----
    # 模型清单在客户端（ModelCatalog），服务端不重复维护一份「谁能看图」的表。
    print("\n5. vision_capable：视觉组模型直连（不再转文本）")
    try:
        settings.vision_mode = "auto"
        direct = copy.deepcopy(src)
        direct["vision_capable"] = True
        out_v = upstream.rewrite_request(direct)
        check("image 块原样保留", count_images(out_v) == 1, f"{count_images(out_v)} 个")
        check("vision_capable 已剥掉", "vision_capable" not in out_v)
        blk_v = out_v["messages"][2]["content"][0]["content"][1]
        check("base64 未被改动", blk_v["source"]["data"] == b64)

        # 显式强制档优先：人工排障时把 VISION_MODE 设成 ocr，就该照 ocr 走
        settings.vision_mode = "ocr"
        out_force = upstream.rewrite_request(copy.deepcopy(direct))
        check("VISION_MODE=ocr 时强制转换，不听 vision_capable",
              count_images(out_force) == 0, f"{count_images(out_force)} 个")
    finally:
        settings.vision_mode = old

    # ---- 6. 无图请求不受影响 ----
    print("\n6. 纯文本请求不受影响")
    plain = {"model": "m", "max_tokens": 10, "stream": True,
             "messages": [{"role": "user", "content": "你好"}]}
    out4 = upstream.rewrite_request(copy.deepcopy(plain))
    check("messages 结构未变", out4["messages"] == plain["messages"])

    # ---- 6. 回归：在事件循环里调用也不能静默失效 ----
    # 真实链路里 rewrite_request 是在 uvicorn 的事件循环上下文中跑的。
    # 早期实现里 OCR 用 asyncio.run()，在已有循环中会抛
    # "asyncio.run() cannot be called from a running event loop"，
    # 异常被吞掉 → 退成「看不到」占位 → 模型反问用户屏幕上是什么。
    # 这个 bug 只在服务端暴露，离线同步调用测不出来，必须专门锁住。
    print("\n7. 回归：事件循环内调用（服务端真实路径）")
    if settings.vision_mode in ("auto", "ocr") and ocr.available():

        async def in_loop():
            return upstream.rewrite_request(copy.deepcopy(src))

        out5 = asyncio.run(in_loop())
        t5: list[str] = []
        collect_text(out5, t5)
        j5 = "\n".join(t5)
        check("循环内无 image 块", count_images(out5) == 0)
        check("循环内拿到了 OCR 文本（不是占位）", "[当前屏幕上的文字如下" in j5,
              j5[:60].replace("\n", " "))
        check("循环内 OCR 有实质内容", len(j5) > 200, f"{len(j5)} 字符")

        async def via_thread():
            return await asyncio.to_thread(
                upstream.rewrite_request, copy.deepcopy(src))

        out6 = asyncio.run(via_thread())
        t6: list[str] = []
        collect_text(out6, t6)
        check("to_thread 路径同样拿到 OCR", "[当前屏幕上的文字如下" in "\n".join(t6))
    else:
        print("    （OCR 不可用，跳过）")

    print("\n" + "=" * 70)
    print("全部通过" if fails == 0 else f"{fails} 项失败")
    print("=" * 70)
    return 0 if fails == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
