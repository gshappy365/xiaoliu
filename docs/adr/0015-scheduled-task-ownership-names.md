# 计划任务按真实归属改名：diandian-server / diandian-server-watchdog

## Context

ADR 0013 解开了物理目录上的耦合（C1–C3），ADR 0014 解开了文字里的耦合（C4）。
剩下的 **C5** 是最后一类：**名字在撒谎**。

盘点时的实际状态：

| 任务名 | action 实际指向 | 跑的是谁 | 名字准吗 |
|---|---|---|---|
| `xiaoliu-server` | `diandian-server-ops\launch_hidden.py` | **点点**服务端 | ❌ |
| `xiaoliu-server-watchdog` | `diandian-server-ops\watchdog_host.py` | **点点**看门狗 | ❌ |
| `xiaoliu-client` | `deploy\start-xiaoliu.bat` | 小六客户端 | ✅ |

两个服务端任务挂着「小六」的前缀，跑的却是点点的 FastAPI 服务。
这类不一致比缺文档更危险：看门狗是**自动**运行的东西，出问题时没人会想到
「任务名和实际跑的东西不是一回事」。

拖了这么久的真实原因是：**Windows 计划任务不能改名**，只能删了重建，
而重建意味着服务中断。

## Decision

### 改什么

```
xiaoliu-server           →  diandian-server
xiaoliu-server-watchdog  →  diandian-server-watchdog
xiaoliu-client           →  不改（名字本来就准）
```

`xiaoliu-client` 不改是有意的：它的名字与实际跑的东西一致，
改名只会制造无谓的变更。

### 怎么改（关键：不动 XML 内容，只换身份）

Windows 没有「重命名计划任务」的操作。可行路径有两条：

1. `Set-ScheduledTask -TaskName 新名` —— **行不通**。
   `Set-ScheduledTask` 没有 `-TaskName` 参数能改身份，改名本质上仍是删+建。
2. 导出 XML → 改 `<URI>` → `Register-ScheduledTask -Xml` → 注销旧任务。

选第二条，且**只改 `<URI>` 一个字段**，其余 XML 原样注册。
这样 `Settings`（`ExecutionTimeLimit` / `MultipleInstancesPolicy` /
`IdleSettings` / `RestartOnFailure`）、`Principal`（`LogonType=InteractiveToken`
与 SID）、`Triggers`（`PT2M` 重复 + `StartBoundary`）都从备份原样搬过来，
不存在「手工重建时漏配某个字段」的风险。

切换顺序（避免 443 双实例竞争）：

1. 导出三个任务的 XML 存档
2. 注册两个新名字任务（此时**不启动**，旧任务仍在跑，线上无感）
3. **改 `watchdog.ps1` 的 `$task`** —— 必须在停旧任务之前
4. 禁用两个看门狗任务（防止切换中途它来抢 443）
5. 停旧 server 任务 + 杀占 443 的进程树
6. 启动 `diandian-server`，轮询 healthz
7. 注销两个旧任务，重新启用新看门狗

### 顺手修掉：看门狗的静默失效

`watchdog.ps1` 里 `Start-ScheduledTask -ErrorAction SilentlyContinue`。
这次改名如果忘了同步改 `$task`，看门狗会**安静地**拉不起来 ——
服务掉线 → 看门狗"重启" → 实际什么都没发生 → 每 2 分钟重复一次，
日志里只有「重启后仍不健康」，看不出根因是任务名对不上。

改成：

- 启动前先精确查找任务，**找不到就写日志并 `exit 1`**；
- `Start-ScheduledTask` 去掉 `SilentlyContinue`，改用 `try/catch` 记录异常；
- 查找时按**精确任务名**过滤 —— `Get-ScheduledTask -TaskName` **支持通配符**，
  `'diandian-server'` 会同时命中 `'diandian-server-watchdog'`，
  取到数组后 `.State` 是数组而非标量，逻辑会走错分支
  （这个坑在 ADR 0013 处理 C3 时就踩过一次）。

## Consequences

### 好处

- 任务名与实际归属一致。`Get-ScheduledTask` 一眼能看出谁是谁。
- 看门狗改名写错时会**大声报错**（退出码 1 + 明确日志），不再静默失效。
- 服务端与客户端的运维入口彻底分开：点点看 `diandian-server-ops/`，
  小六看 `deploy/`。

### 代价

- **服务端重启一次**，10 秒 healthz 恢复（实测 t+10s = 200）。
- 旧的 `xiaoliu-server` / `xiaoliu-server-watchdog` 任务已注销，
  外部若有人引用旧名字需自行调整（仓库内引用已全部改完）。

### 验证

- 新旧任务 XML 逐行 diff：除 `<RegistrationInfo>` 外**差异行数 = 0**；
  最终态与改名前的备份再次比对，仍为 **0**
- 端到端对话：SSE 正常返回，`stop_reason: end_turn`
- **看门狗自愈路径**（核心验证）：杀掉占 443 的进程 → 手动跑看门狗 →
  日志出现「准备重启任务 diandian-server」→ 6 秒后「重启成功，healthz=200」
- 负向验证：把任务名改成不存在的 `diandian-server-TYPO` → 看门狗
  **退出码 1** + 日志「找不到计划任务 diandian-server，看门狗无法拉起服务
  （任务可能被改名过，需人工处理）」→ 恢复后 healthz=200
- 误伤检查：点点语音的 `qwen3-asr` 进程（2 个）全程未被误杀
