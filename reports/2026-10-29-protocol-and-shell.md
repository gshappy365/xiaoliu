# v1 线协议层与常驻外壳：做到哪、怎么验的

日期：2026-10-29　仓库：`D:\gao\xiaoliu\mattpocock`

## 一句话

14 个工具的客户端、双 WebSocket、常驻外壳（托盘 + 字幕条 + 指点层 + 圈选层 + 历史窗）已经能跑；**线协议层对着自建服务器逐项验过**，语音两端都是真实往返。

## 已验证（全部对着 443 上在跑的服务器）

| 项 | 命令 | 结果 |
|---|---|---|
| HTTP 连接 | `xiaoliu-cli health` | `/healthz` 200，`/capabilities` 909 字节 |
| 无工具对话 | `chat "用一句话介绍你自己"` | 首字 5758ms，1 次请求，`stop=end_turn` |
| 工具回环 + 抓屏拒绝 | `chat --tools "你看看我屏幕上写的是什么"` | 模型调 `capture_screen` → `capture_not_user_initiated` → 改口请用户圈选，2 次请求 |
| 圈选图 + 指点 | `chat --tools --capture "…告诉我最左边的图标在哪"` | `point_at_screen` accepted，图形坐标 (96,540)，1 次工具调用 |
| ASR 中继 | `asr asr-20261028-132612.wav` | 8.00s 音频 → 「呃，你可以用这个 overlay 在当前屏幕画一个三角…」 |
| TTS 中继 | `tts "小柳在这里，线协议已经通了。"` | 首帧 4049ms，258568 字节 ≈ 5.39s 音频 |
| 单元测试 | `dotnet test` | 31 项全绿 |

界面部分单独截了图确认：字幕条（`artifacts/ui-subtitle.jpg`）、四种指点图形（`artifacts/ui-overlay.jpg`，用 `--demo` 触发）。

## 两个当场撞上的坑

### 1. `messages` 里塞了数组 → 服务器 500

第一版把用户内容块（`[{type:image}, {type:text}]`）直接 `messages.Add(blocks)`，于是 `messages = [[...]]`。
服务器 `upstream.summarize()` 里 `m.get("content")` 直接 `AttributeError: 'list' object has no attribute 'get'`。

修法：`{role:"user", content: blocks}` 包一层。**这条值得记**：服务器对消息形状是「假定合法」的，客户端错了就是 500 而不是 400。

### 2. 内容指纹逐字节比较 → 第二次指点必然 stale

第一次跑「圈选 + 指点」时，第一个 `point_at_screen` 成功，紧接着的 `highlight_screen_region` 被判 `stale_capture`。
原因不是屏幕真变了，而是**我们自己的控制台输出把画面改了**。

修法：指纹改成 32×32 灰度 + 8% 容差（`ScreenCapture.Difference`）。
光标闪烁、时钟秒数、一行字幕大约 3–5% 格子；换页面 50% 以上。两头都留了余量，测试锁在 `ScreenSignatureTests`。

### 顺带修掉的一个体验问题

模型会把「策略拒绝」当成「临时故障」反复重试：实测一次提问里它调了 3 次 `capture_screen`、3 次 `point_at_screen`，6 次请求、72 秒，最后给了一段 200 字的道歉。

现在：同一工具被策略拒绝第二次就置位「本轮停止工具恢复」；一轮里所有回执都不可重试时，直接收尾并用一句人话兜底（`ToolDispatcher.FallbackMessage`）。同样的提问降到 2 次请求、15 秒。

## 与点点的对照（实现时逐条核对过）

| 环节 | 点点 | xiaoliu |
|---|---|---|
| 请求体 | model/max_tokens/stream/system/messages/main_reasoning_effort/tools/diandian_credits | 同，但不发 `diandian_credits` |
| SSE 解码 | 原生 `pipeline_brain_ffi.dll` | 自写 `SseReader`（`\n\n` 分帧、`[DONE]` 结束） |
| 工具回环 | 6 次请求 / 20 次调用上限 | 同 |
| 工具结果 | `{type:tool_result, tool_use_id, content, is_error}` | 同 |
| 圈选坐标 | 0-1000 归一化，基准是截图 | 同 |
| 截图有效期 | 45 秒定时 | 内容指纹 + 8% 容差 |
| 热键 | WH_KEYBOARD_LL，纯修饰键按住 250ms | 同 |
| WS 前缀 | `/diandian/ws/*`（HTTP 是 `/diandian/proxy/*`） | 同，`WireProtocol.WebSocketUri` 换算 |

## 代码规模

`src/Xiaoliu.Core` 约 2.6k 行（协议 4 文件、工具 2、取图 2、指点 1、存储 1、语音 1、Agent 2）
`src/Xiaoliu.App` 约 1.1k 行（外壳 1、编排 1、窗口 4、音频 1、热键 1）

## 还没做的

1. **单文件发布没跑过**：`dotnet publish` 的配置写好了，没验证产物能否在干净机器上起。
2. **语音整链没有真人验证**：ASR 和 TTS 分别验过，但「按住热键 → 听到回答」这一步需要人按键（低级键盘钩子不收合成输入）。
3. **多屏未测**：取图与 overlay 都按虚拟桌面并集写，只有一块屏的机器上验过。
4. **`insert_text` 没实机验证**：`SendInput` 逐字键入的路径没在真实目标窗口上跑过。
