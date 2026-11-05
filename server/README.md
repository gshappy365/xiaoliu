# xiaoliu 服务器端（P1）

与点点（Diandian 0.8.9）**逐字段线协议兼容**的服务器端。点点可以直接连上来，
不需要改客户端一个字节。

- 语言/框架：Python 3.13 + FastAPI
- 线上协议：Anthropic Messages SSE
- 兼容性依据：不是读文档，是把输出喂给点点自己的 `pipeline_brain_ffi.dll` 验过
  （见 `tests/test_against_native_decoder.py`）

---

## 1. 为什么这样设计

点点把「解析模型流」这件事下沉到了 Rust 的 `pipeline_brain_ffi.dll`，C# 侧只是薄壳。
所以**唯一的权威规范是那个 DLL**，不是文档、也不是抓包样本。

结论（`reports/decoder-abi-oracle-20261026.md` 有完整实测记录）：

| 事实 | 含义 |
|---|---|
| 线上就是 Anthropic SSE，没有自定义外层 | 上游是 Anthropic 兼容中转时**可以纯透传**，不需要重新编码 |
| 两个解码器读**同一份字节流** | 服务器只输出一种格式，客户端自己按有无工具二选一 |
| 结束标志是 `data: [DONE]`，不是 `message_stop` | 不发这帧，客户端循环不退出 |
| `agent` 解码器对事件顺序**严格** | 必须发 `message_start → blocks → message_delta → message_stop` |
| `tool_use.input` 必须走 `partial_json` 分片 | 整块发也能被拼上，但分片才是原样 |
| 解码器每次 push 独立 UTF-8 解码 | 一个汉字被 TCP 切开就变 `�`（客户端自身缺陷） |

因此本服务器**不做协议翻译**，只做四件事：剥私有字段、按客户端选的模型选上游、转发字节、补 `[DONE]`。
兼容性由「同一方言」保证，而不是由我重新编码的正确性保证。

---

## 2. 快速开始

### 2.1 mock 模式（不连上游，验协议）

```bash
cd server
../.venv/Scripts/python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 8899
```

```bash
curl -s http://127.0.0.1:8899/diandian/proxy/healthz
curl -s http://127.0.0.1:8899/diandian/v1/credits
```

### 2.2 接真上游

任何 Anthropic Messages 兼容端点都行（DeepSeek Anthropic 兼容口、one-api、
new-api、LiteLLM、官方 Anthropic……）：

```bash
export UPSTREAM_BASE_URL="https://api.deepseek.com/anthropic"
export UPSTREAM_API_KEY="sk-..."
export UPSTREAM_MODEL="deepseek-v4.1-flash"
../.venv/Scripts/python.exe -m uvicorn app.main:app --host 0.0.0.0 --port 8899
```

> 服务器会向 `{UPSTREAM_BASE_URL}/v1/messages` 发 POST。若你的中转路径不同，
> 改 `app/upstream.py` 里 `stream_chat` 拼 URL 的那一行。

### 2.3 环境变量

| 变量 | 默认 | 说明 |
|---|---|---|
| `UPSTREAM_BASE_URL` | — | 上游根地址，不含 `/v1/messages` |
| `UPSTREAM_API_KEY` | — | 上游密钥；同时放进 `x-api-key` 和 `Authorization` |
| `UPSTREAM_MODEL` | `deepseek-v4.1-flash` | **默认上游**用的模型；客户端选的模型不在方舟前缀里时才落到它 |
| `ARK_API_KEY` | — | 方舟套餐 Key；留空则只有默认上游可用 |
| `ARK_ANTHROPIC_BASE_URL` | `https://ark.cn-beijing.volces.com/api/plan` | 方舟的 Anthropic 兼容端点（**不带 `/v3`**，和识图那条 `/api/plan/v3` 不是一套协议） |
| `UPSTREAM_TIMEOUT` | `300` | 秒 |
| `MOCK_MODE` | `0` | `1` 时不连上游，回显请求结构 |
| `REQUIRE_AUTH` | `0` | `1` 时要求带 `Authorization` 头（不校验内容） |
| `LOG_BODIES` | `1` | 只记结构（消息数/块类型/工具名），**不记内容** |
| `PORT` / `HOST` | `8787` / `0.0.0.0` | 注意 Windows 上 8787 可能被保留，用 8899 |

