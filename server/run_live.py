#!/usr/bin/env python3
"""一键起 xiaoliu（含 TLS 与可选假上游）。

点点只认 https://…:443，所以真接入必须占 443。这个脚本负责：
  1. 校验证书
  2. 检查端口占用（443 被占时给出明确原因）
  3. 可选同时拉起假上游，并把 xiaoliu 指向它
  4. 打印点点侧的自检命令

用法：
    python run_live.py                    # 443 + 真上游（读环境变量）
    python run_live.py --port 8443        # 换端口自测
    python run_live.py --fake-upstream    # 443 + 假上游（无需 API key）
"""
from __future__ import annotations

import argparse
import datetime as dt
import os
import socket
import subprocess
import sys
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
CERT_DIR = os.path.join(os.path.dirname(HERE), "probe", "certs")
CERT = os.path.join(CERT_DIR, "server.crt")
KEY = os.path.join(CERT_DIR, "server.key")
FAKE_PORT = 8901


def port_free(port: int) -> tuple[bool, str]:
    s = socket.socket()
    try:
        s.bind(("0.0.0.0", port))
        return True, ""
    except OSError as exc:
        return False, f"winerror={getattr(exc, 'winerror', '?')} {exc}"
    finally:
        s.close()


def who_on(port: int) -> str:
    """尽量说清是谁占着，省得来回猜。"""
    try:
        out = subprocess.run(
            ["powershell", "-NoProfile", "-Command",
             f"$c=Get-NetTCPConnection -LocalPort {port} -State Listen "
             f"-ErrorAction SilentlyContinue;"
             f"if($c){{$c|%{{(Get-Process -Id $_.OwningProcess).ProcessName}}}}"
             f"else{{'(Windows 侧无监听进程)'}}"],
            capture_output=True, text=True, timeout=15)
        return out.stdout.strip() or "(无)"
    except Exception:
        return "(查不到)"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=443)
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--fake-upstream", action="store_true",
                    help="同时拉起 tests/fake_upstream.py 并指过去")
    ap.add_argument("--model", default=None, help="覆盖 UPSTREAM_MODEL")
    ap.add_argument("--log-file", default=None,
                    help="把服务端日志同时写进文件（默认 logs/server-YYYYMMDD.log，传 - 关闭）")
    a = ap.parse_args()

    for f in (CERT, KEY):
        if not os.path.exists(f):
            print(f"缺少证书 {f}\n先跑：python probe/make_ca.py")
            return 2

    ok, why = port_free(a.port)
    if not ok:
        print(f"端口 {a.port} 绑不上：{why}")
        print(f"Windows 侧监听者：{who_on(a.port)}")
        if a.port == 443:
            print(
                "\n443 被占的常见原因（按可能性排序）：\n"
                "  1. WSL 里的 probe.py 还在跑 —— 之前抓包用的探针没停。\n"
                "     WSL 里执行：sudo pkill -f probe.py\n"
                "  2. IIS / 其它 Web 服务\n"
                "     Windows 里执行：netstat -ano | findstr :443\n"
                "\n探针当前特征：响应头带 `Server: Python/3.14 aiohttp`，\n"
                "且 healthz 会转发到真实 CloudFront。\n"
                "\n先用 --port 8443 自测 TLS，等 443 空出来再正式接入。")
        return 3

    children: list[subprocess.Popen] = []
    env = {**os.environ, "PORT": str(a.port), "HOST": a.host,
           "LOG_BODIES": os.environ.get("LOG_BODIES", "1")}

    # 日志落盘：排查时最怕「刚才那轮到底走到哪了」查不到。
    log_fp = None
    if a.log_file != "-":
        log_path = a.log_file or os.path.join(
            HERE, "logs", f"server-{dt.date.today():%Y%m%d}.log")
        os.makedirs(os.path.dirname(os.path.abspath(log_path)), exist_ok=True)
        log_fp = open(log_path, "a", encoding="utf-8", buffering=1)
        log_fp.write(f"\n===== {dt.datetime.now():%Y-%m-%d %H:%M:%S} "
                     f"port={a.port} fake_upstream={a.fake_upstream} =====\n")
        env["PYTHONUNBUFFERED"] = "1"
        print(f"日志：{log_path}")

    def _pump(stream, fp):
        """边打屏边落盘，交互运行时也不会瞎。"""
        for raw in iter(stream.readline, b""):
            line = raw.decode("utf-8", "replace")
            sys.stdout.write(line)
            sys.stdout.flush()
            if fp:
                fp.write(line)
        stream.close()

    try:
        if a.fake_upstream:
            env["UPSTREAM_BASE_URL"] = f"http://127.0.0.1:{FAKE_PORT}"
            env["UPSTREAM_API_KEY"] = "fake"
            # 让假上游选中 tool 场景：服务器会把 model 改写成这个值
            env["UPSTREAM_MODEL"] = a.model or "SCENARIO:tool"
            env["MOCK_MODE"] = "0"
            fake = subprocess.Popen(
                [sys.executable, "-m", "uvicorn", "tests.fake_upstream:app",
                 "--host", "127.0.0.1", "--port", str(FAKE_PORT),
                 "--log-level", "warning"],
                cwd=HERE, env={**os.environ,
                               "FAKE_UPSTREAM_RECORD": os.path.join(HERE, "tests", "_live_record.json")})
            children.append(fake)
            time.sleep(2)
            print(f"假上游已起：http://127.0.0.1:{FAKE_PORT}（场景 {env['UPSTREAM_MODEL']}）")
        elif a.model:
            env["UPSTREAM_MODEL"] = a.model

        print(f"xiaoliu 起在 https://{a.host}:{a.port}")
        print(f"  上游：{env.get('UPSTREAM_BASE_URL') or '(未配置)'}")
        print(f"  模型：{env.get('UPSTREAM_MODEL') or '(默认)'}")
        print("\n点点侧自检：")
        print(f'  curl.exe -k https://api.botlearn.com.cn:{a.port}/diandian/proxy/healthz'
              if a.port != 443 else
              '  curl.exe -k https://api.botlearn.com.cn/diandian/proxy/healthz')
        print("\nCtrl-C 停止。\n")

        child = subprocess.Popen(
            [sys.executable, "-m", "uvicorn", "app.main:app",
             "--host", a.host, "--port", str(a.port),
             "--ssl-certfile", CERT, "--ssl-keyfile", KEY,
             "--log-level", "info"],
            cwd=HERE, env=env,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        children.append(child)
        threading.Thread(target=_pump, args=(child.stdout, log_fp),
                         daemon=True).start()
        return child.wait()
    except KeyboardInterrupt:
        return 0
    finally:
        for c in children:
            if c.poll() is None:
                c.terminate()
                try:
                    c.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    c.kill()
        if log_fp:
            log_fp.close()


if __name__ == "__main__":
    sys.exit(main())
