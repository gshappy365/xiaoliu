# xiaoliu.exe

自建服务器端的 Windows 客户端。与点点并列存在，不是点点的补丁 —— 点点继续当协议判定器。

## 结构

```
src/Xiaoliu.Core/     线协议、工具与策略、取图、存储、语音中继（无 UI，可单测）
src/Xiaoliu.App/      WPF 常驻客户端：托盘 + 字幕条 + 指点层 + 圈选层 + 历史窗
src/Xiaoliu.Cli/      无界面联调台，用来证明协议层通了
tests/                单元测试（SSE 分帧、工具策略、留存、内容指纹）
docs/adr/             客户端级决策
reference/            点点的反编译产物（.gitignore，不入库）
```

## 构建与运行

```bash
dotnet build src/Xiaoliu.App/Xiaoliu.App.csproj -c Release
dotnet publish src/Xiaoliu.App/Xiaoliu.App.csproj -c Release   # 单文件 exe
```

产物：`src/Xiaoliu.App/bin/Release/net8.0-windows/win-x64/publish/xiaoliu.exe`

开发时直接跑 `dotnet run --project src/Xiaoliu.App`。加 `--demo` 会把四种指点图形一次画出来，调样式用。

### 自检：`--demo typing`

```bash
xiaoliu.exe --demo typing   # 退出码 0 = 通过
```

验证 `insert_text` 的完整链路：起一个记事本把前台占住（模拟「小柳在后台」），再从它手里把靶子窗口抢回前台，键入 30 个中英混合字符并核对。日志在 `%LOCALAPPDATA%\xiaoliu\logs\`。

这条自检不是摆设：它抓到过一个会把字打进别的程序的 bug。`SendInput` 投递给的是**当前前台窗口**而不是句柄，抢前台失败还照打，字符就进了用户当时正在用的程序 —— 而 WPF 的 `IsActive` 这时还显示 `true`。现在 `insert_text` 抢不到前台就一个字符不打，直接回 `target_not_focusable`。


## 配置

`%LOCALAPPDATA%\xiaoliu\settings.json`，首次运行不自动创建（用默认值跑），改过设置后才会写出来。

```json
{
  "BaseUrl": "https://127.0.0.1/diandian/proxy",
  "AccessToken": "selfhost",
  "AllowUntrustedTls": true,
  "Model": "aliyun.qwen3.8-flash",
  "PushToTalkModifiers": "Control,Alt",
  "SelectRegionModifiers": "Control,Alt",
  "SelectRegionKey": "S",
  "TextRetentionDays": 90,
  "ImageRetentionDays": 7,
  "MaxTotalMegabytes": 200
}
```

`BaseUrl` 指向服务器端，**不需要改 hosts**。HTTP 走 `/diandian/proxy/*`，WebSocket 走 `/diandian/ws/*`，前缀不同是服务器端的选择，`WireProtocol.WebSocketUri` 负责换算。

`AllowUntrustedTls` 默认 `true`：服务器端那张自签 CA 的 SAN 是 botlearn 域名，主机名校验对 `127.0.0.1` 必然失败。接真云端前把它改回 `false`。

## 联调

服务器端在跑（`cd /d/gao/wbai/rea && bash server/start-real.sh`）之后：

```bash
# 1. 通不通
./src/Xiaoliu.Cli/bin/Debug/net8.0-windows/xiaoliu-cli.exe health

# 2. 一轮对话（不声明工具）
.../xiaoliu-cli.exe chat "用一句话介绍你自己"

# 3. 工具回环：模型会试着抓屏，被拒后改口
.../xiaoliu-cli.exe chat --tools "你看看我屏幕上写的是什么"

# 4. 带圈选图 + 指点
.../xiaoliu-cli.exe chat --tools --capture "我圈了这块屏幕，用指点告诉我最左边的图标在哪"

# 5. 语音两端
.../xiaoliu-cli.exe asr /path/to/16k-mono.wav
.../xiaoliu-cli.exe tts "小柳在这里" --out tts.wav
```

## 操作

| 动作 | 键 |
|---|---|
| 按住说话 | Ctrl + Alt（按住 ≥250ms） |
| 圈选一块区域 | Ctrl + Alt + S，拖框，松手即取图 |
| 清空指点图形与截图 | Esc |
| 打断正在念的回答 | 再按一次 Ctrl + Alt |
| 历史 / 退出 | 托盘菜单 |

## 已知限制

- 取图用 GDI `BitBlt`，受 DRM 保护的窗口和独占全屏会拿到黑图。要覆盖这些场景得换 WGC。
- 同一时间只能和一个进程抢热键：小柳与点点同时运行会互相干扰，二选一。
- `insert_text` 要求能把目标窗口切到前台。切不过去（比如目标被别的程序盖着、或用户在切过去的瞬间又点了别处）就**一个字符都不打**并回 `target_not_focusable` / `target_lost_focus`，请用户手动点一下目标窗口再说一次。宁可没填，也不能填错地方。
- `report_text_insertion` 的 `written` 结论被策略拒绝（没有画面可核对），填字只能到 `uncertain`。
- 语音中继的 partial 依赖服务器端滚动重解码，`doubao` 后端没有 partial。
- 多屏只按虚拟桌面并集写，没在真实多屏机器上验过。