---

## 3. 让点点连上来

点点把端点**硬编码**在 `ProxyServiceEndpoint.cs` 里，没有配置覆盖项。
所以只能劫持域名。整套材料在 `probe/` 下已经有了。

### 3.1 起 TLS（客户端走 https）

```bash
cd server
../.venv/Scripts/python.exe -m uvicorn app.main:app \
  --host 0.0.0.0 --port 443 \
  --ssl-keyfile ../probe/certs/server.key \
  --ssl-certfile ../probe/certs/server.crt
```

### 3.2 装根证书 + 改 hosts

```bash
cd probe
python make_ca.py          # 若 certs/ 里已有可跳过
# 双击 ca.cer 装到「受信任的根证书颁发机构」
# 双击 hosts-on.bat（会自提权、自动备份、自动 flushdns）
```

`hosts-on.bat` 当前劫持 `api` / `user` / `dl` 三个子域。**要直连 `/chat` 还需要
`proxy.botlearn.com.cn`** —— 见下面第 6 节的待办。

### 3.3 一键起（含 TLS）

```bash
cd server
../.venv/Scripts/python.exe run_live.py --port 8443 --fake-upstream   # 自测，不需要 key
../.venv/Scripts/python.exe run_live.py                               # 正式，占 443
```

脚本会校验证书、检查端口、按需拉起假上游，并打印点点侧的自检命令。

**443 被占用时**（`winerror=10048`）：最常见原因是 WSL 里的 `probe.py` 还在跑。
探针的指纹很好认——响应头带 `Server: Python/3.14 aiohttp`，且 healthz 会转发到真实 CloudFront。
WSL 里执行 `sudo pkill -f probe.py` 即可让出端口。

### 3.4 验证

```bash
ping api.botlearn.com.cn     # 必须是 127.0.0.1（别用 nslookup，它不读 hosts）
curl.exe -k https://api.botlearn.com.cn/diandian/proxy/healthz
```

然后启动点点，正常说话。客户端会先拉 `capabilities`、`credits`、`activation`，
再打 `/chat`。

### 3.5 证书的已知取舍

服务端叶子证书已补 `AuthorityKeyIdentifier`（`probe/reissue_server_cert.py` 可单独重签，
**不需要重装根证书**）。但根证书是第一版生成的、没有 `SubjectKeyIdentifier`，
而 Python 3.13 默认开启 `VERIFY_X509_STRICT`，所以 httpx/curl 这类 OpenSSL 系客户端
会报 `Missing Subject Key Identifier`。

- **点点不受影响**：它走 .NET → schannel，本来就宽容，实测 `ssl_verify_result=0`
- **测试脚本**用 `tests/e2e_tls.py` 里的 `strict_off_context()`，只关严格模式、仍验链
- 要让所有工具都干净通过，只能重签根证书并重装 `ca.cer` —— 一次性操作，按需再做

### 3.6 还原

```bash
probe/hosts-off.bat        # 还原 hosts + 删根证书
```

---

## 4. 已实现的端点

路径前缀照抄抓包（`api.botlearn.com.cn/diandian/*`）。

### 核心

| 端点 | 行为 |
|---|---|
| `POST /diandian/proxy/chat` | **Anthropic Messages SSE**，透传上游，补 `[DONE]` |
| `GET /diandian/proxy/capabilities` | 模型/音色清单 |
| `GET /diandian/proxy/healthz` | `{"status":"ok",...}` |
| `POST /diandian/proxy/credits/settlements` | 404 `credits_write_rejected`（真实服务端也这样） |

### 账号与额度（全部 stub 成「无限」）

`GET /v1/credits`、`POST /v1/credits/quotes`、`GET /v1/credits/transactions`、
`GET /v1/activation`、`GET /v1/account/lifecycle`、`GET /v1/account/profile`、
`GET /v1/public/runtime-parameters`、`GET /v1/payments/{orders,credits,products,capabilities}`

### 遥测（收下就丢）

`POST /v1/metrics/asr`、`POST /v1/metrics/interactions/batches`

### 语音（P1 占位）

