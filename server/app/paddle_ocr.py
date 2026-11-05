"""本地 OCR：PaddleOCR-VL-1.6（0.9B，GPU 推理）—— 子进程客户端。

定位：**它替换的是 Windows OCR，不是 SenseNova VL。**

    Windows OCR 只做字形识别，形近字错得离谱（「预研」→「予贞石开」）。
    SenseNova VL 能理解界面语义，但一次要 21-48 秒。
    PaddleOCR-VL 在中间：6-14 秒拿到**准确得多的文字**，语义交给下游
    spark-x2.6 自己推理（「在听...」这种判断不需要视觉模型，看到文字就会）。

实测（RTX 3060 12GB，关掉文档专用预处理）：

    evidence/selfcheck-capture-1931.jpg  1280x720   7.4s   353 字

    识别质量：整体远好于 Windows OCR，仍有个别形近字错（「点点」→「四点」、
    「项」→「顶」、「多屏截图」→「多屏蔽图」）。这些错字**稳定复现**，
    不是随机噪声，说明是模型对该 UI 字体的弱点，调参数解决不了。

## 为什么推理在独立进程里（app/ocr_worker.py）

同一个模型、同一张图、同一份代码：

    独立脚本里跑      7.4 秒，353 字，稳定
    服务进程里跑      30 秒不返回（GPU 利用率 5%、SM 时钟 210 MHz、功耗 19.9 W）

服务进程里有 uvicorn 事件循环、sherpa-onnx（ASR）、comtypes/SAPI（TTS）
这些会初始化 CUDA / COM / OpenMP 的东西。和 paddle 放进同一个进程后，
`pipe.predict` 会在某个同步点上卡住 —— GPU 明明空闲，kernel 就是不结束。

与其去猜是哪一家的全局状态踩到了 paddle，不如隔到独立进程里：
跑的是被验证过稳定的那条路径，而且**卡死了可以 kill 掉重启**。
同一个进程内的卡死线程是杀不掉的（它攥着 GPU 上下文）。

## 踩过的坑：中文用户名

    paddlex 默认把模型下到 `C:\\Users\\<用户名>\\.paddlex`。这台机器用户名是
    「李平」，于是：
        SentencePiece  → RuntimeError: NOT_FOUND "...\\tokenizer.model"
        Paddle Inference → RuntimeError: Cannot open file "...\\inference.json"
    而 Python 的 os.path.exists() 对同一路径返回 True —— C++ 层打不开非 ASCII
    路径。更麻烦的是 paddlex 把前者包装成 `return False`，上层直接解包
    tuple 时报 `TypeError: cannot unpack non-iterable bool object`，
    **真实的路径错误被完全掩盖**，排查了很久。

    所以模型目录必须放在纯 ASCII 路径下（本仓库的 models/paddlex/ 满足），
    并用 vl_rec_model_dir 显式指定。注意环境变量是 PADDLE_PDX_CACHE_HOME
    （不是 PADDLEX_HOME），但实测改它反而会卡在远端校验，不如直接指定目录。
"""
from __future__ import annotations

import json
import logging
import os
import queue
import subprocess
import sys
import threading
import time

log = logging.getLogger("xiaoliu.paddleocr")

HERE = os.path.dirname(os.path.abspath(__file__))
WORKER = os.path.join(HERE, "ocr_worker.py")
DEFAULT_MODEL_DIR = os.path.join(
    os.path.dirname(os.path.dirname(HERE)),
    "models", "paddlex", "official_models", "PaddleOCR-VL-1.6")

_lock = threading.Lock()          # 请求串行，worker 一次只处理一个
_state_lock = threading.Lock()    # 只管 _state/_reason，别和请求锁混用
_proc: subprocess.Popen | None = None
_queue: queue.Queue = queue.Queue()
_state: str | None = None         # None=未尝试, "ok", "unavailable"
_reason = ""
_load_ms = 0.0
# 卡死一次就先歇一会儿。不能永久停用：实测同一进程里第一次卡、之后就好了，
# 永久降级会让服务质量一直停在 Windows OCR。
_poisoned = False
_poisoned_at = 0.0
_poison_reason = ""
COOLDOWN_S = 300.0


def model_dir() -> str:
    return os.environ.get("PADDLE_OCR_MODEL_DIR", "").strip() or DEFAULT_MODEL_DIR


def device() -> str:
    # 没有 GPU 也能跑，只是慢。设 PADDLE_OCR_DEVICE=cpu 可强制。
    return os.environ.get("PADDLE_OCR_DEVICE", "").strip() or "gpu"


