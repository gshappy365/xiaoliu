# 单机云直连：客户端自带密钥，废除自建服务器依赖

## Context

小六现有的三条链路（chat SSE、ASR 中继 WS、TTS 中继 WS）全部指向自建服务器
（`BaseUrl` 默认 `https://127.0.0.1/diandian/proxy`）：服务器按模型前缀把请求转发到
火山方舟的 Anthropic 兼容端点、为纯文本模型跑 VL 转述、并中继火山语音。客户端除了
这三个端点形状和 `BaseUrl`/`AccessToken` 两个设置项，其余（工具、策略、抓屏、历史、
UI）全部本地。

用户决定把小六改成单机本地使用：不再要求服务器进程在跑。ADR 0009 曾把「客户端直连
方舟」否决，理由是密钥落到客户端、要重写 SSE 和工具回环 —— 前一个理由被用户接受
（自用场景，密钥自己管），后一个理由不成立：客户端本来就在说 Anthropic Messages SSE
（`SseReader`、工具回环、thinking 帧都是现成的），直连方舟的 Anthropic 兼容端点
换的是基地址和鉴权头，不是协议层。

## Decision

- **云直连**：对话与看图走火山方舟的 Anthropic 兼容端点，用户在设置里填自己的
  方舟 API key；不再经过任何自建服务器。「本地」指不依赖服务器进程，模型仍在云端。
- **硬切**：删除服务器路径 —— `/diandian` 路径换算（`WireProtocol`）、chat 代理、
  ASR/TTS 中继 WS、`healthz`/`capabilities`、「连接」设置页与 `BaseUrl`/`AccessToken`/
  `AllowUntrustedTls` 设置项。设置页「连接」改为「模型服务」。
- **密钥存储**：方舟 key 与火山语音凭证（appid/access token）用 Windows DPAPI
  （CurrentUser）加密后落 `settings.json`，不进配置明文。换机器需重新填写。
- **语音直连**：ASR/TTS 走火山语音技术的原生 WS 协议，客户端自己维持连接与 partial
  语义（服务器滚动重解码带来的 partial 行为不再存在）。
- **纯文本模型 + 图片**：图片请求直接拒绝并提示换视觉组模型。服务器的 VL 转述兜底
  不再存在，也不在客户端复刻它（延续 ADR 0009 反对 VL 转述的理由：丢颜色布局、
  多一次调用）。
- **模型清单**：23 项快照不动（全方舟）；设置里预留一个自定义 OpenAI 兼容端点
  （base URL + key + 单个模型名，自由输入），配置后下拉出现「自定义：<模型名>」。
  自定义端点与方舟组**能力对齐**：工具回环翻译成 OpenAI function calling，
  圈选图翻译成 `image_url` 数据 URL，工具回执走 `role:"tool"` 消息。
- **取代关系**：本 ADR 取代 ADR 0009 中「不直连方舟」的结论（其 vision_capable
  的服务端语义随之消失）；ADR 0008 的「服务器按客户端 model 前缀路由上游」失效 ——
  上游只剩方舟与自定义端点两个，路由在客户端设置里，不在前缀猜测里。

## Considered Options

- **保留自建服务器做双模式**：自用场景不值得双份设置、双份测试面和一份必须继续
  维护的服务器。
- **key 明文存 settings.json**：与现状 `AccessToken="selfhost"` 一致，但接真实云
  凭证后违反团队「key 不进配置明文」规则。
- **客户端复刻 VL 转述兜底**：见上，成本与失真都不值。
- **完全离线本地模型（Ollama 等）**：「看屏幕 + 动手」需要的视觉与指令能力本地小
  模型给不了，单文件 exe 捆绑推理运行时是另一个量级的工程；自定义 OpenAI 兼容端点
  留了将来接本地推理服务的口子。

## Consequences

- 客户端第一次持有真实云凭证：DPAPI 绑定当前用户，备份/迁移 settings.json 不会
  带走密钥，重装要重填。
- 服务器的隐性能力消失：VL 转述、滚动重解码 partial、自签证书放行
  （`AllowUntrustedTls` 随硬切删除）。用户文档要写清行为变化。
- 没有服务器日志可对账，排障全靠客户端日志（`%LOCALAPPDATA%\xiaoliu\logs`），
  日志里要补请求级的上游错误透出。
- CLI 的 `health` 从「测服务器」变为「测直连上游」；`chat`/`asr`/`tts` 跟着新
  传输层走，子命令面不变。
- 自定义端点的 OpenAI 翻译层（工具回环 + 图片）是一块独立的、只服务自定义端点的
  代码，方舟主链路不经过它。
