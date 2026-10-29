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
| 单文件发布 | `dotnet publish -c Release` | 67,653,825 字节，能起、日志正常、未写自启项 |
| `insert_text` 全链路 | `xiaoliu.exe --demo typing` | 从外部进程手里抢回前台，30 字全部送达，退出码 0；**发布产物上复跑同样通过** |
| 单元测试 | `dotnet test` | 43 项全绿 |

界面部分单独截了图确认：字幕条（`artifacts/ui-subtitle.jpg`）、四种指点图形（`artifacts/ui-overlay.jpg`，用 `--demo` 触发）。

## 当场撞上的坑

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

### 3. `insert_text` 抢不到前台时，字会打进别的程序

这是收尾时才挖出来的，也是这批里唯一一个会**造成实际破坏**的问题。

`SendInput` 投递给「当前前台窗口」，不是投递给句柄。原来 `insert_text` 的写法是：

```csharp
NativeWindows.Focus(match.Handle);
Thread.Sleep(180);
NativeWindows.TypeText(text);
```

`Focus` 里只有一句 `SetForegroundWindow`。当前台被别的进程占着时，这个调用会被前台锁挡掉，**返回 false 但不抛异常**，而调用方把返回值丢了。结果就是：窗口没切过去，字照样发出去，全打进了用户当时正在用的那个程序。

第一次写的自检没测出这个问题 —— 靶子窗口是自己进程的，没有外部进程来抢前台，等于没测。改成先起一个 notepad 把前台占住再抢回来，问题立刻现形：

```
阶段2 外部进程在前台=True 靶子窗口激活=False
阶段3 SetForegroundWindow 返回=False
阶段3 抢回前台=True 系统真实前台=Notepad.exe WPF_IsActive=True
阶段4 期望 30 字 / 送出 30 键 / 收到 0 字 / ok=False
```

注意那行 `WPF_IsActive=True`：**WPF 的 `IsActive` 会说谎**，它记的是自己的账，不是系统眼里的前台。判据只能用 `GetForegroundWindow() == handle`。

两处修：

1. `NativeWindows.Focus` 用 `AttachThreadInput` 把本线程的输入队列临时挂到「当前前台线程」和「目标线程」上再交接 —— 前台锁只拦非前台线程，挂上之后这次交接就是合法的。返回值改成 `GetForegroundWindow() == handle`，即「真的切过去了没有」。
2. `insert_text` 在键入前连查两道：抢前台失败 → `target_not_focusable`，180ms 后前台又被抢走 → `target_lost_focus`，两个都是**一个字符都不打**，且不可重试（`retry_allowed=false`），避免模型把环境问题当成参数问题反复试。

修完自检全绿：

```
阶段3 抢回前台=True 系统真实前台=xiaoliu.exe WPF_IsActive=True
阶段4 期望 30 字 / 送出 30 键 / 收到 30 字 / ok=True
```

复跑命令：`xiaoliu.exe --demo typing`，退出码 0 = 通过。它自带一个记事本当「别人占着前台」的对照组，跑完自己收尾。

为了能测「抢不到前台就不许打字」这条，`ToolDispatcher` 的窗口操作抽成了 `IWindowWriter`（`Focus` / `IsForeground` / `Type`），生产实现直接落 Win32，测试用桩断言 `Type` 从未被调用 —— `InsertTextFocusTests` 四条。

### 4. `search_history` 的空查询等于「把最近 5 段对话交出去」

SQL 原来是 `WHERE question LIKE '%' || $q || '%'`。两个口子：

- `query` 为空 → `LIKE '%%'` → 命中全部，再按 `id DESC LIMIT 5` 取最近 5 条。模型不提任何关键词就能翻用户的对话记录。
- `query` 里带 `%` 或 `_` → 当通配符使，一句 `%` 同样拿到全部。

修法：空查询直接返回空（并在工具层回 `invalid_arguments`，让模型知道是参数问题而不是「没找到」）；`%` / `_` / `\` 转义后配 `ESCAPE '\'` 按字面匹配。`HistorySearchTests` 五条锁住。

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

`src/Xiaoliu.Core` 3379 行 / 17 文件（协议 5、工具 2、取图 2、指点 1、存储 1、语音 1、Agent 2、Interop 2、配置 1）
`src/Xiaoliu.App` 1861 行 / 12 文件（外壳 1、编排 1、窗口 4、音频 1、热键 1、自检 1、其余 3）
`tests/Xiaoliu.Core.Tests` 约 900 行 / 7 文件（43 项）

## 还没做的

1. ~~语音整链没有真人验证~~ **已于 2026-10-29 15:28–15:33 实机跑通 4 轮**，问答记录见 `reports/2026-10-29-acceptance-checklist.md` 的 C 组。
2. **多屏未测**：取图与 overlay 都按虚拟桌面并集写，只有一块屏的机器上验过。
3. **`insert_text` 只在自检靶子上验过**：抢前台 + 键入全链路通了，但没往真实第三方应用（记事本以外的）里填过；富文本/自绘控件可能另有脾气。
4. **`read_user_file` 的「用户明确请求」还只是提示词，没有客户端强制**：现在只校验「绝对路径 + 是文件 + ≤512KB」，不校验这个路径有没有出现在用户本轮原话里。schema 里写的是「只读用户本轮明确请求的文件」，模型照做，但绕过去没有任何拦截。要真按「策略全部客户端执行」的口径落实，得让调度器拿到本轮用户原话，按文件名（或完整路径）比对，没出现就回 `path_not_user_requested`。**这条没做，因为会改变产品行为，等确认。**

## 挂起的 P0：按用户决定不动

交接文档第 5 步要求「联调跑通后清 hosts + 删自签 CA」。这次问过用户，选择**先不动**，等整机不再需要自建服务端时一起清。

查证结果记在这里，将来动手时不用重新摸：

- `C:\Windows\System32\drivers\etc\hosts` 第 28–32 行，5 条指向 `127.0.0.1`：`update.discord.com` + `api/user/dl/proxy.botlearn.com.cn`
- 自签 CA `CN=xiaoliu probe CA`，指纹 `F0991C42BF9FEECEDF2DC489B655ED6CA70D960E`，`CurrentUser\Root` 与 `LocalMachine\Root` **两处都在**
- `probe/hosts-off.bat` 不能直接用：它只过滤 `botlearn`，`update.discord.com` 会留下；而且用 `Set-Content -Encoding UTF8` 会写 BOM，hosts 带 BOM 有解析风险
- **xiaoliu 不依赖这两项**：直连 `https://127.0.0.1/diandian/proxy`（字面 IP，不走 DNS），且 `AllowUntrustedTls=true` 跳过证书校验。所以将来清理不会影响 xiaoliu，受影响的只有点点（它会回到真云端）

