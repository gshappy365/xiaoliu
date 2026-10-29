# v1 验收清单

日期：2026-10-29　产物：`src/Xiaoliu.App/bin/Release/net8.0-windows/win-x64/publish/xiaoliu.exe`

## 先看结论

| 组 | 内容 | 现在能验吗 |
|---|---|---|
| A | 托盘、热键、圈选、指点、字幕条、历史窗、两套自检 | **能**，需要你按几个键 |
| B | 对话、工具回环、抓屏拒绝、指点坐标、insert_text | **能**，服务器已在跑 |
| C | 按住说话 → 听到回答（整链） | **不能**：这台机器没有物理麦克风，见文末 |

## 前提

- 服务端在 443 上跑着：`cd /d/gao/wbai/rea && bash server/start-real.sh`
- **不要同时开点点**：两者抢同一套热键，必有一个失灵
- 日志：`%LOCALAPPDATA%\xiaoliu\logs\xiaoliu-YYYYMMDD.log`，出问题先看这里

---

## A 组：外壳（约 3 分钟）

| # | 步骤 | 预期 | 失败看什么 |
|---|---|---|---|
| A1 | 双击 `xiaoliu.exe` | 托盘出现图标；右下角字幕条提示「小柳在托盘里…」 | 进程是否起了；日志有没有 `启动 版本` |
| A2 | 再双击一次 `xiaoliu.exe` | 弹「小柳已经在运行了。」，第二个实例退出 | 互斥锁 `Global\xiaoliu.exe.single-instance` |
| A3 | 按 `Ctrl+Alt+S`，拖一个框 | 屏幕变暗、拖出选框，松手后选框消失 | 热键钩子是否装上了（日志 `hotkey`） |
| A4 | 托盘菜单 → 历史记录 | 弹出表格，列出刚才那轮对话 | 库在 `%LOCALAPPDATA%\xiaoliu\history.db` |
| A5 | 托盘菜单 → 开机自启，勾上再取消 | 每次都有字幕条提示；注册表 `HKCU\...\Run` 只在勾选时存在 `xiaoliu` | **默认是关的**，不勾就不该写 |
| A6 | 命令行跑 `xiaoliu.exe --demo typing` | 弹出小窗口，自动打出一行中英混合文字后关闭，**退出码 0** | 日志 `selftest` 四行阶段记录 |
| A7 | 命令行跑 `xiaoliu.exe --demo mic 3` | 打印录音设备清单和电平 | 见文末「麦克风」 |

## B 组：协议与工具（约 5 分钟）

用 CLI 验最省事，输出直接可读：

```bash
cd /d/gao/xiaoliu/mattpocock
CLI=src/Xiaoliu.Cli/bin/Debug/net8.0-windows/xiaoliu-cli.exe
```

| # | 命令 | 预期 |
|---|---|---|
| B1 | `$CLI health` | `/healthz` 200，`/capabilities` 909 字节 |
| B2 | `$CLI chat "用一句话介绍你自己"` | 有回答，`stop=end_turn`，1 次请求 |
| B3 | `$CLI chat --tools "你看看我屏幕上写的是什么"` | 模型调 `capture_screen` → 被拒 `capture_not_user_initiated` → 改口请你圈选。**不应该反复重试** |
| B4 | `$CLI chat --tools --capture "我圈了这块屏幕，用指点告诉我最左边的图标在哪"` | `point_at_screen` accepted，屏幕上有图形；回执里 `verification=presentation_only` |
| B5 | `$CLI chat --tools "调用 list_target_applications 看有哪些窗口"` | 列出当前可见窗口 |
| B6 | 在 GUI 里：圈选记事本 → 说「把『测试一下』填进记事本」 | 字进记事本，**不按回车**；回执 `verification=unverified` |
| B7 | B6 之前先把记事本最小化或让别的窗口盖住它 | 要么成功切过去并填字，要么回 `target_not_focusable` 且**一个字都没打**；绝不能打到别的程序里 |

B7 是这次修的重点，值得专门试一次。

## C 组：语音整链

按住 `Ctrl+Alt` 说话 ≥250ms 松手 → 字幕条出中间结果 → 听到回答 → 再按一次可打断。

**这台机器现在过不了**，原因见下。

---

## 麦克风：现在的实际状态

系统里只有三个录音端点，全是虚拟设备，没有一个接着物理麦克风：

| 设备序号 | 名称 | 实测 rms / peak |
|---|---|---|
| -1（默认） | Microsoft 声音映射器 → Virtual Mic (AudioRelay) | 0.0000 / 0.0000 |
| 0 | Virtual Mic (AudioRelay) | 0.0000 / 0.0000 |
| 1 | 麦克风阵列 (UU远程虚拟音频设备) | 0.0010 / 0.0020（只有底噪） |
| 2 | CABLE Output (VB-Audio Virtual Cable) | 0.0000 / 0.0000 |

`Realtek High Definition Audio` 在设备管理器里是正常的，但没有暴露录音端点 —— 也就是没有东西插在麦克风口上。10-28 那次能识别出人声（peak 0.021），应该是当时 UU 远程会话把对方的声音灌进了设备 1。

**要做 C 组，先解决硬件**：插一个麦克风（或开 UU 远程会话，让对方说话）。接上之后：

```bash
xiaoliu.exe --demo mic 3              # 看默认设备有没有声音
xiaoliu.exe --demo mic 3 --device 1   # 指定设备试
```

确认哪个序号有声音，就把 `%LOCALAPPDATA%\xiaoliu\settings.json` 里的
`"CaptureDeviceNumber"` 改成那个序号（-1 = 跟随系统默认）。改完重开小柳再按热键。

## 现在明确做不到的三件事

1. **语音整链**：缺物理麦克风（上面）。
2. **多屏**：这台机器虚拟桌面只有 1920×1080，单屏。取图和 overlay 都按虚拟桌面并集写，逻辑在，但没在真多屏上跑过。
3. **`insert_text` 往真实第三方应用填字**：自检靶子是我们自己的窗口 + 记事本当抢前台的对照组，记事本本身没被填过。B6/B7 就是在补这一条，需要你手动做。
