"""麦克风自检：找出这台机器上**真正能录到人声**的录音设备。

为什么需要它：
    点点按住 Ctrl+Alt 会正常开始录音，但录到的内容取决于系统给它的录音设备。
    如果那个设备是虚拟声卡（VB-Cable / AudioRelay / 远程串流），录到的就是
    数字静音或 -60dB 底噪。SenseVoice 在这类输入上会幻觉出一个固定短词
    「我。」，客户端拿到这句无意义的文本后直接取消这一轮——用户看到的现象
    就是「按了热键没反应」。光看日志里「识别出 2 个字」完全看不出根因。

做法：
    用 ffmpeg 的 dshow 枚举录音设备，逐个录 2 秒，算 rms / peak / 有声占比，
    再判定「能录到声音 / 只有底噪 / 全零」。

用法：
    python tools/mic_check.py            # 录 2 秒
    python tools/mic_check.py --sec 4    # 录 4 秒（要说话）
    python tools/mic_check.py --no-record # 只列设备，不录音

录音期间请对着**默认麦克风**正常说一句话，结果才有意义。
"""
from __future__ import annotations

import argparse
import glob
import os
import shutil
import subprocess
import sys
import tempfile
import wave

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from app import asr                                        # noqa: E402

# 虚拟声卡的设备名特征。命中这些的都不是真实麦克风。
VIRTUAL_HINTS = (
    "virtual", "cable", "vb-audio", "voicemeeter", "audioRelay".lower(),
    "远程", "stream", "stereo mix", "立体声混音", "loopback", "dummy",
)


def find_ffmpeg() -> str | None:
    """找一个**能用**的 ffmpeg。

    坑：WinGet 的 Links 目录下是 0 字节转发文件，shutil.which 能找到但
    执行时报 WinError 193。所以每个候选都要校验文件大小 > 0。
    """
    cands: list[str] = []
    w = shutil.which("ffmpeg")
    if w:
        cands.append(w)
    local = os.environ.get("LOCALAPPDATA", "")
    if local:
        cands += glob.glob(os.path.join(
            local, "Microsoft", "WinGet", "Packages",
            "*FFmpeg*", "**", "ffmpeg.exe"), recursive=True)
        cands += glob.glob(os.path.join(
            local, "Microsoft", "WinGet", "Links", "ffmpeg.exe"))
        cands += glob.glob(os.path.join(
            local, "Packages", "*", "**", "windows-amd64", "bin", "ffmpeg.exe"),
            recursive=True)
    for c in cands:
        try:
            if os.path.isfile(c) and os.path.getsize(c) > 1_000_000:
                return c
        except OSError:
            continue
    return None


def list_devices(ffmpeg: str) -> list[str]:
    """解析 ffmpeg -list_devices 输出里的音频设备名。"""
    try:
        out = subprocess.run(
            [ffmpeg, "-hide_banner", "-list_devices", "true",
             "-f", "dshow", "-i", "dummy"],
            capture_output=True, text=True, encoding="utf-8",
            errors="replace", timeout=30)
    except Exception as exc:                               # noqa: BLE001
        print(f"枚举设备失败：{exc}")
        return []
    text = (out.stderr or "") + (out.stdout or "")
    devs = []
    for line in text.splitlines():
        if "(audio)" not in line:
            continue
        # 形如： [in#0 @ 0x...] "麦克风阵列 (Realtek(R) Audio)" (audio)
        first = line.find('"')
        last = line.rfind('"')
        if 0 <= first < last:
            devs.append(line[first + 1:last])
    return devs


def record(ffmpeg: str, device: str, seconds: float, out: str) -> bool:
    try:
        subprocess.run(
            [ffmpeg, "-hide_banner", "-loglevel", "error",
             "-f", "dshow", "-i", f"audio={device}",
             "-t", str(seconds), "-ac", "1", "-ar", "16000", "-y", out],
            capture_output=True, timeout=seconds + 20)
        return os.path.exists(out) and os.path.getsize(out) > 1000
    except Exception:                                      # noqa: BLE001
        return False


def is_virtual(name: str) -> bool:
    low = name.lower()
    return any(h in low for h in VIRTUAL_HINTS)


def _w(s: str) -> int:
    """字符串的终端显示宽度。中文占 2 列，用 len() 对齐会错位。"""
    import unicodedata

    return sum(2 if unicodedata.east_asian_width(c) in "WF" else 1 for c in s)


def pad(s: str, width: int) -> str:
    s = s[:width]
    return s + " " * max(0, width - _w(s))


