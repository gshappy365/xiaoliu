# reference/ —— 指针目录

**这个目录现在只剩这一个 README。** 点点的反编译源码已于 2026-11-05 移出本仓库。

## 源码现在在哪

```
D:\gao\wbai\diandian-reference\
├── App/                      点点客户端 App 层反编译（17 个命名空间）
│   └── Diandian.Windows.App/ 及 Audio / Authentication / Capture / Companion /
│       Course / DesignSystem / Input / Interop / Overlay / Spike / Surface / Text / Update
├── Core/                     点点客户端 Core 层反编译（Diandian.Windows.Core.*）
├── CompanionPrompts.json     点点的人设/提示词
└── tools-from-diandian.json  19 个工具的 name / description / input_schema
```

共 766 个文件（760 个 `.cs` + 2 个 `.csproj` + 2 个 `.json` + 2 个 `.txt`），约 4.6 MB。

## 为什么要移走

点点的源码是**闭源商业产品**的反编译产物。放在小六的仓库里会造成两个问题：

1. **代码目录耦合** —— 小六是独立项目，不该在物理上寄生着另一个产品的源码。
2. **误入版本库的风险** —— 之前靠 `.gitignore` 里的 `reference/` 挡着；
   一旦有人改了 ignore 规则，或者用 `git add -f`，760 个闭源文件就会进 GitHub。

移出之后：仓库里只剩这个指针，`.gitignore` 改成 `reference/*` + `!reference/README.md`，
**只放行这一个 README**，从机制上杜绝误提交。

## 与 `rea/decompiled/` 的区别

别搞混，这是两套不同用途的东西：

| 位置 | 内容 | 用途 |
|---|---|---|
| `D:\gao\wbai\diandian-reference\`（本目录原内容） | 点点客户端**完整**反编译（760 个 .cs） | 看点点怎么实现的 |
| `D:\gao\wbai\rea\decompiled\` | 按主题裁剪（Capture / Companion / Core-Agent / Core-Text / Networking / Orchestrator / Voice，84 个 .cs） | 写服务器端时查协议细节 |
| `D:\gao\wbai\rea\decompiled-089\` | 0.8.9 版差量（WorkBuddy / Metrics / Growth / Auth / Capture / System，37 个 .cs） | 查 0.8.9 相对 0.8.2 的变化 |

`rea/` 下的两个目录属于**服务器端项目**的产物，报告里大量引用，暂不搬迁。
本目录是**客户端侧的完整参考**，两者内容不重复。

## 用法

只读参考，不要在这个目录里改代码 —— 它随时可能被重新反编译覆盖。
要记笔记请写到 `D:\gao\wbai\rea\reports\` 或小六的 `docs/`。

## 相关

- 小六的工具声明照抄点点：`src/Xiaoliu.Core/Tools/ToolCatalog.cs` 的注释指向
  `D:\gao\wbai\diandian-reference\tools-from-diandian.json`
- 点点/服务器端的术语表在 `D:\gao\wbai\rea\CONTEXT.md`
- 小六的术语表在仓库根目录 `CONTEXT.md`
