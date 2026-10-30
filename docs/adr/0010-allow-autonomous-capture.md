# 放开自主抓屏：默认允许，设置里能关

## Context

`docs/adr/0002-capture-policy.md` 定的立场是「看不到屏幕，除非用户主动给我看」：
`capture_screen` 一律回 `capture_not_user_initiated`，画面只能靠用户圈选。

实测下来这条立场的代价比预想的大。用户问「你现在能看到我屏幕上是什么吗？」，
模型只能回「看不到，你圈选一下」，而它**本来有能力自己抓一张**——客户端有
`ScreenCapture`，工具有 `capture_screen`，缺的只是策略放行。用户的原话：
「这个改成放行能让它抓图」。

ADR 0002 里那条「模型会不听话偷偷抓屏，用户永远不会知道」的担心，现在也不成立了：
抓屏是**客户端执行的工具调用**，抓没抓、抓了几次都写在日志里（`[tool] capture_screen`），
而且抓到的图会跟着 `capture_call_id` 回到模型手上 —— 不是暗中行为。

## Decision

`AppSettings.AllowAutonomousCapture`，**默认 true**：

- 打开时：`ToolDispatcher` 真的抓一张虚拟屏幕全图，`tool_result` 的 content 用块数组
  返回 `[text 回执, image 原图]`，图登记进 `VisualEvidence`（id 前缀 `capture-`），
  后续指点工具可以直接引用它。
- 关掉时：完全回到 ADR 0002 的行为 —— 原因码、措辞、二次拒绝后终止工具恢复都不变。
- 一轮最多抓两次：屏幕变了再抓一次是合理的，第三次就是在打转（每次都是一整个上游往返）。
- 系统提示词的【你能看到什么】整段跟着开关换（`SystemPrompt.For(bool)`）：
  打开时说「涉及当前画面先抓屏再回答」，关掉时说「抓屏会被拒绝，不要重试」。
  **提示词必须和代码同源**：只改代码不改提示词，模型会照旧说「我抓不了」。
- 设置页「通用 → 画面」加开关，文案写清代价（下一轮生效）。
- 服务器端仍然一行不改：`_NEED_TOOL_HINT` 与 `_ACTION_NUDGE` 本来就写着
  「需要画面就先 capture_screen」，现在这句话终于和客户端行为一致了。

## Considered Options

- **不做开关，直接永久放行**：少一个配置项，但「不想让它自己截屏」是合理诉求
  （共享屏幕、录屏、开会时），一刀切会让这类用户没法用。
- **默认关，用户自己去开**：最保守，但用户的诉求就是「让它能看」，默认关等于没改。
- **抓屏前弹一次确认**：安全，但每次都要打断用户，语音助手的价值就是不用动手。
- **只把图存盘不发给模型**：等于没抓。

## Consequences

- 隐私面变大：模型可以在用户没圈选的情况下拿到整屏。缓解手段是开关 + 日志 +
  截图留存天数（ADR 0004 的 `ImageRetentionDays`，默认 7 天）。
- 抓到的图会进 `VisualEvidence`，受 ADR 0003 的 hash 判定约束：屏幕一变就判
  `stale_capture`，模型不能拿旧图继续指点。
- 纯文本对话模型仍然能「看」：抓到的图在 `tool_result` 里，服务端的
  `apply_vision_policy` 会把它转成 VL 描述 / OCR 文字（`_transform_blocks` 会递归进
  `tool_result` 的 content 数组）。视觉组模型则走 `vision_capable` 直连（ADR 0009）。
- 实测（doubao-seed-2-1-pro，`--tools`，问「你现在能看到我屏幕上是什么吗」）：
  `工具 1 次 / 请求 2 次 / stop=end_turn`，两轮共 8.5 秒，模型准确说出了左侧空间名、
  当前任务标题、中间那段开发进度、底部输入框和右下角时间 —— 说明原图真的到了模型眼前。
- `tool_result.content` 从此有两种形状（字符串 / 块数组），所有读它的地方都要判类型
  （`IsRetryable`、`FallbackMessage`、CLI 打印）。这类地方漏一个就是
  `The node must be of type 'JsonValue'` 的运行时异常。
