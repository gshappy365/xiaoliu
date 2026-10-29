using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xiaoliu.Core.Configuration;

/// <summary>
/// 客户端全部可配置项。持久化在 <c>%LOCALAPPDATA%\xiaoliu\settings.json</c>。
/// 默认值就是交接文档里定下的那一套，改默认值等于改产品行为，先看 docs/adr。
/// </summary>
public sealed class AppSettings
{
    /// <summary>服务器端根地址（含 <c>/diandian/proxy</c> 前缀，与点点同形）。</summary>
    public string BaseUrl { get; set; } = "https://127.0.0.1/diandian/proxy";

    /// <summary>服务器只检查 authorization 头存在，值不重要（见 rea/server/app/main.py）。</summary>
    public string AccessToken { get; set; } = "selfhost";

    /// <summary>服务器端自签 CA 未必覆盖 127.0.0.1，v1 允许跳过校验。</summary>
    public bool AllowUntrustedTls { get; set; } = true;

    public string Model { get; set; } = "aliyun.qwen3.8-flash";

    public int MaxTokensWithTools { get; set; } = 8192;

    public int MaxTokensWithoutTools { get; set; } = 1024;

    public string ReasoningEffort { get; set; } = "medium";

    /// <summary>按住说话的修饰键组合，照抄点点：Ctrl+Alt。</summary>
    public string PushToTalkModifiers { get; set; } = "Control,Alt";

    /// <summary>圈选热键（按下后进入框选，拖拽结束即取图）。</summary>
    public string SelectRegionModifiers { get; set; } = "Control,Alt";

    public string SelectRegionKey { get; set; } = "S";

    /// <summary>按住多久算「按住说话」，点点是 250ms。</summary>
    public int PushToTalkHoldMilliseconds { get; set; } = 250;

    public int TextRetentionDays { get; set; } = 90;

    public int ImageRetentionDays { get; set; } = 7;

    public int MaxTotalMegabytes { get; set; } = 200;

    public bool ShowCursor { get; set; } = true;

    /// <summary>字幕条是否显示 ASR 中间结果。</summary>
    public bool ShowAsrPartial { get; set; } = true;

    public bool LaunchAtLogin { get; set; } = true;

    public string TtsVoiceId { get; set; } = "volcengine.xiaohe";

    /// <summary>本轮对话最多几次模型请求（含工具回环），点点是 6。</summary>
    public int MaxModelRequestsPerTurn { get; set; } = 6;

    public int MaxToolCallsPerTurn { get; set; } = 20;

    [JsonIgnore]
    public Uri BaseUri => new(BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "xiaoliu");

    public static string DefaultPath => Path.Combine(DefaultDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = Xiaoliu.Core.Protocol.JsonDefaults.Pretty;

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
                if (loaded is not null)
                {
                    return loaded.Normalize();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 配置坏了不能拖死启动：落回默认值，下一次保存会覆盖。
        }

        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public AppSettings Normalize()
    {
        if (TextRetentionDays <= 0) TextRetentionDays = 90;
        if (ImageRetentionDays <= 0) ImageRetentionDays = 7;
        if (MaxTotalMegabytes < 16) MaxTotalMegabytes = 16;
        if (MaxModelRequestsPerTurn is < 1 or > 12) MaxModelRequestsPerTurn = 6;
        if (MaxToolCallsPerTurn is < 1 or > 60) MaxToolCallsPerTurn = 20;
        if (string.IsNullOrWhiteSpace(BaseUrl)) BaseUrl = "https://127.0.0.1/diandian/proxy";
        return this;
    }

    public string DatabasePath => Path.Combine(DefaultDirectory, "history.db");

    public string ScreenshotDirectory => Path.Combine(DefaultDirectory, "screenshots");
}
