# xiaoliu.exe

单机本地使用的 Windows 客户端（ADR 0011）：对话/看图直连火山方舟的 Anthropic 兼容端点，
语音直连火山语音技术网关，密钥由用户自己填、DPAPI 加密落盘 —— 不再需要任何自建服务器。
与点点并列存在，不是点点的补丁。

## 结构

```
src/Xiaoliu.Core/     云直连传输、工具与策略、取图、存储、语音直连（无 UI，可单测）
src/Xiaoliu.App/      WPF 常驻客户端：托盘 + 胶囊 + 字幕条 + 指点层 + 圈选层 + 主工作区
src/Xiaoliu.Cli/      无界面联调台，用来证明协议层通了
tests/                单元测试（SSE 分帧、工具策略、留存、内容指纹）
docs/adr/             客户端级决策
reference/            只剩一个指针 README：点点的反编译源码已移出仓库（闭源，见该 README）
```

## 构建与运行

```bash
dotnet build src/Xiaoliu.App/Xiaoliu.App.csproj -c Release
dotnet publish src/Xiaoliu.App/Xiaoliu.App.csproj -c Release   # 单文件 exe
```

产物：`src/Xiaoliu.App/bin/Release/net8.0-windows/win-x64/publish/xiaoliu.exe`

开发时直接跑 `dotnet run --project src/Xiaoliu.App`。加 `--demo` 会把四种指点图形一次画出来；加 `--demo ui` 连顶部胶囊和回答气泡一起摆出来（截图见 `artifacts/ui-v2-bubble.jpg`）。

加 `--window` 启动后直接开主工作区，可以带页面名：`home` / `history` / `settings`，
设置页还能二级定位，例如 `--window settings:voice`。

## 界面

三个部分：

| 位置 | 内容 | 怎么出来 |
|---|---|---|
| 托盘图标 | 打开主界面、历史、设置、圈选、开机自启、日志目录、退出 | 一直在 |
| 顶部胶囊 | `● 小六`，带状态（在听 / 在想） | 默认显示，可在设置里关 |
| 主工作区 | 1000×680，左侧导航：对话 / 历史 / 设置 | **鼠标移到胶囊上**就展开；托盘双击/菜单打开时钉住 |

主工作区不是独立窗口，是**从胶囊下面拉出来的一块面板**（对齐点点的交互）：

- 鼠标移到顶部胶囊 → 面板从胶囊下方滑出，**不抢焦点**（你正在别的窗口打字不会被切走）
- 鼠标离开面板约 0.7 秒 → 自动淡出收起
- 点一下胶囊 → 钉住，鼠标可以随便走；再点一下收起
- `Esc` → 收起（面板里正在打的草稿不丢）
- 面板被点进去（激活）时不会自动收起


**对话页**：打字或按住热键说话；「圈选一块区域」「选择图片」把画面交给我；下方卡片显示
这一轮能看到什么（哪几张图、指纹是否还对得上）。

**主动看屏幕**：问「你现在能看到我屏幕上是什么吗」，我会自己抓一张当前屏幕再回答
（ADR 0010）。不想要这个行为就在设置 → 通用 → 画面里关掉「允许我主动看屏幕」，
关掉后画面只能由你圈选提供。一轮最多抓两次；抓屏行为都会写进日志。

**设置页**：七类 —— 通用（自启、热键、按住时长、自检）、语音（设备、试麦、音色、中间结果）、
模型（模型、推理强度、请求上限）、外观（胶囊、气泡跟随、指点图形）、数据（留存、上限、
清空）、模型服务（方舟 key、火山语音凭证、自定义 OpenAI 兼容端点）、关于（版本与策略说明）。
改完即存；密钥留空表示保持已配置的值。

**模型下拉**的候选来自火山方舟（`arkcli models list`），按能力分两组：能看懂截屏的 10 个、
只看文本的 13 个。清单是 2026-10-30 的快照，写死在
`Xiaoliu.Core/Configuration/ModelCatalog.cs`，过期了照那里的注释重新生成。
选中的名字存进 `settings.json` 的 `Model`，云直连后客户端把它直接发给方舟的
Anthropic 兼容端点（`ark.cn-beijing.volces.com/api/anthropic/v1/messages`）。换模型后下一轮生效。

选纯文本模型时，圈选/截图类提问会被直接拦下并提示换「能看懂截屏」的模型 ——
服务器的 VL 转述兜底已随解耦取消（ADR 0011），客户端不再替你把图转成文字。

**推理强度**：低/标准/深 对应 Anthropic 原生 `thinking` 的 1024/4096/8191 预算；
预算装不下 `max_tokens` 时会自动抬高上限。这是把原服务器私有字段
`main_reasoning_effort` 换成协议原生参数，实测口径以你机器上的冒烟为准。

**自定义端点**：在「模型服务」里填一个 OpenAI 兼容的 base URL + key + 模型名，
模型下拉会多出「自定义：模型名」一项。工具回环翻译成 function calling、
圈选图翻译成 `image_url`、工具回执走 `role:"tool"` 消息 —— 能力与方舟组对齐。

结构与点点的 `MainWorkspaceWindow` 同构（左侧 232 DIP 导航 + 设置作为窗口内的一页 + 二级分类 +
52 高的设置行），但不搬账号、课程、技能、积分这四类 —— 小六没有对应能力，摆上去是死入口。
详见 `docs/adr/0006-workspace-and-settings.md`。

