# 栈与打包：.NET 8 + WPF，单文件自包含

## Context

要点点那条 WinUI 3 + 原生解码器（`pipeline_brain_ffi.dll`）的路，就得连它的 XAML 控件库、WinRT 互操作和原生 SSE 解码器一起复刻。而 xiaoliu 真正需要复刻的只有两件事：Anthropic Messages 的 SSE 帧，和两块点击穿透的全屏窗口。

## Decision

C# / .NET 8 + WPF，`win-x64` 自包含单文件 exe，常驻托盘，命名互斥锁防多开，注册表 Run 键自启。SSE 解码器自己写（`SseReader`），不复用点点的原生库。

## Considered Options

- **WinUI 3（照抄点点）**：控件观感一致，但要拖 `Microsoft.WindowsAppRuntime` 的一整套部署，单文件打包会变成「单文件 + 一堆框架依赖」，而且 WinUI 的窗口层级在点击穿透上比 WPF 麻烦。
- **Electron / Tauri**：跨平台无收益（这个产品只跑 Windows），内存与启动成本反而更高。

## Consequences

- 单文件 exe 约 60–90MB（自包含运行时），换的是「拷到另一台机器双击就能跑」。
- 自己写 SSE 解码意味着要自己扛协议边界（`\n\n` 分帧、`[DONE]` 结束、`input_json_delta` 拼接）。已在 `tests/SseReaderTests.cs` 里锁住。
- WPF 的 `AllowsTransparency` 在超大窗口上会走软件渲染路径；实测 1920×1080 全屏透明窗口可接受，多屏 4K 需要复测。