def predict_timeout() -> float:
    """单次推理超时。正常 6-14 秒，留 30 秒余量给首次 kernel 编译。"""
    try:
        return float(os.environ.get("PADDLE_OCR_TIMEOUT", "") or 30.0)
    except ValueError:
        return 30.0


def startup_timeout() -> float:
    """worker 启动超时（含模型加载）。实测加载 3 秒，给 60 秒足够。"""
    try:
        return float(os.environ.get("PADDLE_OCR_STARTUP_TIMEOUT", "") or 60.0)
    except ValueError:
        return 60.0


def max_new_tokens() -> int:
    """生成上限。这张 1280x720 截图输出 353 字（约 700 token），2048 足够。"""
    try:
        return int(os.environ.get("PADDLE_OCR_MAX_TOKENS", "") or 2048)
    except ValueError:
        return 2048


def _ascii_only(path: str) -> bool:
    try:
        path.encode("ascii")
        return True
    except UnicodeEncodeError:
        return False


def _is_poisoned() -> bool:
    global _poisoned
    if not _poisoned:
        return False
    if time.time() - _poisoned_at >= COOLDOWN_S:
        _poisoned = False
        log.info("paddleocr: 冷却结束，重新尝试")
        return False
    return True


def _mark_poisoned(reason: str) -> None:
    global _poisoned, _poisoned_at, _poison_reason
    _poisoned = True
    _poisoned_at = time.time()
    _poison_reason = reason


def available() -> bool:
    """能否用 PaddleOCR-VL。不可用时上层退回 Windows OCR。"""
    global _state, _reason
    if _is_poisoned():
        return False
    if _state == "ok":
        return True
    if _state == "unavailable":
        return False
    with _state_lock:
        if _state is not None:
            return _state == "ok"
        # PADDLE_OCR_DEVICE=off = 手动禁用（2026-11-04：RTX 3060 12GB 里它占约
        # 10GB，而这条链是冷路径；挪 CPU 实测模型加载 44s、推理 178s，不可用）。
        # 禁用后 warmup 不拉 worker、调度直接跳过，OCR 兜底全交 Windows OCR。
        if os.environ.get("PADDLE_OCR_DEVICE", "").strip().lower() in (
                "off", "none", "disabled"):
            _state, _reason = "unavailable", "PADDLE_OCR_DEVICE=off（已禁用，为显卡腾显存）"
            log.info("paddleocr 不可用：%s", _reason)
            return False
        if not os.path.isfile(WORKER):
            _state, _reason = "unavailable", f"worker 脚本不存在：{WORKER}"
        elif not os.path.isdir(model_dir()):
            _state, _reason = "unavailable", f"模型目录不存在：{model_dir()}"
        elif not _ascii_only(os.path.abspath(model_dir())):
            # 见模块注释：C++ 层打不开非 ASCII 路径，会以极具误导性的方式失败
            _state = "unavailable"
            _reason = (f"模型路径含非 ASCII 字符（SentencePiece/Paddle 打不开）："
                       f"{model_dir()}")
            log.warning("paddleocr 不可用：%s", _reason)
            return False
        else:
            _state = "ok"
        if _state == "unavailable":
            log.info("paddleocr 不可用：%s", _reason)
        return _state == "ok"


# ------------------------------------------------------------------ worker

def _reader(proc: subprocess.Popen) -> None:
    """把 worker 的 stdout 逐行丢进队列。进程退出时放一个 None 收尾。"""
    assert proc.stdout is not None
    try:
        for line in proc.stdout:
            _queue.put(line)
    except Exception:                                    # noqa: BLE001
        pass
    _queue.put(None)


def _drain() -> None:
    """清掉队列里可能残留的上一轮响应，避免错位。"""
    while True:
        try:
            _queue.get_nowait()
        except queue.Empty:
            return


def _kill_worker() -> None:
    global _proc
    proc, _proc = _proc, None
    if proc is None:
        return
    try:
        proc.kill()
    except Exception:                                    # noqa: BLE001
        pass
    try:
        proc.wait(timeout=5)
    except Exception:                                    # noqa: BLE001
        pass