def watch(ffmpeg: str, device: str, seconds: float) -> int:
    """实时显示某个设备的输入电平。

    用来回答一个很具体的问题：「这个设备现在到底有没有声音进来？」
    比录音再分析更直观——对着麦克风说话，看电平条跳不跳。
    """
    import time

    print(f"实时监听：{device}")
    print(f"时长 {seconds:.0f} 秒，对着麦克风说话，看电平条是否跳动。Ctrl+C 结束。\n")

    block = 8000                      # 0.5 秒 @16kHz 单声道
    proc = subprocess.Popen(
        [ffmpeg, "-hide_banner", "-loglevel", "error",
         "-f", "dshow", "-i", f"audio={device}",
         "-ac", "1", "-ar", "16000", "-f", "s16le", "-"],
        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    t0 = time.time()
    peak_seen = 0.0
    try:
        while time.time() - t0 < seconds:
            raw = proc.stdout.read(block * 2)
            if not raw or len(raw) < block:
                break
            st = asr.analyze(raw)
            peak_seen = max(peak_seen, st["peak"])
            # 电平条按 dBFS 映射到 40 格
            import math

            db = 20 * math.log10(max(st["rms"], 1e-6))
            n = max(0, min(40, int((db + 70) / 70 * 40)))
            bar = "█" * n + "·" * (40 - n)
            tag = "静音" if asr.is_silent(st) else "有声音"
            print(f"\r[{bar}] rms={st['rms']:.4f} peak={st['peak']:.3f} {tag}   ",
                  end="", flush=True)
    except KeyboardInterrupt:
        pass
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except Exception:                                  # noqa: BLE001
            proc.kill()
    print(f"\n\n本次最大峰值 {peak_seen:.4f}")
    if peak_seen < 1e-4:
        print("→ 全程全零：这个设备没有任何输入源（虚拟设备没在转发）。")
    elif peak_seen < asr.SILENCE_PEAK:
        print("→ 只有极低底噪：设备在输出数据，但没有人声。")
    else:
        print("→ 录到了声音，这个设备可以用。")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--sec", type=float, default=2.0, help="每个设备录几秒")
    ap.add_argument("--no-record", action="store_true", help="只列设备不录音")
    ap.add_argument("--watch", metavar="设备名", help="实时监听指定设备的电平")
    args = ap.parse_args()

    ffmpeg = find_ffmpeg()
    if not ffmpeg:
        print("找不到可用的 ffmpeg（WinGet 的 Links 下可能是 0 字节转发文件）。")
        print("装一个：winget install Gyan.FFmpeg")
        return 2

    devs = list_devices(ffmpeg)
    if args.watch:
        # 允许用子串匹配，省得敲全名
        hit = next((d for d in devs if args.watch.lower() in d.lower()), None)
        if not hit:
            print(f"没找到名字含 {args.watch!r} 的设备。可用设备：")
            for d in devs:
                print(f"  - {d}")
            return 2
        return watch(ffmpeg, hit, args.sec if args.sec != 2.0 else 10.0)

    print("=" * 78)
    print(f"麦克风自检　ffmpeg={os.path.basename(ffmpeg)}")
    print("=" * 78)
    if not devs:
        print("没有枚举到任何录音设备。")
        return 2

    print(f"共 {len(devs)} 个录音设备\n")
    if args.no_record:
        for d in devs:
            print(f"  {'[虚拟]' if is_virtual(d) else '[物理]'} {d}")
        return 0

    print(f"开始逐个录制 {args.sec:.0f} 秒——现在请正常说一句话。\n")
    print(f"{pad('设备', 42)}{pad('类型', 7)}{'rms':>9}{'peak':>8}{'有声':>7}  判定")
    print("-" * 78)

    usable: list[str] = []
    tmpdir = tempfile.mkdtemp(prefix="miccheck-")
    for d in devs:
        path = os.path.join(tmpdir, f"{len(usable)}{abs(hash(d)) % 99999}.wav")
        ok = record(ffmpeg, d, args.sec, path)
        kind = pad("虚拟" if is_virtual(d) else "物理", 7)
        if not ok:
            print(f"{pad(d, 42)}{kind}{'-':>9}{'-':>8}{'-':>7}  录不到（被占用或无输入）")
            continue
        try:
            with wave.open(path, "rb") as w:
                pcm = w.readframes(w.getnframes())
                sr = w.getframerate()
        except Exception:                                  # noqa: BLE001
            continue
        st = asr.analyze(pcm)
        silent = asr.is_silent(st)
        # ffmpeg 从虚拟设备录出来的不是精确全零，可能有 1 个 LSB 的抖动，
        # 所以用 <1e-4 而不是 ==0 来判断「没有输入源」。
        if st["peak"] < 1e-4:
            verdict = "全零（无输入源）"
        elif silent:
            verdict = "只有底噪"
        else:
            verdict = "★ 能录到声音"
            usable.append(d)
        print(f"{pad(d, 42)}{kind}{st['rms']:>9.5f}{st['peak']:>8.4f}"
              f"{st['voiced'] * 100:>6.1f}%  {verdict}")

    print("-" * 78)
    if usable:
        print("\n能录到声音的设备：")
        for d in usable:
            print(f"  - {d}")
        print("\n把其中一个是物理麦克风的设为 Windows 默认录音设备，"
              "再按住 Ctrl+Alt 说话即可。")
    else:
        print("\n[!] 没有任何设备录到有效声音。")
        print("    这台机器目前只有虚拟声卡在送数据，点点拿到的必然是静音，")
        print("    服务端会把它判定为「没有听到声音」而不是幻觉出『我。』。")
        print("    解决：插一个麦克风/耳麦，或在远程串流软件里打开麦克风转发。")

    print(f"\n录到的音频留在：{tmpdir}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
