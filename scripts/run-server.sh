#!/usr/bin/env bash
# xiaoliu 开发启动器：复用 rea 服务器 start-real.sh 的环境配置（真上游与套餐能力），
# 但起在明文 8899 供 xiaoliu 客户端直连。不占 443，不动点点的 hosts/TLS 测试床。
#
# 这是三套工程解耦（ADR 0012）后的开发通道：点点继续用 443 + 自签证书那条，
# xiaoliu 走这条明文端口，两边互不干扰。
#
#   PORT=8899 HOST=127.0.0.1 bash scripts/run-server.sh
#
# 客户端对应设置：连接方式＝本地服务器，服务器地址＝http://127.0.0.1:8899/diandian/proxy
#
# 环境变量全部来自 start-real.sh 的 export 段（含从 arkcli profile 取 ARK Key），
# 本脚本不复制任何密钥。
set -euo pipefail

SERVER_DIR="${SERVER_DIR:-D:/gao/wbai/rea/server}"
ROOT="${ROOT:-D:/gao/wbai/rea}"
HERE="$SERVER_DIR"
PORT="${PORT:-8899}"
HOST="${HOST:-127.0.0.1}"

eval "$(sed -n '/^export PYTHONIOENCODING/,/^export CHAT_DUMP_DIR/p' "$SERVER_DIR/start-real.sh")"

cd "$SERVER_DIR"
exec "$ROOT/.venv/Scripts/python.exe" -m uvicorn app.main:app --host "$HOST" --port "$PORT"
