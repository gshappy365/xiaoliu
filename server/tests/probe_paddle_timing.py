"""分阶段计时探针：找出 PaddleOCR-VL 到底卡在哪一步。

之前出现过「配置好了但 predict 跑 5 分钟不返回」的现象，
日志里没有任何线索。这个脚本把每一步的时间都打出来，并且用
flush=True 保证能实时看到，避免又被管道缓冲掉。
"""
import base64
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from app import paddle_ocr

IMG = os.path.join(os.path.dirname(os.path.dirname(HERE)),
                   "evidence", "selfcheck-capture-1931.jpg")


def step(name, t0):
    print(f"  {name}: {time.time() - t0:.2f}s", flush=True)


print("=" * 60, flush=True)
print("PaddleOCR-VL 分阶段计时", flush=True)
print("=" * 60, flush=True)

t = time.time()
print(f"[1] available() ...", flush=True)
ok = paddle_ocr.available()
print(f"    → {ok}  {paddle_ocr.status()['reason']}", flush=True)
step("available", t)

t = time.time()
print("[2] 读图 + 解码", flush=True)
with open(IMG, "rb") as f:
    raw = f.read()
print(f"    文件 {len(raw)/1024:.0f} KB", flush=True)
img = paddle_ocr._to_image(base64.b64encode(raw).decode())
print(f"    PIL {img.size} {img.mode}", flush=True)
step("解码", t)

t = time.time()
print("[3] 加载模型 _get()", flush=True)
pipe = paddle_ocr._get()
step("加载", t)

t = time.time()
print("[4] paddle.disable_static()", flush=True)
import paddle

paddle.disable_static()
step("disable_static", t)

t = time.time()
print("[5] np.asarray(img)", flush=True)
import numpy as np

arr = np.asarray(img)
print(f"    shape={arr.shape} dtype={arr.dtype}", flush=True)
step("numpy", t)

t = time.time()
print("[6] pipe.predict(...)  ← 之前卡在这里", flush=True)
out = pipe.predict(arr)
print(f"    返回类型 {type(out).__name__}", flush=True)
step("predict 调用", t)

t = time.time()
print("[7] 迭代结果", flush=True)
parts = []
for i, r in enumerate(out):
    print(f"    第 {i} 条：{type(r).__name__}", flush=True)
    md = getattr(r, "markdown", None)
    if isinstance(md, dict):
        parts.append(md.get("markdown_texts") or "")
step("迭代", t)

text = "\n".join(p for p in parts if p).strip()
print(f"\n共 {len(text)} 字", flush=True)
print(text[:300], flush=True)