def _spawn() -> bool:
    """拉起 worker 并等它 ready。失败返回 False。"""
    global _proc, _load_ms
    env = os.environ.copy()
    env["PADDLE_OCR_MODEL_DIR"] = os.path.abspath(model_dir())
    env["PADDLE_OCR_DEVICE"] = device()
    env["PADDLE_OCR_MAX_TOKENS"] = str(max_new_tokens())
    env["PYTHONIOENCODING"] = "utf-8"
    try:
        proc = subprocess.Popen(
            [sys.executable, "-u", WORKER],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,       # worker 日志不混进主进程日志
            text=True, encoding="utf-8", bufsize=1, env=env,
        )
    except Exception as exc:                             # noqa: BLE001
        log.warning("paddleocr: 启动 worker 失败：%s", exc)
        return False
    _drain()
    threading.Thread(target=_reader, args=(proc,), name="paddleocr-reader",
                     daemon=True).start()
    _proc = proc
    t0 = time.time()
    try:
        line = _queue.get(timeout=startup_timeout())
    except queue.Empty:
        line = None
    if not line:
        log.warning("paddleocr: worker %.0fs 内未就绪，放弃", time.time() - t0)
        _kill_worker()
        return False
    try:
        msg = json.loads(line)
        _load_ms = float(msg.get("load_ms") or 0)
    except json.JSONDecodeError:
        pass
    log.info("paddleocr: worker 就绪（模型加载 %.0fms，共 %.0fs）",
             _load_ms, time.time() - t0)
    return True


def _ensure() -> bool:
    global _proc
    if _proc is not None and _proc.poll() is None:
        return True
    _kill_worker()
    return _spawn()


def _request(payload: dict, timeout: float) -> dict | None:
    """发一条请求并等一行响应。超时/进程死掉返回 None。"""
    if not _ensure():
        return None
    assert _proc is not None and _proc.stdin is not None
    try:
        _proc.stdin.write(json.dumps(payload, ensure_ascii=False) + "\n")
        _proc.stdin.flush()
    except Exception as exc:                             # noqa: BLE001
        log.warning("paddleocr: 写 worker 失败：%s", exc)
        _kill_worker()
        return None
    try:
        line = _queue.get(timeout=timeout)
    except queue.Empty:
        return None
    if line is None:                                     # worker 退出了
        _kill_worker()
        return None
    try:
        return json.loads(line)
    except json.JSONDecodeError:
        return None


def warmup() -> None:
    """后台把 worker 拉起来（含模型加载），省掉第一次截图等冷启动。

    注意这里**不跑推理**：冷启动卡死的风险已经由「超时 + 冷却」兜住，
    而预热推理本身会占住 worker 好几分钟（卡的时候），
    不如让第一次真实请求去触发，它有自己的超时。
    """
    if not available():
        return

    def work() -> None:
        with _lock:
            try:
                _ensure()
            except Exception as exc:                     # noqa: BLE001
                log.warning("paddleocr 预热失败：%s", exc)

    threading.Thread(target=work, name="paddleocr-warmup", daemon=True).start()


def image_to_text(b64: str, media_type: str = "image/jpeg") -> str:
    """base64 图 → 文字。任何异常/超时返回空串，由上层决定怎么退化。"""
    if not available():
        return ""
    with _lock:
        t0 = time.time()
        resp = _request({"b64": b64, "media_type": media_type}, predict_timeout())
        dt = time.time() - t0
        if resp is None:
            # 卡死或 worker 崩了。杀掉重启，并冷却一段时间再放行。
            _kill_worker()
            _mark_poisoned(f"推理超过 {predict_timeout():.0f}s 未返回")
            log.error("paddleocr: 推理 %.0fs 未返回（GPU 空闲但 kernel 不结束），"
                      "降级到 Windows OCR，%d 秒后自动重试。",
                      dt, int(COOLDOWN_S))
            return ""
        if "error" in resp:
            log.warning("paddleocr 识别失败：%s", resp["error"])
            return ""
        text = str(resp.get("text") or "")
        log.info("paddleocr: %d 字，耗时 %.0fms", len(text), dt * 1000)
        return text


def status() -> dict[str, object]:
    return {
        "available": available(),
        "reason": _reason,
        "model_dir": model_dir(),
        "device": device(),
        "worker": "running" if (_proc is not None and _proc.poll() is None) else "stopped",
        "load_ms": round(_load_ms),
        "timeout": predict_timeout(),
        "max_new_tokens": max_new_tokens(),
        "poisoned": _poisoned,
        "poison_reason": _poison_reason,
    }


def shutdown() -> None:
    """服务退出时收尾。"""
    global _proc
    proc = _proc
    if proc is None:
        return
    try:
        if proc.stdin:
            proc.stdin.write(json.dumps({"op": "shutdown"}) + "\n")
            proc.stdin.flush()
        proc.wait(timeout=5)
    except Exception:                                    # noqa: BLE001
        _kill_worker()


__all__ = ["available", "image_to_text", "warmup", "status", "model_dir", "shutdown"]
