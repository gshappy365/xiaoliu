# 工作区布局：外部项目的路径记法

本仓库（xiaoliu 客户端）在文档和代码注释里会引用**仓库之外**的项目：
点点服务器端、点点反编译源码、服务器运维脚本。

这些引用一律用**逻辑路径**写，不写死盘符绝对路径。
本文是**唯一**允许出现绝对路径的地方 —— 它是那张对照表。

## 为什么要这么写

写死 `D:\gao\wbai\rea\...` 有三个具体问题：

1. **换机器就失效。** 仓库推到 GitHub 后，别人（和未来的自己）克隆下来，
   按文档去找 `D:\gao\wbai\rea` 只会扑空 —— 路径对别人毫无意义。
2. **它把「这台机器的目录布局」写成了项目的事实。**
   ADR 0013 花了很大力气把点点源码搬出本仓库，就是为了不寄生在别人的目录里；
   结果文档里满篇指着那个绝对路径，等于把耦合从文件系统搬回了文字。
3. **漏一处就断链。** 绝对路径散落在 6 个文件里，改一次要动 6 处，
   漏掉任何一处，那条引用就静默失效 —— 没有编译错误，没有测试失败，只有「文档说不清了」。

## 逻辑路径对照表

| 逻辑路径 | 实际位置 | 是什么 |
|---|---|---|
| `rea/` | `D:\gao\wbai\rea\` | 点点服务器端项目（FastAPI）。含 `server/`、`docs/adr/`、`reports/`、`CONTEXT.md`。**不是 git 仓库** |
| `rea/decompiled/` | `D:\gao\wbai\rea\decompiled\` | 点点反编译源码（按主题裁剪，84 个 .cs）。服务器端项目的产物 |
| `rea/decompiled-089/` | `D:\gao\wbai\rea\decompiled-089\` | 0.8.9 版差量（37 个 .cs） |
| `diandian-reference/` | `D:\gao\wbai\diandian-reference\` | 点点客户端**完整**反编译（766 文件）。2026-11-05 从本仓库移出，只读 |
| `diandian-server-ops/` | `D:\gao\wbai\diandian-server-ops\` | 服务器端运维脚本与日志（计划任务拉起用） |
| `xiaoliu-deploy/` | `D:\gao\xiaoliu\deploy\` | 小六客户端启动脚本 |
| `xiaoliu-repo/` | `D:\gao\xiaoliu\mattpocock\` | 本仓库的真实工作副本 |
| `xiaoliu-repo/potepo/` | `D:\gao\xiaoliu\potepo\` | 旧支线副本（已并入主线，可弃） |
| `xiaoliu-repo/xiaoliu-dsh/` | `D:\gao\xiaoliu\xiaoliu-dsh\` | 旧 PR 审查用克隆（一次性工具，可弃） |
| `diandian-client/` | `%LOCALAPPDATA%\Programs\Diandian\` | 点点客户端安装目录（闭源商业产品） |

> 上面这张表是**本机快照**。换机器时只改这一张表，别处不用动。

## 引用规则

**规则**：文档、代码注释、脚本里一律写逻辑路径（`rea/server/app/main.py`），
不写 `D:\gao\wbai\rea\server\app\main.py`。

逻辑路径的末尾若指到外部项目内的具体文件，用 `逻辑路径/项目内相对路径`：

```
rea/server/app/main.py              ✓
rea/docs/adr/0005-scope-14-tools.md ✓
diandian-reference/tools-from-diandian.json ✓
D:\gao\wbai\rea\server\app\main.py ✗
```

指向本仓库自己的文件时，用**仓库内相对路径**（照旧）：

```
src/Xiaoliu.Core/Protocol/SseReader.cs ✓
docs/adr/0012-dual-connection-mode.md ✓
```

### 例外：终端命令

命令示例里的路径必须是**能直接粘贴执行**的，不能是逻辑路径：

```bash
cd /d/gao/wbai/rea && bash server/start-real.sh     ✓ 能粘贴
cd rea && bash server/start-real.sh                 ✗ 粘不了
```

所以命令示例写在本文或运维文档里（`diandian-server-ops/README.md`），
其余文档引用它，不重复粘贴命令。

## 谁在守这条规则

`tests/Xiaoliu.Core.Tests/WorkspacePathTests.cs` 扫描仓库里所有入库的
`.md` / `.cs` / `.sh` / `.bat` / `.ps1` / `.py` / `.json` 文件，
发现盘符绝对路径就失败 —— **本文除外**（对照表本身就得写绝对路径）。

新增文件若必须写绝对路径（例如贴一段报错原文），加进那个测试的白名单，
并在行尾注明原因。
