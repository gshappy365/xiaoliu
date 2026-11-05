#!/usr/bin/env bash
# 用真实上游启动 xiaoliu（占 443，点点直连）。
#
# 上游：讯飞星火 MaaS（Anthropic 兼容）
#   https://maas-api.cn-huabei-1.xf-yun.com/anthropic
#   model: spark-x2.6
#
# 四层能力：
#   LLM    spark-x2.6（云端，Anthropic Messages SSE）
#   ASR    SenseVoice int8（本地，sherpa-onnx，纯 CPU）｜ doubao-seed-asr-2.0（可选）
#   TTS    SAPI（默认，快）/ edge-tts（自然）/ doubao-seed-tts-2.0（套餐）
#   视觉   火山 doubao-seed-2-0-mini（套餐，2 秒）→ 商汤 SenseNova VL →
#          PaddleOCR-VL（本地 GPU）→ Windows OCR
#
# 可覆盖的环境变量：
#   VISION_MODE   auto|vl|ocr|pass|strip   默认 auto（有 VL 用 VL，失败回退 OCR）
#   VISION_VL_BACKEND auto|ark|sensenova  VL 档由谁实现，默认 auto（ark 优先）
#   VISION_API_KEY / VISION_MODEL / VISION_EFFORT / VISION_TIMEOUT
#   OCR_BACKEND   auto|paddle|windows     OCR 引擎，默认 auto（paddle 优先）
#   PADDLE_OCR_MODEL_DIR  PaddleOCR-VL 模型目录，必须是纯 ASCII 路径
#   PADDLE_OCR_DEVICE     gpu|cpu，默认 gpu
#   TTS_BACKEND   auto|sapi|edge|doubao  默认 auto（=sapi）
#   ASR_BACKEND   local|doubao           默认 local（云端约 2 秒，且没有 partial）
#   ARK_API_KEY   套餐专属 Key；留空则启动时从 arkcli profile 自动读取
#   ARK_VISION_MODEL / ARK_TTS_SPEAKER  套餐内的识图模型与默认音色
#   ASR_MODEL_DIR SenseVoice 目录        默认 <repo>/models/sense-voice
#   ASR_PARTIAL   0 关掉边录边出字        默认开
#   ASR_STUB_TEXT 识别失败时的兜底文本    默认「看看我的屏幕」
#   ASR_SILENCE_TEXT 录到静音时的提示文本  默认「（没有听到声音，请再说一遍）」
#   ASR_DUMP_DIR  把每次录音存成 wav 的目录（排查麦克风问题用）
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$HERE")"

# 虚拟环境的解释器路径在两个平台上不一样：Windows 是 .venv/Scripts/python.exe，
# macOS / Linux 是 .venv/bin/python。写死任一个都会让脚本在另一台机器上直接失败。
if [ -x "$ROOT/.venv/bin/python" ]; then
  PY="$ROOT/.venv/bin/python"                 # macOS / Linux
elif [ -x "$ROOT/.venv/Scripts/python.exe" ]; then
  PY="$ROOT/.venv/Scripts/python.exe"         # Windows（Git Bash）
else
  echo "找不到虚拟环境，先建：python3 -m venv .venv 然后 pip install -r server/requirements.txt" >&2
  exit 2
fi
export PY

