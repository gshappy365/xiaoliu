# v1 验收清单

日期：2026-10-29　产物：`src/Xiaoliu.App/bin/Release/net8.0-windows/win-x64/publish/xiaoliu.exe`

## 先看结论

| 组 | 内容 | 现在能验吗 |
|---|---|---|
| A | 托盘、热键、圈选、指点、字幕条、历史窗、两套自检 | **能**，需要你按几个键 |
| B | 对话、工具回环、抓屏拒绝、指点坐标、insert_text | **能**，服务器已在跑 |
| C | 按住说话 → 听到回答（整链） | **待定**：设备已配成 Virtual Mic (AudioRelay)，但该端点目前是数字静音，见文末 |

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

**已按你的选择把 `CaptureDeviceNumber` 设成 0（Virtual Mic / AudioRelay），但那个端点目前是纯数字静音。**

| 设备序号 | 名称 | 实测 rms / peak |
|---|---|---|
| -1（默认） | Microsoft 声音映射器 → Virtual Mic (AudioRelay) | 0.0000 / 0.0000 |
| 0 | Virtual Mic (AudioRelay) | 0.0000 / 0.0000（79649 个采样，一个非零值都没有） |
| 1 | 麦克风阵列 (UU远程虚拟音频设备) | 0.0010 / 0.0020（只有底噪） |
| 2 | CABLE Output (VB-Audio Virtual Cable) | 0.0000 / 0.0000 |

「一个非零值都没有」和「有底噪」是两回事：前者说明**根本没有音频在往这个端点里灌**，不是说话声音小。

AudioRelay 确实连着一台设备（`10.202.83.39` → 本机 `59100`，已建立连接），所以问题不在连接，在传输方向：现在大概率是手机当**播放端**（电脑 → 手机），而 Virtual Mic 要的是手机当**麦克风**（手机 → 电脑）。

### 要让它出声，按这个顺序查

1. 手机端 AudioRelay：连接模式选 **Microphone**（不是 Audio / Speaker）。
2. 手机系统设置里允许 AudioRelay 使用麦克风。
3. 电脑端 AudioRelay 设置：麦克风输出目标选 **Virtual Mic**，并确认麦克风传输是打开的。
4. 回来验一下：

```bash
xiaoliu.exe --demo mic 5 --device 0
```

看到 `rms` / `peak` 不再是 `0.0000` 就通了。

### 现在按热键会怎样

小柳已经在跑（设备 0）。按住 `Ctrl+Alt` 说话，日志会多一行：

```
[audio] 本轮采集 3.2s peak=0.0000 设备=Virtual Mic (Virtual Mic for Au
[audio] 采集电平接近静音，未送识别
```

字幕条会直接告诉你「没收到声音（设备：Virtual Mic…）」，而不是让你以为程序卡住了。设备修好之后这一行会变成正常的 `peak`，链路就通了。

> **改 `settings.json` 之前先退出小柳。** 小柳把配置整份读进内存，退出或改设置时会整份写回去 ——
> 运行期间手改文件会被覆盖。实测踩过：把 `CaptureDeviceNumber` 改成 0 之后，一个还活着的实例
> 退出时用内存里的 `-1` 把它冲了回去，于是按热键看到的是「设备=Microsoft 声音映射器」。

## 现在明确做不到的三件事

1. **语音整链**：缺物理麦克风（上面）。
2. **多屏**：这台机器虚拟桌面只有 1920×1080，单屏。取图和 overlay 都按虚拟桌面并集写，逻辑在，但没在真多屏上跑过。
3. **`insert_text` 往真实第三方应用填字**：自检靶子是我们自己的窗口 + 记事本当抢前台的对照组，记事本本身没被填过。B6/B7 就是在补这一条，需要你手动做。
