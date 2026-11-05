# 目录解耦：把点点的闭源源码与服务器启动器搬出 xiaoliu 仓库

## Context

ADR 0012 解开了**运行时**耦合：三套工程各走各的链路，互不绑架。
但代码**目录**层面还有两处反向依赖 —— 都是「物理上住在别人家里」：

| # | 耦合 | 具体表现 |
|---|---|---|
| C1 | 点点的闭源源码住在 xiaoliu 仓库里 | `reference/App/Diandian.Windows.App/`（145 文件）+ `reference/Core/`（620 文件），共 766 文件 4.6MB |
| C2 | xiaoliu 仓库的脚本反向依赖服务器端的绝对路径 | `scripts/run-server.sh` 硬编码 `SERVER_DIR=D:/gao/wbai/rea/server`、`ROOT=D:/gao/wbai/rea`，并 `eval` 服务器端 `start-real.sh` 的 export 段 |

C1 的问题不只是难看。点点是**闭源商业产品**，它的反编译源码放在一个公开的
GitHub 仓库里，靠 `.gitignore` 里的 `reference/` 一行挡着 —— 只要有人改了 ignore 规则，
或者用 `git add -f`，760 个闭源文件就进了 GitHub。

C2 的问题更隐蔽：为了开一个明文端口，需要复用服务器端的整套环境变量，
于是只能 `eval` 别人的 export 段；这逼出了硬编码绝对路径。
本仓库的 `scripts/` 目录里**只有这一个文件** —— 一个客户端仓库里唯一的脚本，
是启动别人的服务器的脚本。

## Decision

### 1. 点点的反编译源码搬出仓库，独立成目录

```
D:\gao\wbai\diandian-reference\     766 文件 / 4.6MB，点点客户端 App + Core 全量
```

位置选在 `wbai/` 下与服务器端项目 `rea/` 并列：同属点点生态，但不寄生在任何客户端项目里。
仓库里只留一个指针 README（`reference/README.md`），写清新位置、为什么移走、
以及与 `rea/decompiled/` 的区别。

`.gitignore` 从整目录忽略改成只放行那一个文件：

```
reference/*
!reference/README.md
```

这样即使 ignore 规则被改、或者有人 `git add -f`，能进库的从 760 个闭源文件
降级为 1 个说明文档。

### 2. 启动器合并成一个，服务器端自己支持明文模式

原来的 `scripts/run-server.sh` 之所以要 `eval` + 硬编码路径，根因是
**服务器端启动器只能起 TLS**：它强制要求自签证书存在，且不给 uvicorn 传明文参数。

改法不是再加一个脚本，而是让服务器端那个启动器多一种拓扑：

| 拓扑 | 命令 | 用途 |
|---|---|---|
| 443 + TLS | `bash start-real.sh` | 生产，点点直连 |
| 8899 + 明文 | `TLS=0 PORT=8899 bash start-real.sh` | 开发通道，不占 443、不依赖证书 |

于是开发通道的命令变成服务器端项目里的一条命令，
**本仓库不需要携带任何服务器启动器**，`scripts/` 目录随之消失。

服务器端的改动（`run_live.py` 加 `--plaintext`、`start-real.sh` 加 `TLS` 开关）
是纯增量的：默认行为与改之前逐字节等价，443 生产实例不受影响。

### 3. 指针写进三处

搬走文件必须留下能找到它的路，否则下一个人还会重新翻一遍：

- 仓库 `reference/README.md` —— 新位置、内容清单、为什么移走、与 `rea/decompiled/` 的区别
- 仓库 `README.md` 结构说明 + `src/Xiaoliu.Core/Tools/ToolCatalog.cs` 的注释
- 服务器端 `CONTEXT.md` 新增「资料位置」一节

## Considered Options