| 端点 | 状态 |
|---|---|
| `WS /diandian/ws/tts/volcengine` | 协议完整（`connected/ready/text/finish/finished`），音频是**静音 PCM** |
| `WS /diandian/ws/asr/volcengine` | 协议完整（`ready/partial/final`），**识别结果为空** |
| `POST /diandian/transcribe-token` | 占位 token |
| `POST /diandian/tts` | 静音 PCM |
| `POST /oidc/token` | 发长期假 token（点点 access_token 只有 900 秒） |

语音协议形状 100% 照抄抓包（`reports/protocol-capture-20261026.md` 第三节），
只有音频数据是假的。

---

## 5. 验收

```bash
# 协议层：把生成的 SSE 喂给点点原生解码器
../.venv/Scripts/python.exe tests/test_against_native_decoder.py

# 端到端（明文）：真发 HTTP，响应字节再喂给原生解码器
../.venv/Scripts/python.exe tests/e2e_local.py

# 端到端（TLS + 真域名）：最接近点点的视角
../.venv/Scripts/python.exe run_live.py --port 8443 --fake-upstream
../.venv/Scripts/python.exe tests/e2e_tls.py 8443

# 透传链路 + 工具回环（自起两个进程，跑完自动收）
../.venv/Scripts/python.exe tests/integration_tool_loop.py
```

`test_against_native_decoder.py` 覆盖 6 项：

1. 无工具路径 → SSE 解码器增量拼接正确、`[DONE]` 后 `finished=True`
2. 有工具路径 → agent 解码器 `text` 是累积全文、`tool_use.input` 完整还原、
   `stopReason=tool_use`、`usage` 正确
3. TCP 分片 1/3/7/13 字节切片全部还原
4. UTF-8 跨片**已知有损**（把客户端缺陷固定成预期，避免以后误判）
5. 缺 `message_start` 会被判 `malformed` → 证明必须发完整序列
6. 不发 `[DONE]` 则 `finished=False` → 证明该帧必需

`integration_tool_loop.py` 覆盖 6 项（xiaoliu ⇄ 假上游 ⇄ 原生解码器）：

1. 文本透传 + 服务器自动补 `[DONE]`
2. 请求改写：剥 `diandian_credits`/`main_reasoning_effort`、换 model、带密钥头
3. 工具轮 1：`stopReason=tool_use`，`input` 由 `partial_json` 拼成
4. 工具轮 2：`tool_result` 原样透传（含 base64 图片 + label），第二轮 `end_turn`
5. 上游 529 → 降级成 error 流，客户端读到 `provider_error` 而不是 HTTP 错误
6. stub 端点全通，`settlements` 正确返回 404

---

## 6. 待办

**P1 已完成**：协议层、HTTP 端点、语音协议壳、端到端验收。

**P2 候选**：

1. **让出 443** —— hosts 已劫持 `api`/`proxy`/`user`/`dl` 四条，证书链也验过
   （`ssl_verify_result=0`）。**唯一阻塞点是 443 被 WSL 里的旧探针占着**，
   WSL 内 `sudo pkill -f probe.py` 即可。之后 `run_live.py` 默认就占 443。
2. **接真语音**：火山引擎流式 ASR（16 kHz PCM 上行）与 TTS（24 kHz PCM 下行）。
   `VolcengineRelayProtocol.cs` 的字段已全部确认，只需替换 `main.py` 里两处
   发静音/回空串的代码。
   **朗读文本必须先过 `speakable_prefix`（在第一个 `[` 截断）**，否则会把
   `[STEP:S5]`、`[链接](url)` 念出来。注意 `pointing.spokenText` 只剥末尾
   `[POINT]`，不能直接拿去 TTS——详见 `reports/self-verification-crosscheck.md`。
3. **工具执行回环**：点点把 `tool_use` 结果作为 `tool_result` 回传，服务器要把
   多轮工具调用串起来（`maxChatCalls` 上限 8）。
4. **图片块**：`{"type":"image","source":{"type":"base64",...}}` 后跟一个
   `{"type":"text","text":label}`。透传即可，但要确认上游模型支持视觉。
5. **本地持久化**：点点是 90 天 / 200MB 明文 SQLite，且文本与图片分两个参数存。
   自建可以不做，或做更保守的。

**已知限制**：

- 服务器不做鉴权（`REQUIRE_AUTH=0` 时谁都能连），只适合本机/内网
- `LOG_BODIES=1` 只记结构，但如果打开上游调试日志，屏幕内容会进日志
- TTS/ASR 是空实现，点点会「听到静音」而不是报错
