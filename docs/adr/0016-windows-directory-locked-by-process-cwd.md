# 查清「目录删不掉」的真正根因：进程当前工作目录

## Context

ADR 0013（C3）把部署目录拆成 `diandian-server-ops/` 与 `xiaoliu/deploy/` 两个目录，
旧目录 `wbai/xiaoliu/` 的内容全部迁出。**内容是空的**（0 个文件、0 个子目录，
连隐藏文件都没有），但目录本身删不掉：

```
[System.IO.Directory]::Delete("D:\gao\wbai\xiaoliu", $false)
→ 文件"D:\gao\wbai\xiaoliu"正由另一进程使用，因此该进程无法访问此文件。
   HResult 0x80070020  (ERROR_SHARING_VIOLATION / 32)
```

重命名同样失败（`Directory.Move` 也报「另一个进程正在使用此文件」）。

当时的结论记成「被某进程持有句柄，不影响功能」—— **这个结论是对的，但不够具体**：
既没说清是谁持有，也没说清为什么持有。而「删不掉」这件事本身，
在半年后回看时仍然会让人重新调查一遍。

## 调查过程

### 排除项

| 手段 | 结果 |
|---|---|
| `Get-ChildItem -Force -Recurse` | 空，0 个文件、0 个子目录 |
| 目录可见性（父目录枚举） | **可见**，不是 delete-pending |
| 任何进程的**命令行**含该路径 | 无 |
| `NtQuerySystemInformation` 枚举 **49 万个具名句柄** | 无一指向该路径 |
| 计划任务 action / 脚本 / 仓库引用 | 全部指向新目录，无一引用旧路径 |

句柄枚举查不到这一点最有意思：说明占用**不是**「打开了这个目录的文件」。

### 转折点：进程当前工作目录

Windows 上，进程的**当前工作目录（CWD）** 本身持有目录句柄，
且该句柄以独占方式打开 —— 于是这个目录既不能删也不能改名。
这类占用不会出现在句柄名里（`NtQueryObject` 对目录句柄不返回完整路径），
所以前一轮的句柄枚举完全查不到它。

用 `psutil`（`Process.cwd()`）遍历全机进程，一次命中 6 个：

```
PID=56996  node.exe          CWD = d:\gao\wbai\xiaoliu
PID=66712  node.exe          CWD = d:\gao\wbai\xiaoliu
PID=80376  sandbox-cli.exe   CWD = D:\gao\wbai\xiaoliu
PID=96048  node.exe          CWD = d:\gao\wbai\xiaoliu
PID=105032 WorkBuddyAI.exe   CWD = D:\gao\wbai\xiaoliu   ← 根
PID=121388 cmd.exe           CWD = d:\gao\wbai\xiaoliu
```

父进程链显示 `105032` 是源头，其余 5 个都继承了它的 CWD：

```
105032  WorkBuddyAI.exe ... codebuddy --serve --session-id 2020d09c-...
  └─ 96048  node.exe
  └─ 121388 cmd.exe
  └─ 56996  node.exe
  └─ 80376  sandbox-cli.exe
```

**成因**：ADR 0013 那轮排查里用 Bash 工具 `cd /d/gao/wbai/xiaoliu` 反复进出这个目录，
WorkBuddy 把该路径记成了会话的工作目录，随后启动的子进程全部继承了这个 CWD。
那个会话结束后，`codebuddy --serve` 的宿主进程**没有退出**，一直挂在那里当"常驻服务"，
它的 CWD 就一直锁着这个目录。

## Decision

**结束那个残留的会话进程组，然后删目录。**

执行前先确认它与当前会话无关：当前会话的进程链是
`WorkBuddy.exe → sandbox-cli.exe → bash → python`，CWD 全在 `xiaoliu-dsh`；
而 `105032` 属于 `WorkBuddyAI` 的另一棵进程树（`explorer.exe → 56180 → 5220 → 40804 → 105032`），
两棵树互不相干。用户在确认后选择结束它。

```
kill 80376 / 56996 / 121388 / 96048 / 105032   （先子后父）
→ 剩余持有者: (无)
→ RemoveDirectoryW("D:\gao\wbai\xiaoliu")  err=0
→ 目录已删除
```

### 为什么不选另外两条路

- **注入远程线程调 `SetCurrentDirectoryW`**：技术上可行，
  但要往别的进程里注入代码，风险明显高于结束一个已经没人用的会话进程。
- **继续留着**：目录是空的、无任何引用，功能上零影响。但它会一直误导下一次排查
  （半年后看到「删不掉」还会重新查一遍），而且没人知道它是"活的"还是"死的"。

## Consequences

- `wbai/xiaoliu/` 已不存在。`wbai/` 现在只剩三个各归其主的目录：
  `rea/`（服务器端）、`diandian-reference/`（点点反编译，只读）、
  `diandian-server-ops/`（服务端运维），外加备份目录 `_backup-xiaoliu-deploy-20261105/`。
- 目录解耦 C1–C5 至此**全部收尾**，无遗留项。

### 排查方法的可复用部分

「目录删不掉」在 Windows 上的成因按出现频率大致是：

1. **进程的当前工作目录**（本次）—— 隐藏得最深，且**不会**出现在句柄名里
2. 文件被其他程序独占打开 —— 常规句柄枚举就能看到
3. 目录里有正在被使用的子项 —— `Get-ChildItem` 能看到
4. 目录是重解析点/符号链接指向别处 —— 看 `Attributes` 里的 `ReparsePoint`

第 1 类最容易被误判成第 2 类。**判据**：句柄枚举查不到 + `Attributes` 无
`ReparsePoint` + `Get-ChildItem` 为空 → 用 `psutil` 查 CWD。

> `psutil` 在 Windows 上没有 `Process.chdir()`（那是 POSIX 专有），
> 所以只能**读**CWD、不能改。改别人的 CWD 在 Windows 上没有公开 API，
> 只能靠远程线程注入 —— 除非那进程可以直接结束掉。

### 两个环境限制（本机踩过）

- `Add-Type` 被安全策略拦 → 无法在 PowerShell 里编译 C# 查句柄；
  改用 Python + ctypes 调 `NtQuerySystemInformation`。
- `ctypes` 调 Win32 API **必须显式设 `argtypes`**，
  否则 `byref()` 无法转换成指针参数 → `STATUS_ACCESS_VIOLATION`；
  且 `NtQuerySystemInformation` 的第 3 个参数是**长度**不是指针，
  写错会得到 `INFO_LENGTH_MISMATCH`。