**点点源码放 `D:\gao\wbai\rea\decompiled-full\`？** 不选。
那会把客户端的闭源源码挂到**服务器端项目**下 —— 只是把耦合从客户端挪到服务器，
而且 `rea/` 的身份是「替 botlearn 云的服务器端」，装客户端源码名不副实。

**点点源码原地保留，只加个 README 声明？** 不选。
物理混住没解决，而 `reference/` 里那 766 个文件会继续在每次 `git status`、
每次打包、每次同步时出现在视野里。

**保留 `scripts/run-server.sh`，只把硬编码路径改成环境变量必填？** 不选。
那只解决了「路径硬编码」这个症状，没解决「客户端仓库里养着服务器启动器」这个病根，
而且 `eval` 别人 export 段的做法仍然在。

**把明文模式做成第二个启动器（放在服务器端项目里）？** 不选。
两个启动器要同步维护同一套环境变量，迟早漂移。合并成一个参数化启动器更简单。

## Consequences

**好的一面**

- xiaoliu 仓库里**不再有任何闭源代码**，入库风险从「760 个文件」降到「1 个文档」
- xiaoliu 仓库里**不再有服务器端代码** —— 它就是个客户端
- 服务器端启动器从「只会起 TLS」变成「两种拓扑一个入口」，少了一个要同步的脚本
- 开发通道的命令变短了：`TLS=0 PORT=8899 bash start-real.sh`，
  不用再记住 `SERVER_DIR` / `ROOT` 两个环境变量

**代价与注意事项**

- 服务器端项目**不是 git 仓库**，改动没有版本控制兜底
  → 改前必须备份（本次留了 `run_live.py.bak-20261105-plaintext`、
  `start-real.sh.bak-20261105-plaintext`）
- 明文通道**没有 TLS**，只适合本机开发。客户端侧用
  `http://127.0.0.1:8899/diandian/proxy`，语音地址会由 `WebSocketUri`
  自动从 `http` 换成 `ws`
- 服务器端 `rea/decompiled/` 与 `rea/decompiled-089/`（点点的**裁剪版**反编译）
  仍留在原处 —— 它们是服务器端项目的产物，`rea/reports/` 里大量引用其路径，
  搬迁要连带改报告。本 ADR 只处理「客户端仓库里的完整版」

**尚未处理的目录耦合**（盘点后留给后续）：

| # | 耦合 | 位置 |
|---|---|---|
| C3 | 部署目录把服务器与客户端混装 | `D:\gao\wbai\xiaoliu\`：watchdog / launch_hidden / start-server 都是服务器端的，却和 `start-xiaoliu.bat` 同住 |
| C4 | 文档用绝对路径交叉引用 | `CONTEXT.md`、`docs/adr/0008` 等多处指向 `D:\gao\wbai\rea\...` |
| C5 | 计划任务名误导 | `xiaoliu-server` / `xiaoliu-server-watchdog` 跑的其实是点点的服务器，却挂着「小六」前缀 |

## 验证

- 搬迁：同盘 `mv`（原子改名，无半复制状态），766 文件 / 44 目录**一个不少**；
  JSON 可解析（19 个工具）；两个 0 字节文件是**反编译本身就失败**留下的空洞，
  时间戳与其它文件一致，非搬运损坏
- 搬前必查三件事：csproj 是否 glob 该目录（**否**）、仓库内几处引用（**2 处**）、
  其他副本是否有重复副本（**无**）
- 明文通道端到端：小六客户端 `端点=http://127.0.0.1:8899/diandian/proxy`、
  语音地址自动换成 `ws://127.0.0.1:8899/diandian/ws/asr/volcengine`，
  health OK（`stop_reason=max_tokens`），完整对话跑通并成功回环 `capture_screen` 工具
- 生产实例未受影响：起明文实例时 `reap: 443 已被 pid 监听，不做清理`，
  测试后 443 healthz 仍 200
- 仓库：Release 构建 0 警告 0 错误；113 个单测全绿；
  远程仓库扫描 `Diandian` 命名的文件数为 **0**
