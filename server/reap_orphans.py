"""清掉上一轮遗留的 OCR worker 进程。

为什么需要这个：2026-10-30 12:34 那次，服务随后台任务一起退出后，
留下 2 个 `ocr_worker.py` 和 2 个 8899 端口的 uvicorn 孤儿进程，
显存从 1.36GB 被吃到 10.5GB。新服务起来时 PaddleOCR-VL 申请不到显存，
worker 启动超时（60s）后降级到 Windows OCR，识图质量掉一档，而且很难看出来。

为什么 worker 不自己退：`ocr_worker.main()` 里是 `for line in sys.stdin`，
父进程一死 stdin 就该 EOF。但如果 worker 正卡在 `predict()` 里（这是已知
会发生的：日志里出现过 30s 不返回、GPU 利用率 5% 的僵死状态），
它根本回不到读循环，也就永远看不到 EOF，于是进程和显存一起留着。

判定规则：**443 没有监听 → 任何 ocr_worker.py 都是孤儿**。
因为 worker 的唯一用途就是伺候绑在 443 上的那个服务。

用法：
    python reap_orphans.py            # 真的杀
    python reap_orphans.py --dry-run  # 只打印
"""
from __future__ import annotations

import sys

try:
    import psutil
except ImportError:                      # pragma: no cover
    print("reap: 没装 psutil，跳过")
    raise SystemExit(0)

# 端口可覆盖只为测试用：指向一个没人监听的端口，就能在不真停服务的前提下
# 验证「孤儿判定 + dry-run 输出」这条路径。
PORT = int(__import__("os").environ.get("REAP_PORT", "443") or 443)


def _listeners() -> set[int]:
    """绑在 PORT 上的进程 pid。拿不到就返回空集（保守：不杀任何东西）。"""
    pids: set[int] = set()
    try:
        for c in psutil.net_connections(kind="inet"):
            if c.status == psutil.CONN_LISTEN and c.laddr and c.laddr.port == PORT:
                if c.pid:
                    pids.add(c.pid)
    except Exception:                    # noqa: BLE001  无权限时整个调用会抛
        return set()
    return pids


def _cmdline(proc: psutil.Process) -> str:
    try:
        return " ".join(proc.cmdline())
    except Exception:                    # noqa: BLE001
        return ""


def main() -> int:
    dry = "--dry-run" in sys.argv

    live = _listeners()
    if live:
        # 443 上还有服务在跑，说明这些 worker 不是孤儿，一律不动。
        print(f"reap: 443 已被 pid {sorted(live)} 监听，不做清理")
        return 0

    victims: list[psutil.Process] = []
    for proc in psutil.process_iter(["pid"]):
        if proc.pid == 0:
            continue
        cmd = _cmdline(proc)
        if not cmd:
            continue
        # 只认本仓库的 worker：别的项目也可能有同名脚本
        if "ocr_worker.py" in cmd and "rea" in cmd.lower():
            victims.append(proc)

    if not victims:
        print("reap: 没有遗留的 ocr_worker")
        return 0

    for proc in victims:
        try:
            rss = proc.memory_info().rss / 2 ** 20
        except Exception:                # noqa: BLE001
            rss = 0
        print(f"reap: {'[dry-run] ' if dry else ''}kill pid={proc.pid} "
              f"rss={rss:.0f}MB  {_cmdline(proc)[:90]}")
        if not dry:
            try:
                proc.kill()
            except Exception as exc:     # noqa: BLE001
                print(f"reap: kill {proc.pid} 失败：{exc}")

    if not dry:
        gone, alive = psutil.wait_procs(victims, timeout=5)
        if alive:
            print(f"reap: 仍有 {len(alive)} 个未退出：{[p.pid for p in alive]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