export PYTHONIOENCODING=utf-8
export UPSTREAM_BASE_URL="${UPSTREAM_BASE_URL:-https://maas-api.cn-huabei-1.xf-yun.com/anthropic}"
export UPSTREAM_API_KEY="${UPSTREAM_API_KEY:-ak-REDACTED-SET-ENV-OR-FILL-LOCALLY}"
export UPSTREAM_MODEL="${UPSTREAM_MODEL:-spark-x2.6}"
# 思考策略：smart = 只有「本轮没有截图、必须主动调工具」的回合开 1024 token
# 思考，其余回合保持关闭以压低首字延迟。
# 实测关掉思考时模型只有 1/5 会真的调用 capture_screen，其余只叙述「我去截一张」，
# 客户端拿不到工具调用就表现为「没回信」；开 1024 后是 5/5。
export UPSTREAM_THINKING="${UPSTREAM_THINKING:-smart}"
export VISION_MODE="${VISION_MODE:-auto}"
# 视觉上游：商汤 SenseNova（OpenAI 兼容 /v1）。文本上游 spark-x2.6 只吃文本，
# 要真识图必须另配一个多模态端点。
export VISION_BASE_URL="${VISION_BASE_URL:-https://token.sensenova.cn/v1}"
export VISION_API_KEY="${VISION_API_KEY:-sk-REDACTED-SET-ENV-OR-FILL-LOCALLY}"
export VISION_MODEL="${VISION_MODEL:-sensenova-6.8-flash-lite}"
# 推理预算。留空 = 让模型自己决定。
# 别手动压低：实测 none/low 会把点点认成 Edge / Windows 相册 / VS Code 文本文件。
# 错误的识图比「看不到」更危险，宁慢勿错。
export VISION_EFFORT="${VISION_EFFORT:-}"
export VISION_TIMEOUT="${VISION_TIMEOUT:-60}"
# 本机 OCR：PaddleOCR-VL-1.6（0.9B，RTX 3060 上 6-14 秒），替换 Windows OCR。
# 模型目录**必须**是纯 ASCII 路径：SentencePiece 与 Paddle Inference 的 C++ 层
# 打不开含中文的路径，而 Python 的 os.path.exists() 却返回 True，失败信息还被
# paddlex 包成 `return False`，排查成本极高。仓库内 models/ 满足要求。
export OCR_BACKEND="${OCR_BACKEND:-auto}"
export PADDLE_OCR_MODEL_DIR="${PADDLE_OCR_MODEL_DIR:-$ROOT/models/paddlex/official_models/PaddleOCR-VL-1.6}"
# macOS 没有 CUDA，paddle 的 gpu 设备会直接初始化失败；默认改成 cpu，
# 需要 MPS 时自己设 PADDLE_OCR_DEVICE=mps（paddle 对 MPS 支持有限，未必可用）。
if [ "$(uname -s)" = "Darwin" ]; then
  export PADDLE_OCR_DEVICE="${PADDLE_OCR_DEVICE:-cpu}"
else
  # 2026-11-04 起 Windows 上默认 off：它占 RTX 3060 约 10GB 显存，而这条链是
  # 冷路径（VL 失败才轮到它）。挪 CPU 也实测过不可行：模型加载 44s、推理 178s。
  # 彻底下线后 OCR 兜底交给 Windows OCR（<1s）。要恢复 GPU 版就设
  # PADDLE_OCR_DEVICE=gpu 并重启服务。
  export PADDLE_OCR_DEVICE="${PADDLE_OCR_DEVICE:-off}"
fi
# 单次推理超时。正常 6 秒；首次 kernel 编译会到 15 秒左右。
# 超过 30 秒基本可判定是卡死而不是在算（卡住时 GPU 利用率 5%、SM 时钟 210MHz），
# 这时杀掉 OCR 子进程、降级到 Windows OCR，5 分钟后自动重试。
export PADDLE_OCR_TIMEOUT="${PADDLE_OCR_TIMEOUT:-30}"
export PADDLE_OCR_STARTUP_TIMEOUT="${PADDLE_OCR_STARTUP_TIMEOUT:-60}"
export PADDLE_OCR_MAX_TOKENS="${PADDLE_OCR_MAX_TOKENS:-2048}"
# 默认走豆包（点点请求的是 volcengine.xiaohe-2.0 音色，SAPI 会变成 Huihui，
# 音色明显不同）。auto 模式按 tts.py 的设计优先 SAPI，所以这里必须显式写 doubao。
# 云端不可用时 ark_tts 返回空，tts.py 会自动回退 SAPI，不会没声音。
export TTS_BACKEND="${TTS_BACKEND:-doubao}"

