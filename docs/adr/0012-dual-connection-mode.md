# 三套工程解耦：xiaoliu 支持本地服务器与云直连双模式

## Context

本机同时跑着三套东西，它们今天靠「hosts 劫持 + 私有协议」耦在一起：

| 工程 | 位置 | 角色 |
|---|---|---|
| 点点客户端 | `C:\Users\李平\AppData\Local\Programs\Diandian` | 商业客户端，改不了字节 |
| 服务器端 | `D:\gao\wbai\rea\server` | 替 botlearn 云端，点点靠 hosts 指过来 |
| xiaoliu 客户端 | `D:\gao\xiaoliu\mattpocock` | 自研客户端，点点与它并列 |

ADR 0011 把 xiaoliu 改成**硬切**云直连：删掉 `BaseUrl`/`AccessToken`/`AllowUntrustedTls`，
删掉 `/diandian` 路径换算，语音改走火山原生 WS 协议。这解开了「xiaoliu 依赖服务器进程」，
但同时产生两个新问题：

1. **服务器端不能只服务点点。** 点点那 8 个对话模型 id 全是 `aliyun.*` / `volcengine.*` /
   `deepseek.*`，服务端改不了，只能靠别名映射兜住。xiaoliu 这边如果也要用服务器端，
   就得和点点共用同一套路由与推理策略 —— 但两者对「思考预算」「图片透传」的诉求不同。
2. **xiaoliu 的云直连链路完全不可用。** 方舟套餐 profile `agent-plan_cn-beijing_personal`
   2026-11-28 到期，之后 `ArkApiKey` 直接 401，整机哑巴。而服务器端那条路本来就能通。

用户要的是：**三套工程互不绑架**，各自能独立演进 ——
点点配本地服务器、xiaoliu 也能配本地服务器、xiaoliu 同时保留单机云直连。

## Decision

### 1. 双模式，不是双客户端

xiaoliu 客户端**一个 exe**，设置页「模型服务」下多一个「连接方式」：
`云直连` / `本地服务器`。切换只换 transport 与语音栈，不换进程、不换数据目录。

选它的理由：三条链路（chat SSE、ASR WS、TTS WS）的差异全在「往哪儿发」，
编排、工具回环、抓屏、历史、UI 完全共用。拆成两个 exe 等于把共用部分复制两份，
以后修一处要同步两处 —— 正是上一轮 PR 审查里 B1「改设置不生效」的同类病根。

### 2. 对话层：新增 `IChatTransport` 第三个实现

ADR 0011 已经把对话层收敛成接缝（`IChatTransport` + `ChatTransportFactory`）。
本 ADR **不推翻**它，只加第三个实现：

| 模式 | 实现 | 端点 | 鉴权 |
|---|---|---|---|
| 云直连 | `ArkChatClient` | 方舟 Anthropic 兼容端点 | `Authorization: Bearer <方舟 key>` |
| 云直连 + 自定义 | `OpenAiChatClient` | 自定义 OpenAI 兼容端点 | 同上 |
| 本地服务器 | `LocalServerChatClient` | `{BaseUrl}/chat` | `Authorization: Bearer <AccessToken>` |

关键事实：**服务器端的 `/diandian/proxy/chat` 本身就是 Anthropic Messages SSE**
（`rea/server/app/main.py:100`），请求体与方舟端点同构。所以
`LocalServerChatClient` 与 `ArkChatClient` 共享 SSE 解析（`SseReader` +
`ChatStreamAccumulator`），只差三处：URL、鉴权头、请求体里的私有字段。

### 3. 请求体的私有字段：随模式切换

服务器端的 `rewrite_request`（`rea/server/app/upstream.py:261`）**读**这三个字段来决定
上游行为，然后在转发前把它们 `_DROP_FIELDS` 丢掉：

| 字段 | 服务器端拿它干什么 | 云直连下 |
|---|---|---|
| `main_reasoning_effort` | 映射成上游 `thinking.budget_tokens` | 不发，改用协议原生 `thinking` |
| `vision_capable` | 决定图片原样透传还是跑 VL/OCR 转文本 | 不发，客户端自己拒纯文本模型 |
| `diandian_credits` | 点点计费字段 | 不发 |

所以**本地服务器模式必须发前两个**，否则：图片会被降级成文字描述（用户看到的是
「一张模糊的界面截图」而不是模型真正看到的像素），思考预算会退回默认值。
这不是「兼容旧协议」的怀旧，而是「按对方真实需要提供它读的那几个字段」。

### 4. 语音层：两套协议栈并存，用接缝隔开

这是本 ADR 最硬的一块。两条链路的**线协议完全不同**：

| | 云直连（ADR 0011） | 本地服务器（点点的线协议） |
|---|---|---|
| ASR | 火山原生二进制帧，负序号空包收口 | JSON 帧 `start`→`ready`→`partial*`→`finish`→`final` |
| TTS | 火山事件帧 `StartConnection`→`StartSession`→`TaskRequest` | JSON 帧 `connected`→`start`→`ready`→`text*`→`finish`→`finished` |
| 音频 | 裸 PCM（16k ASR / 24k TTS） | 裸 PCM，同 |

好消息是两套的**公开 API 形状完全一致**
（`ConnectAsync` / `SendAudioAsync` / `SendTextAsync` / `FinishAsync` / `CancelAsync`
+ `PartialReceived` / `AudioReceived` 事件 + `SampleRate`），
所以编排层（`Companion`）不需要知道走哪条。

