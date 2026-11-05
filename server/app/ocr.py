"""本地 OCR：把截图转成文字，喂给不支持视觉的文本模型。

为什么需要这层：`spark-x2.6` 会**静默丢弃**图片（HTTP 200，模型反问「你屏幕上是什么」），
不会报错。这种失败比报错更危险——模型会基于「看不见」自信编造屏幕内容。
所以要么老实告诉模型「你看不到」，要么把图变成它能读的文字。

Windows 自带 OCR 引擎对中文简体效果可用（`zh-Hans-CN`），且不需要联网、
不需要额外模型文件，正好适合本地自建服务器。

坐标：OCR 结果按行输出，每行带 `[y]` 行首位置，让模型能大致定位「第几行」。
点点自己的视觉工具用 0-1000 归一化坐标，但文本模型本来也用不上精确坐标。
"""
from __future__ import annotations

import asyncio
import base64
import io
import logging
import threading

from . import paddle_ocr

log = logging.getLogger("xiaoliu.ocr")

_engine = None
_engine_lock = threading.Lock()
_available: bool | None = None


def available() -> bool:
    """是否有**任意** OCR 后端可用（PaddleOCR-VL 或 Windows OCR）。"""
    return paddle_ocr.available() or _windows_available()


def _windows_available() -> bool:
    """Windows 自带 OCR 是否可用。不可用时上层退回「诚实占位」。"""
    global _available
    if _available is not None:
        return _available
    try:
        from winrt.windows.media.ocr import OcrEngine  # noqa: F401
        _available = True
    except Exception as exc:                             # noqa: BLE001
        log.info("Windows OCR 不可用：%s", exc)
        _available = False
    return _available


def _get_engine():
    global _engine
    with _engine_lock:
        if _engine is not None:
            return _engine
        from winrt.windows.globalization import Language
        from winrt.windows.media.ocr import OcrEngine

        for tag in ("zh-Hans-CN", "zh-CN", "en-US"):
            try:
                eng = OcrEngine.try_create_from_language(Language(tag))
                if eng is not None:
                    _engine = eng
                    log.info("ocr 引擎：%s", tag)
                    return _engine
            except Exception:                            # noqa: BLE001
                continue
        _engine = OcrEngine.try_create_from_user_profile_languages()
        if _engine is None:
            raise RuntimeError("没有可用的 OCR 语言")
        return _engine


# 放大倍数：Windows OCR 对小字号很敏感，1280x720 的深色 UI 直接识别
# 只有 21 行 557 字符，2x LANCZOS 后变成 31 行 764 字符，可读性明显变好。
# 代价约 30ms。
_UPSCALE = 2
_MAX_PIXELS = 12_000_000          # 放大后上限，防止 4K 屏把内存打爆


def _preprocess(raw: bytes) -> tuple[bytes, float]:
    """返回 (PNG 字节, 缩放比)。PIL 缺失时原样返回。"""
    try:
        import io

        from PIL import Image
    except Exception:                                    # noqa: BLE001
        return raw, 1.0
    try:
        img = Image.open(io.BytesIO(raw)).convert("RGB")
    except Exception:                                    # noqa: BLE001
        return raw, 1.0
    scale = 1.0
    w, h = img.size
    if w * h * (_UPSCALE ** 2) <= _MAX_PIXELS:
        scale = float(_UPSCALE)
        img = img.resize((w * _UPSCALE, h * _UPSCALE), Image.LANCZOS)
    buf = io.BytesIO()
    img.save(buf, "PNG")
    return buf.getvalue(), scale


async def _recognize(raw: bytes) -> tuple[str, int]:
    from winrt.windows.graphics.imaging import BitmapDecoder
    from winrt.windows.storage.streams import DataWriter, InMemoryRandomAccessStream

    prepared, scale = _preprocess(raw)

    stream = InMemoryRandomAccessStream()
    writer = DataWriter(stream.get_output_stream_at(0))
    writer.write_bytes(prepared)
    await writer.store_async()
    await writer.flush_async()
    stream.seek(0)

    decoder = await BitmapDecoder.create_async(stream)
    bitmap = await decoder.get_software_bitmap_async()

    eng = _get_engine()
    result = await eng.recognize_async(bitmap)

    rows: list[tuple[int, str]] = []
    for line in result.lines:
        txt = (line.text or "").strip()
        if not txt:
            continue
        y = 0
        try:
            if line.words:
                y = int(line.words[0].bounding_rect.y)
        except Exception:                                # noqa: BLE001
            pass
        rows.append((int(y / scale) if scale else y, txt))   # 换回原图坐标
    rows.sort(key=lambda r: r[0])
    text = "\n".join(f"[y={y}] {t}" for y, t in rows)
    return text, len(rows)


