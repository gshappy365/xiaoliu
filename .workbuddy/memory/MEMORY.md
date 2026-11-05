# xiaoliu 项目长期记忆

## 仓库结构

- `gshappy365/xiaoliu` —— 小六，Windows 单机客户端（.NET 8 + WPF）。
  - `src/Xiaoliu.Core` —— 协议 / 工具 / 语音 / 配置（纯逻辑，可跨平台单测）
  - `src/Xiaoliu.App` —— WPF 外壳（胶囊、工作区窗口、托盘、设置页）
  - `src/Xiaoliu.Cli` —— 无界面联调台（`health` / `chat` / `asr` / `tts` / `tools`）
  - `tests/Xiaoliu.Core.Tests` —— xunit
  - `docs/adr/` —— 决策记录，术语表在根目录 `CONTEXT.md`（改代码前先读，词表用它的说法）
- **本机真实工作副本是 `D:\gao\xiaoliu\mattpocock`**（不是 `xiaoliu-dsh`）。发布产物在
  `mattpocock\src\Xiaoliu.App\bin\Release\net8.0-windows\win-x64\publish\xiaoliu.exe`。
- 部署：`D:\gao\wbai\xiaoliu\`（`start-xiaoliu.bat` / `start-server.bat` / `watchdog.ps1` / `server-console.log`）。
- 计划任务：`xiaoliu-server`（rea 自建服务器，443）、`xiaoliu-server-watchdog`（每 2 分钟探 healthz）。
- **三副本已收敛**（2026-11-04）：`xiaoliu-dsh`（PR3 测试副本）/ `mattpocock`（主线）/ `potepo`（分线）
  → 代码主线统一到 `mattpocock`，分支 `feature/dual-connection`。
  potepo 的三项独有价值已逐项并入主线：未处理异常弹窗（进 `App.xaml.cs`）、`scripts/run-server.sh`
  （明文 8899 开发服务器）、数据目录隔离（**判定不需要**）。
- **点点反编译源码已移出小六仓库**（2026-11-05）→ 独立目录 `D:\gao\wbai\diandian-reference\`
  （766 文件 / 4.6MB：App 层 145 + Core 层 620 + 2 个 json + 2 个 txt）。
  小六仓库里 `reference/` **只剩一个指针 README**（入库）；`.gitignore` 改成
  `reference/*` + `!reference/README.md` —— 只放行这一个文件，从机制上杜绝闭源误提交。
  该目录**只读**，随时可能被重新反编译覆盖。

## 目录解耦现状（2026-11-05 盘点 + 当日推进）

**已解耦：**
- 点点反编译源码 ↔ 小六仓库 → 独立目录 `D:\gao\wbai\diandian-reference\`
  （766 文件 / 4.6MB：App 层 145 + Core 层 620 + 2 个 json + 2 个 txt）。
  小六仓库里 `reference/` **只剩一个指针 README**（入库）；`.gitignore` 改成
  `reference/*` + `!reference/README.md` —— 只放行这一个文件，从机制上杜绝闭源误提交。
  该目录**只读**，随时可能被重新反编译覆盖。
- 小六仓库的脚本 ↔ 服务器端绝对路径 → **删掉 `scripts/run-server.sh`**，
  改成让**服务器端启动器自己支持明文模式**：
  `TLS=0 PORT=8899 bash start-real.sh`（`run_live.py --plaintext` + `start-real.sh` 的 `TLS` 开关）。
  → 小六仓库里**不再有任何服务器端代码**，它就是个客户端。
  → 决策记录在 `docs/adr/0013-directory-decoupling.md`。

**尚未解耦（用户明确只做这两项，其余保留）：**

| # | 耦合 | 位置 | 备注 |
|---|---|---|---|
| C3 | 部署目录把服务器与客户端混装 | `D:\gao\wbai\xiaoliu\`：`start-server.bat`→rea、`watchdog.ps1`/`watchdog_host.py`/`launch_hidden.py`→rea、`server-console.log`，但也有 `start-xiaoliu.bat`→小六客户端 | 一个目录两个项目 |
| C4 | 文档用绝对路径交叉引用 | `CONTEXT.md:5`、`docs/adr/0008:18`、`docs/adr/0012` 多处指向 `D:\gao\wbai\rea\...`；另有 4 处代码注释指向 `rea/server/app/*.py` | 文档级 |
| C5 | 计划任务名误导 | `xiaoliu-server` / `xiaoliu-server-watchdog` 跑的其实是**点点的服务器**（rea），却挂着「小六」前缀 | 改名要重建计划任务，有中断风险 |
| — | 点点反编译源码仍在 rea 项目内 | `rea/decompiled/`(84 .cs) 与 `rea/decompiled-089/`(37 .cs) | 属服务器端项目的产物，`rea/reports/` 大量引用，搬迁要连带改报告 |

**搬动前必查的三件事（每次都查）**：csproj 是否 glob 该目录、仓库内有几处引用、
其他副本是否有重复副本。
→ 通用规律：**跨目录搬代码前先查 csproj 的 glob 与引用点**，否则构建会静默崩。

## 仓库与 PR 布局（GitHub: `gshappy365/xiaoliu`）

- 远程：`ssh://git@ssh.github.com:443/gshappy365/xiaoliu.git`（**SSH 走 443 端口**，不是标准 22）。
- **PR #3** = ADR 0011 云直连，head = `agent/standalone-client-refactor-bbf378d95476`（`23f9ea0`），base = `main`。
  → https://github.com/gshappy365/xiaoliu/pull/3
- **PR #4** = ADR 0012 双连接模式，head = `feature/dual-connection`（`cdd18ed`），
  base = **PR #3 的分支**（叠层 PR）→ https://github.com/gshappy365/xiaoliu/pull/4
  - 差异：21 文件 / +2073 −74，正好是 ADR 0012 那 5 个提交。
  - ⚠️ **合并顺序：先合 PR #3，再合 PR #4**（base 是 PR #3 的分支，PR #3 合并后 GitHub 会自动改写 base）。
- `gh` CLI 在 `/c/Users/李平/bin/gh`（不在系统 PATH 里）。
- 本地 `main`（`4d01150`）已被远程 `main`（`c221ac6`）超越 —— 远端含 PR #2 的合并，本地 main 未更新。

## 关键约定

### 双连接模式（ADR 0012，当前架构）

- **一个客户端内切换模式**，不拆两个 exe。
  - `ConnectionMode=cloud` → 客户端自带密钥直连火山方舟 / 火山语音（ADR 0011）。
  - `ConnectionMode=local-server` → 走自建服务器（`{BaseUrl}/chat` + WS 语音），与点点同构。
- **三个 `IChatTransport` 实现**：`ArkChatClient`（云）/ `OpenAiChatClient`（cloud+custom）/
  `LocalServerChatClient`（本地服务器）。工厂 `ChatTransportFactory.Create` 三路分派。
- **两套不兼容语音线协议并存**，用 `IVoiceSession` 接缝隔离：
  - 云直连 = 火山原生二进制帧（`VolcengineFrame`）
  - 本地服务器 = 点点 JSON 帧中继
  - `VoiceSessionFactory` 按 `settings.IsLocalServerMode` 分派。
- **配置项恢复**：`ConnectionMode` / `BaseUrl` / `AccessToken` / `AllowUntrustedTls`。
- 两条链路协议**同构**（Anthropic Messages SSE），本地服务器链路只差三处：
  1. 请求体多发私有字段 `main_reasoning_effort` / `vision_capable`
  2. **不发**原生 `thinking` 字段（服务器端 `rewrite_request` 读私有字段决定上游行为，转发前 `_DROP_FIELDS` 丢掉）
  3. 响应里的 `diandian_credits` 字段（客户端忽略）
- WS 路径换算：`HttpUri` 与 `WebSocketUri` 并存，`/proxy` 前缀换成 `/ws`。
- ADR 0012 取代 0008/0009；**不推翻** ADR 0011 的 `IChatTransport` 接缝，只加第三个实现。

### 其他约定

- **⚠️ rea/server 不是死代码，别下掉**（2026-11-04 更正，我自己先说错过一次）：
  hosts 里 `api/proxy/user/dl.botlearn.com.cn` 全部指向 `127.0.0.1`，**点点（Diandian 0.8.9）
  正在用 `D:\gao\wbai\rea\server` 这个自建服务端**（FastAPI，443 HTTPS，`/diandian/proxy` + `/diandian/ws`，
  自签证书在 `rea/probe/certs`）。ADR 0011 只是让 **xiaoliu 客户端**不再走它。
  所以 `xiaoliu-server` 计划任务与 `xiaoliu-server-watchdog` **必须保留**（点点靠它保活）。
  只有等点点也卸了，整套 rea/server 才真正变成死代码。
- 点点客户端本体在 `C:\Users\李平\AppData\Local\Programs\Diandian\Diandian.Windows.App.exe`。
  反编译源码在 `rea/decompiled/` 与 `rea/decompiled-089/`；术语表在 `rea/CONTEXT.md`。
- **凭证一律 DPAPI(CurrentUser) 加密落 `settings.json`**，格式 `dpapi:1:<base64>`，前缀兼作幂等闸。
  明文 key 不许进配置。
- **`AppSettings.SourcePath` 机制**（2026-11-04 根治）：`Save()` 写回来源路径而非默认目录；
  手工 `new AppSettings()` 的实例 `SourcePath` 为空 → **不写任何文件**。
  → 单测不再污染真实用户配置。改 `Save`/`Load` 相关代码时务必保住这个不变量。
- `Normalize()` 自愈：未知模式兜回 cloud、空 BaseUrl 补默认值、custom + 非法 URL 退回方舟模型（修 B2）。
- **⚠️ 本机 xiaoliu 从来没配过方舟密钥**（2026-11-04 实测）：两份配置备份里 `ArkApiKey` 都是空串，
  `VoiceAppId` / `VoiceAccessToken` 也是空。→ 这台机器上 xiaoliu 实际一直在走 **local-server 模式**
  （`BaseUrl=https://127.0.0.1/diandian/proxy` + `AccessToken=selfhost` + `AllowUntrustedTls=true`）。
  要切 `cloud` 模式必须先在设置页填方舟 Key，否则首次对话必然鉴权失败。
  → 恢复配置时默认模式应当是 `local-server`，不是 `cloud`。

## 本机执行要点（Windows）

- `dotnet` 8.0.427 在 `C:\Program Files\dotnet`，与 CI 用的一致。
- **桌面端命令里起的后台进程会随命令调用结束被杀掉**（job object）；要常驻必须经 `explorer.exe` 启动。
- `Add-Type` 被安全策略拦；窗口枚举 / UI 自动化要编译成独立 exe 跑。
- `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)` **不认 `LOCALAPPDATA` 环境变量**，
  没法靠改 env 隔离配置目录。
- 读系统 exe 用 PowerShell 工具（Bash 里 spawn 系统级 exe 会假报 "No such file or directory"）。
- 仓库自带中文注释很多，控制台输出在 bash 里是 GBK 乱码 —— 看 PASS/FAIL 计数即可，别当成 bug。
- WPF 发布用项目自带配置：`dotnet publish -c Release -o <dir>`；
  **不要加 `--self-contained false`**（与 csproj 的 `PublishSingleFile` 冲突 → NETSDK1176）。
- UI 自动化探针：设置页的「模型服务」在**二级导航**里，主界面默认停在「对话」页 → 先点「设置」再选目标项。

## 待办（2026-11-04 PR #3 审查发现）

**已修：**
- ~~B1 改设置后 transport 不重建~~ → `Companion.RebuildTransport()`（重建 transport + `ReplaceTransport`，保留对话历史）。
- ~~B2 custom + 空 base URL 启动即崩~~ → 三层防护：`Normalize()` 自愈 + 工厂兜底不抛 + `App.OnStartup` try/catch。
- ~~PasswordBox 白底刺眼~~ → 新增 `PasswordBoxStyle`（`BoxStyle` 的 `TargetType` 是 `TextBox`，套不到 PasswordBox）。
- ~~单测污染真实用户配置~~ → `AppSettings.SourcePath` 机制。

**未修：**
- I1 TTS session_id 语义反了（应客户端生成）；`VolcengineFrame` 的 Write/Parse 帧布局不一致，会整帧错位。
- I2 TTS 鉴权头应改 `X-Api-Key` + `X-Api-Resource-Id: seed-tts-2.0`（现用旧版 `X-Api-App-Key` + `volc.service_type.10029`）。
- I3 ASR/TTS 错误被吞（`_errorMessage` 从未对外暴露），变成空串 / 30 秒「判定卡住」。
- I4 视觉能力判断逻辑在两处各存一份（`ConversationAgent.cs` / `ToolDispatcher.cs`），且「未知」被当成「不能看图」。
- I5 `capture_model_cannot_see` 未接进 `FallbackMessage`。
- I6 thinking 块可能缺 `signature` 回传（**方舟侧是静默劣化，不是 400**）。
- 方舟套餐 profile `agent-plan_cn-beijing_personal` **2026-11-28 到期**。
- `connect_screen_regions` 工具两次「未完成」是独立问题，未深查。

## 复现/工具

- 端到端探针工程（仓库外）：`D:\gao\xiaoliu\_pr3_probe\`
  （`Probe.csproj` 65 条断言 + `MockServer.cs` 裸 TcpListener chunked SSE mock + `uiprobe/` WPF 自动化截图）。
- PR #3 测试报告：`D:\gao\xiaoliu\PR3-本地测试报告.md`。
- rea 排查工具：`D:/gao/wbai/rea/tools/`（`tools_probe_toolcall.py` / `tools_probe_timing.py` / `tools_probe_e2e.py`）。
- 打本机 443 必须绕过系统代理（Clash 7897），否则拿到误导性的 502。
  python 用 `build_opener(ProxyHandler({}), HTTPSHandler(context=ctx))` —— `OpenerDirector.open()` **不接受 `context=`**。