落地方式：抽 `IVoiceSession` 接缝 + `VoiceSessionFactory`，
把 ADR 0011 的火山原生实现与服务器端的 JSON 帧实现各自归位，
`Companion` 只跟工厂说话。

### 5. 配置：新增「连接方式」，旧字段按需复活

`AppSettings` 新增 `ConnectionMode`（`cloud` / `local-server`），
并**恢复** `BaseUrl` / `AccessToken` / `AllowUntrustedTls` 三个字段 ——
它们只在 `local-server` 模式下有意义，云直连模式下不读。

ADR 0011 的硬切是「在只有一条路时」的正确决策。现在有两条路，
配置项跟着走是自然的，不是回退。

### 6. 凭证存储不变

`ArkApiKey` / `VoiceAppId` / `VoiceAccessToken` / `CustomApiKey` 继续走
DPAPI(CurrentUser)（`SecretProtector`）。`AccessToken` 是本地服务器的自定口令，
按 ADR 0011 之前的形态明文存即可 —— 它不是云凭证，没有加密价值，
却给「换机后解不开」添麻烦。

### 7. 修掉 PR 审查发现的两个阻断项

本 ADR 的实现必须顺带修掉上一轮审查发现的：

- **B1**：改设置/换端点后 transport 不重建 → 设置页显示「通」但真实对话 401。
  根因是 `Companion._chat` 是 `readonly`、只在构造时建一次。
  修法：设置页保存后显式重建 transport 与 Agent。
- **B2**：`Model=custom` + 空 base URL → 启动即崩（`App.OnStartup` 抛异常，
  WPF 的 `DispatcherUnhandledException` 兜不住启动期异常）。
  修法：`Normalize()` 里对空 base URL 做自愈，工厂构造期不再抛。

### 8. 服务端不动业务逻辑

服务器端**不改**。本地服务器模式用的就是它今天给点点用的那套接口 ——
这正是「解耦」的意思：xiaoliu 是服务器端的**又一个客户端**，
而不是把服务器端改造成 xiaoliu 专用后端。

`rewrite_request` 读的私有字段是**客户端提供的**，不是服务器端对客户端的假设，
所以 xiaoliu 补上这些字段不需要碰服务端一行代码。

## Considered Options

- **拆成两个独立 exe**（一个走服务器、一个走云直连）：见上，共用部分要复制两份，
  修复要同步两次。否决。
- **让 xiaoliu 也改用 hosts 劫持**：没必要。服务器端的 HTTP 路径由
  `BaseUrl` 给出，只有 WS 的 `/proxy` 前缀换算需要客户端做（`WireProtocol.WebSocketUri`）。
- **在客户端复刻服务端的 VL 转述兜底**：ADR 0009 已否决，理由不变
  （丢颜色布局、多一次调用）。本地服务器模式下由服务端做这件事，客户端不碰。
- **把语音也直连，服务器端只做 chat 中继**：语音直连的凭证与 chat 不同源
  （语音技术 appid/token vs 方舟 key），拆开只会让「半直连」更难排障。
- **让服务器端感知 xiaoliu 的存在**（比如加 `/xiaoliu/*` 路径）：违背解耦目标。
  服务器端不该知道有几种客户端。

## Consequences

- **配置项回来了**：`BaseUrl` / `AccessToken` / `AllowUntrustedTls` 在设置页
  「本地服务器」分区下才显示，云直连分区下不出现。
- **`IChatTransport` 有三个实现**，测试面变大 —— 但每个实现的协议边界清晰，
  探针能对同一份请求体分别断言三种模式的请求内容。
- **语音栈有两份**。这是双模式的必然代价：两条线协议不兼容，
  强行统一会造出第三种协议。抽象层（`IVoiceSession`）保证编排层不受影响。
- **服务器端继续是点点的刚需**。它不是 xiaoliu 的依赖，反过来。
- **模式切换不影响历史数据**：对话记录、截图、留存策略都在
  `%LOCALAPPDATA%\xiaoliu`，两种模式共用。
- **排障要分清是哪条链路**：客户端日志打出当前模式，服务端日志记录
  `X-DianDian-Client-Build`（点点不带头、xiaoliu 带头），否则
  「方舟 401」和「服务器 502」会混在一起看。
- **开发通道独立**：`scripts/run-server.sh` 让服务器端起在明文 8899，
  不占 443、不动点点的 hosts/TLS 测试床。点点走 443，xiaoliu 走 8899，
  开发时两边能同时跑。

## 三副本收敛

本机曾有三份 xiaoliu 副本，指向同一条主线但各走各的：

| 副本 | 角色 | 处置 |
|---|---|---|
| `D:\gao\xiaoliu\mattpocock` | 真实工作副本 | **保留为唯一主线**，本 ADR 在此落地 |
| `D:\gao\xiaoliu\potepo` | 支线（数据目录隔离 + 崩溃日志 + 开发脚本） | 有价值的三项已并入主线，副本可弃 |
| `D:\gao\xiaoliu\xiaoliu-dsh` | PR 审查用的 HTTPS 克隆 | 一次性审查工具，可弃 |

`potepo` 三项独有内容的处置：

- **崩溃日志（crash.log + 错误弹窗）**：主线的 `Log` 类已覆盖日志能力
  （按日分文件、带堆栈），只把**错误弹窗**并入主线 —— 静默失败比崩溃更难查。
- **数据目录隔离**（`%LOCALAPPDATA%\potepo`）：主线统一用
  `%LOCALAPPDATA%\xiaoliu`，双线共目录的问题已不存在，不需要。
- **`scripts/run-server.sh`**：并入主线，作为解耦后的独立开发通道（见上）。