def image_to_text(b64: str, media_type: str = "image/jpeg") -> str:
    """base64 图 → 文字。按 OCR_BACKEND 调度 PaddleOCR-VL / Windows OCR。

    两层后端的分工：
        PaddleOCR-VL  6-14 秒，GPU 推理，文字准确得多（推荐）
        Windows OCR   0.5 秒，形近字错得离谱（「预研」→「予贞石开」）

    回退链保证：**任何情况下都不会把图片静默丢给只吃文本的上游**。
    """
    from .config import settings
    from . import paddle_ocr

    backend = settings.ocr_backend
    if backend in ("auto", "paddle") and paddle_ocr.available():
        text = paddle_ocr.image_to_text(b64, media_type)
        if text:
            return text
        if backend == "paddle":
            log.warning("ocr: paddle 后端未返回内容（backend=paddle，不回退）")
            return ""
        log.info("ocr: paddle 未返回内容，回退 Windows OCR")

    return _windows_image_to_text(b64, media_type)


def _windows_image_to_text(b64: str, media_type: str = "image/jpeg") -> str:
    """Windows 自带 OCR。快但形近字差，作为兜底。"""
    if not available():
        return ""
    try:
        raw = base64.b64decode(b64, validate=False)
    except Exception:                                    # noqa: BLE001
        return ""
    try:
        try:
            asyncio.get_running_loop()
            in_loop = True
        except RuntimeError:
            in_loop = False

        if in_loop:
            # 已经在事件循环里：asyncio.run() 会抛 RuntimeError。
            # 开一个独立线程跑自己的 loop，别让 OCR 静默失效
            # （静默失效的后果是模型收到「看不到」占位，然后反问用户屏幕上是什么）。
            box: dict[str, object] = {}

            def work() -> None:
                try:
                    box["ok"] = asyncio.run(_recognize(raw))
                except Exception as exc:                 # noqa: BLE001
                    box["err"] = exc

            th = threading.Thread(target=work, name="ocr", daemon=True)
            th.start()
            th.join()
            if "err" in box:
                raise box["err"]                         # type: ignore[misc]
            text, n = box["ok"]                          # type: ignore[misc]
        else:
            text, n = asyncio.run(_recognize(raw))
    except Exception as exc:                             # noqa: BLE001
        log.warning("ocr 失败（%s）：%s", media_type, exc)
        return ""
    if not text:
        log.info("ocr: 图里没识别到文字")
    else:
        log.info("ocr: %d 行 / %d 字符", n, len(text))
    return text


def png_or_jpeg_dimensions(raw: bytes) -> tuple[int, int]:
    """不引第三方库拿尺寸，只用于日志。"""
    try:
        if raw[:2] == b"\xff\xd8":
            i = 2
            while i < len(raw) - 9:
                if raw[i] != 0xFF:
                    i += 1
                    continue
                marker = raw[i + 1]
                if marker in (0xC0, 0xC1, 0xC2, 0xC3):
                    h = int.from_bytes(raw[i + 5:i + 7], "big")
                    w = int.from_bytes(raw[i + 7:i + 9], "big")
                    return w, h
                seglen = int.from_bytes(raw[i + 2:i + 4], "big")
                i += 2 + seglen
        elif raw[:8] == b"\x89PNG\r\n\x1a\n":
            w = int.from_bytes(raw[16:20], "big")
            h = int.from_bytes(raw[20:24], "big")
            return w, h
    except Exception:                                    # noqa: BLE001
        pass
    return 0, 0


__all__ = ["available", "image_to_text", "png_or_jpeg_dimensions"]
_ = io
