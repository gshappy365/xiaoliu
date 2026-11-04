using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xiaoliu.Core.Configuration;

/// <summary>
/// 客户端全部可配置项。持久化在 <c>%LOCALAPPDATA%\xiaoliu\settings.json</c>。
/// 默认值就是交接文档里定下的那一套，改默认值等于改产品行为，先看 docs/adr。
///
/// 云直连后（ADR 0011）客户端自带云凭证：三个密钥字段在文件里存的是
/// <see cref="SecretProtector"/> 的受保护串（<c>dpapi:1:…</c>），不是明文；
/// 运行时用对应的 <c>*Plain</c> 属性取明文。
/// </summary>
public sealed class AppSettings
{
    /// <summary>模型下拉里代表「自定义 OpenAI 兼容端点」的值，存在 <see cref="Model"/> 里。</summary>
    public const string CustomModelKey = "custom";

    /// <summary>火山方舟的 API key（受保护存储）。对话与看图都走它。</summary>
    public string ArkApiKey { get; set; } = string.Empty;

    /// <summary>火山语音技术的 App ID（受保护存储）。ASR/TTS 直连用。</summary>
    public string VoiceAppId { get; set; } = string.Empty;

    /// <summary>火山语音技术的 Access Token（受保护存储）。</summary>
    public string VoiceAccessToken { get; set; } = string.Empty;

    /// <summary>自定义 OpenAI 兼容端点的基地址（如 <c>https://api.deepseek.com/v1</c>），明文存。</summary>
    public string CustomBaseUrl { get; set; } = string.Empty;

    /// <summary>自定义端点的 API key（受保护存储）。</summary>
    public string CustomApiKey { get; set; } = string.Empty;

    /// <summary>自定义端点上的模型名，自由填写（如 <c>deepseek-chat</c>）。</summary>
    public string CustomModelName { get; set; } = string.Empty;

    /// <summary>
    /// 对话模型。默认取方舟的视觉旗舰 —— 小六的核心是「看屏幕 + 动手」，
    /// 纯文本模型圈选/截图类提问会直接失效。设置页里可以从清单里换；
    /// 值为 <see cref="CustomModelKey"/> 时走自定义 OpenAI 兼容端点。
    /// </summary>
    public string Model { get; set; } = "doubao-seed-2-1-pro-260915";

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

    /// <summary>屏幕顶部那个常驻胶囊。关掉后只剩托盘图标。</summary>
    public bool ShowPill { get; set; } = true;

    /// <summary>回答气泡跟着指点目标浮动（ADR 0005）。关掉就固定贴在胶囊下方。</summary>
    public bool BubbleFollowsTarget { get; set; } = true;

    /// <summary>
    /// 允许模型自己调 <c>capture_screen</c> 抓当前屏幕（ADR 0010，推翻 ADR 0002 的禁令）。
    ///
    /// 默认开：用户问「你能看到我屏幕吗」时，让它自己抓一张比反问「你圈选一下」有用得多。
    /// 关掉就回到旧行为 —— capture_screen 一律拒绝（原因码 capture_not_user_initiated），
    /// 画面只能由用户圈选提供。
    /// </summary>
    public bool AllowAutonomousCapture { get; set; } = true;

    /// <summary>字幕条是否显示 ASR 中间结果。</summary>
    public bool ShowAsrPartial { get; set; } = true;

    /// <summary>默认不开：静默写自启项是用户最反感的一类行为。托盘菜单里显式勾选才写。</summary>
    public bool LaunchAtLogin { get; set; } = false;

    /// <summary>火山 TTS 音色 ID（大模型语音合成，如 <c>zh_female_tianmeixiaoyuan_moon_bigtts</c>）。</summary>
    public string TtsVoiceId { get; set; } = "zh_female_tianmeixiaoyuan_moon_bigtts";

    /// <summary>
    /// 录音设备序号，-1 跟随系统默认。机器上挂虚拟声卡（VB-Audio、远程桌面虚拟设备）时，
    /// 默认设备可能是一个永远静音的端点，表现为「按住说话没反应」。
    /// 用 <c>xiaoliu.exe --demo mic</c> 看设备清单和电平。
    /// </summary>
    public int CaptureDeviceNumber { get; set; } = -1;

    /// <summary>本轮对话最多几次模型请求（含工具回环），点点是 6。</summary>
    public int MaxModelRequestsPerTurn { get; set; } = 6;

    public int MaxToolCallsPerTurn { get; set; } = 20;

    [JsonIgnore] public bool IsCustomModel => Model == CustomModelKey;

    [JsonIgnore] public string? ArkApiKeyPlain => SecretProtector.Unprotect(ArkApiKey);

    [JsonIgnore] public string? VoiceAppIdPlain => SecretProtector.Unprotect(VoiceAppId);

    [JsonIgnore] public string? VoiceAccessTokenPlain => SecretProtector.Unprotect(VoiceAccessToken);

    [JsonIgnore] public string? CustomApiKeyPlain => SecretProtector.Unprotect(CustomApiKey);

    /// <summary>本轮请求真正发出去的模型标识（自定义端点用 <see cref="CustomModelName"/>）。</summary>
    [JsonIgnore]
    public string UpstreamModel => IsCustomModel ? CustomModelName : Model;

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
        if (string.IsNullOrWhiteSpace(TtsVoiceId) || TtsVoiceId.StartsWith("volcengine.", StringComparison.OrdinalIgnoreCase))
        {
            // 旧值是服务器方言（volcengine.xiaohe 之类），直连后上游只认官方音色 ID。
            TtsVoiceId = "zh_female_tianmeixiaoyuan_moon_bigtts";
        }
        return this;
    }

    public string DatabasePath => Path.Combine(DefaultDirectory, "history.db");

    public string ScreenshotDirectory => Path.Combine(DefaultDirectory, "screenshots");
}