# ASR 后端。qwen3 = 本机 llama.cpp Vulkan 跑 Qwen3-ASR-1.7B Q8_0
# （计划任务 qwen3-asr，8930 端口），实测 6-8s 音频 0.2s 出结果，准确率
# 明显好于 SenseVoice；失败/llama-server 不在时自动回退 local，不会没字。
# 想换回 SenseVoice：ASR_BACKEND=local。没有 partial（整段识别）。
export ASR_BACKEND="${ASR_BACKEND:-qwen3}"

# ---- 火山 Agent Plan：TTS / 识图 / ASR（套餐内，不走按量）----
# Key 不写死：先用环境里的 ARK_API_KEY，没有就从已登录的 arkcli profile 取。
# 套餐到期（当前 profile 显示 2026-11-28）后这些端点会失效，届时对应后端
# 自动标记不可用并回退本地，不会中断服务。
export ARK_CLI_PROFILE="${ARK_CLI_PROFILE:-agent-plan_cn-beijing_personal}"
if [ -z "${ARK_API_KEY:-}" ] && command -v arkcli >/dev/null 2>&1; then
  ARK_API_KEY="$("$PY" -c '
import json, subprocess, sys
try:
    out = subprocess.check_output(
        "arkcli profile apikey get --profile " + sys.argv[1] + " --format json",
        shell=True)
    print(json.loads(out)["api_key"])
except Exception:
    print("")
' "$ARK_CLI_PROFILE" || true)"
fi
export ARK_API_KEY="${ARK_API_KEY:-}"
export ARK_PLAN_BASE_URL="${ARK_PLAN_BASE_URL:-https://ark.cn-beijing.volces.com/api/plan/v3}"
# 对话用的 Anthropic 兼容端点（不带 /v3）。客户端在设置里选了方舟模型时走这条；
# 和上面那个 /v3 不是一套协议，别合并。
export ARK_ANTHROPIC_BASE_URL="${ARK_ANTHROPIC_BASE_URL:-https://ark.cn-beijing.volces.com/api/plan}"
export ARK_VISION_MODEL="${ARK_VISION_MODEL:-doubao-seed-2-0-mini-260428}"
# minimal = 不思考（实测 reasoning_tokens=0，识图仍然准确）
export ARK_VISION_EFFORT="${ARK_VISION_EFFORT:-minimal}"
export VISION_VL_BACKEND="${VISION_VL_BACKEND:-auto}"
export ARK_TTS_URL="${ARK_TTS_URL:-https://openspeech.bytedance.com/api/v3/plan/tts/unidirectional}"
export ARK_TTS_RESOURCE_ID="${ARK_TTS_RESOURCE_ID:-seed-tts-2.0}"
export ARK_TTS_SPEAKER="${ARK_TTS_SPEAKER:-zh_female_vv_uranus_bigtts}"
# 云端 ASR 比本地慢 35 倍，默认关闭；设 doubao 才启用。
export ASR_BACKEND="${ASR_BACKEND:-local}"
export ARKCLI_BIN="${ARKCLI_BIN:-arkcli}"
export ASR_MODEL_DIR="${ASR_MODEL_DIR:-$ROOT/models/sense-voice}"
export ASR_STUB_TEXT="${ASR_STUB_TEXT:-看看我的屏幕}"
export ASR_SILENCE_TEXT="${ASR_SILENCE_TEXT:-（没有听到声音，请再说一遍）}"
# 默认就落盘：热键类问题只能靠波形定位，「识别出 N 个字」看不出是静音还是不准。
# 每次按住热键都会留一个 wav，排查完可以清空。
export ASR_DUMP_DIR="${ASR_DUMP_DIR:-$ROOT/evidence/asr-dump}"
export CHAT_DUMP_DIR="${CHAT_DUMP_DIR:-$ROOT/evidence/chat-dump}"

cd "$HERE"

# 清掉上一轮遗留的 OCR worker。2026-10-30 12:34 服务退出后留下两个 worker 孤儿，
# 显存从 1.36GB 被吃到 10.5GB，新 worker 申请不到显存只能降级到 Windows OCR。
# 脚本内部会先看 443 有没有人监听：有人在监听就一律不动（说明不是孤儿）。
"$PY" "$HERE/reap_orphans.py" || true

exec "$PY" run_live.py --port "${PORT:-443}" "$@"
