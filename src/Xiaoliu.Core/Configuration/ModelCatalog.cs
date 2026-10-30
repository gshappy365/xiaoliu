namespace Xiaoliu.Core.Configuration;

/// <summary>
/// 一个可选的对话模型。<see cref="Id"/> 是发给服务端的模型标识，
/// <see cref="Display"/> 是给人看的名字。
/// </summary>
public sealed record ModelOption(string Id, string Display, string Vendor, bool Vision, string Note);

/// <summary>
/// 模型下拉的候选清单。
///
/// 数据来源是 <c>arkcli models list --format json --page-all</c>（火山方舟控制面），
/// 只保留 <c>task_types</c> 含 <c>text_generation</c> 的 —— 也就是能当对话模型用的那些。
/// 视频生成、图片生成、TTS、ASR、embedding、3D 全部剔除：它们出现在这里只会让人选错。
///
/// 清单是**某一时刻的快照**（2026-10-30，54 个模型里筛出 23 个）。Ark 上新/下线模型时
/// 这份表会过期，重新生成的办法：
///
/// <code>
/// arkcli models list --format json --page-all
/// </code>
///
/// 然后按同样的过滤条件（task_types 含 text_generation）重排一遍。
/// 不做成运行时调用：客户端是单文件 exe，不能假设用户机器上装了 arkcli、还登着账号。
/// </summary>
public static class ModelCatalog
{
    /// <summary>支持看图的（VLM）。小六的核心能力是看屏幕，这一组排在前面。</summary>
    public static IReadOnlyList<ModelOption> Vision { get; } =
    [
        new("doubao-seed-2-1-pro-260915", "Doubao-Seed-2.1-pro", "Bytedance", true, "更强的 Agent 执行与多模态理解"),
        new("doubao-seed-2-1-lite-260915", "Doubao-Seed-2.1-lite", "Bytedance", true, "三模态理解，支持 1M 超长上下文"),
        new("doubao-seed-2-1-turbo-260628", "Doubao-Seed-2.1-turbo", "Bytedance", true, "2.1 系列的速度档"),
        new("doubao-seed-2-0-pro-260215", "Doubao-Seed-2.0-pro", "Bytedance", true, "2.0 旗舰"),
        new("doubao-seed-2-0-lite-260428", "Doubao-Seed-2.0-lite", "Bytedance", true, "2.0 轻量"),
        new("doubao-seed-2-0-mini-260428", "Doubao-Seed-2.0-mini", "Bytedance", true, "2.0 最小档"),
        new("doubao-seed-2-0-code-preview-260215", "Doubao-Seed-2.0-Code", "Bytedance", true, "Coding 场景增强"),
        new("doubao-seed-character-260628", "Doubao-Seed-Character", "Bytedance", true, "角色扮演向"),
        new("doubao-seed-evolving-latest-version", "Doubao-Seed-Evolving", "Bytedance", true, "Agent 执行与 GUI 理解"),
        new("kimi-k3-1-pro-261024", "Kimi-K3.1-Pro", "月之暗面", true, "面向编程与知识工作的多模态旗舰"),
    ];

    /// <summary>只看文本的（LLM）。没有视觉，圈选/截图类提问会失效。</summary>
    public static IReadOnlyList<ModelOption> TextOnly { get; } =
    [
        new("deepseek-v4-1-flash-261017", "DeepSeek-V4.1-Flash", "DeepSeek", false, "混合推理，精度与速度兼顾"),
        new("deepseek-v4-1-flash-economy-261017", "DeepSeek-V4.1-Flash经济版", "DeepSeek", false, "效果对齐标准版，推理八折"),
        new("deepseek-v4-flash-ga-260731", "DeepSeek-V4-Flash正式版", "DeepSeek", false, "低延迟推理编码"),
        new("deepseek-v4-pro-ga-260813", "DeepSeek-V4-Pro正式版", "DeepSeek", false, "V4 旗舰正式版"),
        new("deepseek-v4-pro-260425", "DeepSeek-V4-pro", "DeepSeek", false, "V4 旗舰"),
        new("glm-5-3-flash-260828", "GLM-5.3-Flash", "智谱AI", false, "GLM-5 系列首个原生多模态（接口只走文本）"),
        new("glm-5-2-260617", "GLM-5.2", "智谱AI", false, "GLM-5 上一版"),
        new("glm-4-5-air-20250728", "GLM-4.5-Air", "智谱AI", false, "面向智能体场景的高效模型"),
        new("qwen3-32b-20250429", "Qwen3-32B", "Qwen", false, "Qwen3 中档"),
        new("qwen3-14b-20250429", "Qwen3-14B", "Qwen", false, "Qwen3 小档"),
        new("qwen3-8b-20250429", "Qwen3-8B", "Qwen", false, "Qwen3 更小档"),
        new("qwen3-0-6b-20250429", "Qwen3-0.6B", "Qwen", false, "Qwen3 最小档"),
        new("qwen2-5-72b-20240919", "Qwen2.5-72B", "Qwen", false, "上一代大杯"),
    ];

    /// <summary>全部候选，视觉的排前面。</summary>
    public static IEnumerable<ModelOption> All => Vision.Concat(TextOnly);

    /// <summary>按标识找，找不到返回 null（配置里可能是人手输的任意值）。</summary>
    public static ModelOption? Find(string id) =>
        All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}