### 自检：`--demo typing`

```bash
xiaoliu.exe --demo typing   # 退出码 0 = 通过
```

验证 `insert_text` 的完整链路：起一个记事本把前台占住（模拟「小六在后台」），再从它手里把靶子窗口抢回前台，键入 30 个中英混合字符并核对。日志在 `%LOCALAPPDATA%\xiaoliu\logs\`。

这条自检不是摆设：它抓到过一个会把字打进别的程序的 bug。`SendInput` 投递给的是**当前前台窗口**而不是句柄，抢前台失败还照打，字符就进了用户当时正在用的程序 —— 而 WPF 的 `IsActive` 这时还显示 `true`。现在 `insert_text` 抢不到前台就一个字符不打，直接回 `target_not_focusable`。


## 配置

`%LOCALAPPDATA%\xiaoliu\settings.json`，首次运行不自动创建（用默认值跑），改过设置后才会写出来。

**改这个文件前先退出小六**：配置是整份读进内存的，退出或改设置时会整份写回，运行期间手改会被覆盖。

密钥三个字段（`ArkApiKey`、`VoiceAppId`、`VoiceAccessToken`，外加可选的 `CustomApiKey`）
**在文件里是 DPAPI 加密串**（`dpapi:1:…`，Windows 当前用户级），不是明文 —— 别想直接手改，
到设置 → 模型服务里填，留空表示保持原值。换机器或重装系统后密文解不开，重填一次即可。

```json
{
  "ArkApiKey": "dpapi:1:…",
  "VoiceAppId": "dpapi:1:…",
  "VoiceAccessToken": "dpapi:1:…",
  "Model": "doubao-seed-2-1-pro-260915",
  "CustomBaseUrl": "",
  "CustomModelName": "",
  "PushToTalkModifiers": "Control,Alt",
  "SelectRegionModifiers": "Control,Alt",
  "SelectRegionKey": "S",
  "TextRetentionDays": 90,
  "ImageRetentionDays": 7,
  "MaxTotalMegabytes": 200
}
```

上游端点写死在 `WireProtocol`：方舟 `https://ark.cn-beijing.volces.com/api/anthropic/v1/messages`，
语音 `wss://openspeech.bytedance.com/api/v3/{saas/bigmodel, tts/bidirection}`。旧配置里的
`BaseUrl`/`AccessToken`/`AllowUntrustedTls` 已随服务器路径一起删除，读入时被忽略。

## 联调

不需要服务器。先在设置 → 模型服务里填好方舟 key（语音验收再填语音凭证），然后：

```bash
# 1. 凭证与上游连通（真实往返一次，1 token）
./src/Xiaoliu.Cli/bin/Debug/net8.0-windows/xiaoliu-cli.exe health

# 2. 一轮对话（不声明工具）
.../xiaoliu-cli.exe chat "用一句话介绍你自己"

# 3. 工具回环：模型会试着抓屏，被拒后改口
.../xiaoliu-cli.exe chat --tools "你看看我屏幕上写的是什么"

# 4. 带圈选图 + 指点
.../xiaoliu-cli.exe chat --tools --capture "我圈了这块屏幕，用指点告诉我最左边的图标在哪"

# 5. 语音两端（直连火山，需要语音凭证）
.../xiaoliu-cli.exe asr /path/to/16k-mono.wav
.../xiaoliu-cli.exe tts "小六在这里" --out tts.wav
```

## 操作

| 动作 | 键 |
|---|---|
| 按住说话 | Ctrl + Alt（按住 ≥250ms） |
| 圈选一块区域 | Ctrl + Alt + S，拖框，松手即取图 |
| 让它自己看当前屏幕 | 直接问（如「你能看到我屏幕吗」），它会调 capture_screen |
| 清空指点图形与截图 | Esc |
| 打断正在念的回答 | 再按一次 Ctrl + Alt |
| 打开主界面 / 历史 / 设置 | 托盘菜单，双击托盘，或点顶部胶囊 |
| 退出 | 托盘菜单 → 退出（关主窗口不退出进程） |

## 已知限制

- 取图用 GDI `BitBlt`，受 DRM 保护的窗口和独占全屏会拿到黑图。要覆盖这些场景得换 WGC。
- 同一时间只能和一个进程抢热键：小六与点点同时运行会互相干扰，二选一。
- `insert_text` 要求能把目标窗口切到前台。切不过去（比如目标被别的程序盖着、或用户在切过去的瞬间又点了别处）就**一个字符都不打**并回 `target_not_focusable` / `target_lost_focus`，请用户手动点一下目标窗口再说一次。宁可没填，也不能填错地方。
- `report_text_insertion` 的 `written` 结论被策略拒绝（没有画面可核对），填字只能到 `uncertain`。
- 语音直连火山后，partial 是火山流式识别的原生行为（识别中持续下发累计文本），不再有服务器滚动重解码那一层。
- 多屏只按虚拟桌面并集写，没在真实多屏机器上验过。
- 设置里改的项生效时机不一致：模型/音色/推理强度下一轮生效，胶囊显隐要重开，留存天数下次启动清理时才生效。
- 主窗口没有深色/浅色切换，只有深色一套。
