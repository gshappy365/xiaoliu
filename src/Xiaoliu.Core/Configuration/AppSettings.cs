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

    /// <summary>连接方式：云直连（ADR 0011）。</summary>
    public const string ConnectionModeCloud = "cloud";

    /// <summary>连接方式：本地服务器（ADR 0012）。</summary>
    public const string ConnectionModeLocalServer = "local-server";

    /// <summary>
    /// 走哪条链路（ADR 0012）。<see cref="ConnectionModeCloud"/> 是云直连：
    /// 对话与看图走火山方舟，语音走火山语音技术，客户端自带凭证。
    /// <see cref="ConnectionModeLocalServer"/> 是本地服务器：三条链路都经本机服务器端。
    /// </summary>
    public string ConnectionMode { get; set; } = ConnectionModeCloud;

    /// <summary>
    /// 本地服务器模式的基地址，如 <c>https://127.0.0.1/diandian/proxy</c>。明文存。
    /// 云直连模式下不读。
    /// </summary>
    public string BaseUrl { get; set; } = "https://127.0.0.1/diandian/proxy";

    /// <summary>
    /// 本地服务器模式的访问口令，明文存 —— 它不是云凭证，DPAPI 加密只会带来
    /// 「换机后解不开」的麻烦而没有安全收益。
    /// </summary>
    public string AccessToken { get; set; } = "selfhost";

    /// <summary>
    /// 本地服务器模式：放行自签证书。服务器端的证书 SAN 是 botlearn 域名，
    /// 走 127.0.0.1 时主机名校验必然失败。云直连模式下不读。
    /// </summary>
    public bool AllowUntrustedTls { get; set; } = true;

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

    /// <summary>当前是不是走本地服务器（ADR 0012）。</summary>
    [JsonIgnore] public bool IsLocalServerMode => ConnectionMode == ConnectionModeLocalServer;

    /// <summary>当前是不是走云直连（ADR 0011）。</summary>
    [JsonIgnore] public bool IsCloudDirectMode => !IsLocalServerMode;

    /// <summary>本地服务器模式的基地址。空值会在 <see cref="Normalize"/> 里补回默认值。</summary>
    [JsonIgnore] public Uri ServerBaseUri => new(BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);

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
                    // 记住来源路径：Save() 无参时写回同一个文件，
                    // 而不是「谁 new 出来的就往默认目录写」。
                    loaded.SourcePath = path;
                    return loaded.Normalize();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 配置坏了不能拖死启动：落回默认值，下一次保存会覆盖。
        }

        // 文件不存在时也要记住路径：用户第一次改设置时得能存下去。
        // 注意与「测试里 new 出来的实例」区分 —— 那种 SourcePath 是 null，Save 不写。
        var fresh = new AppSettings { SourcePath = path };
        return fresh.Normalize();
    }

    /// <summary>
    /// 这个配置是从哪个文件读出来的。<see cref="Save"/> 无参时写回它。
    ///
    /// 为什么需要它：`Save()` 无参曾经直接写默认目录，于是**测试里 new 出来的
    /// AppSettings 一调 Save 就把用户真实配置覆盖成默认值**（实测：
    /// ToolPolicyTests 触发 update_setting → Save() → 用户配置的
    /// Model/ImageRetentionDays 被抹掉）。这不是本 PR 引入的，但 ADR 0012
    /// 恢复 BaseUrl/AccessToken 后破坏面更大 —— 旧字段会被静默丢弃。
    ///
    /// 现在只有从文件加载出来的配置才知道往哪儿写；手工 new 出来的必须显式给路径。
    /// </summary>
    [JsonIgnore]
    public string? SourcePath { get; internal set; }

    /// <summary>
    /// 写回配置。<paramref name="path"/> 为空时写回 <see cref="SourcePath"/>；
    /// 两者都没有就**不写**（防止误覆盖用户真实配置）。
    /// </summary>
    public void Save(string? path = null)
    {
        path ??= SourcePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            // 没有明确目标路径就不写。调用方（App、ToolDispatcher）都是从
            // Load() 拿到的配置，SourcePath 一定有值。
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        SourcePath = path;
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

        // 连接方式只认两个值。配置被手改过或从别的版本迁来时兜回云直连 ——
        // 那是 ADR 0011 的默认形态，也是唯一不依赖外部进程的一条。
        if (ConnectionMode is not (ConnectionModeCloud or ConnectionModeLocalServer))
        {
            ConnectionMode = ConnectionModeCloud;
        }

        // 本地服务器模式的基地址不能为空：空值会让工厂在构造期抛异常，
        // 而 App.OnStartup 的异常 WPF 兜不住 —— 表现为「重启即崩，且崩在设置页之前，
        // 没法自救」。这里自愈掉，让用户还能进设置页改。
        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            BaseUrl = "https://127.0.0.1/diandian/proxy";
        }

        // 自定义端点的 base URL 为空时不致命（模型选回非 custom 就不走它），
        // 但不能是解析不了的地址 —— 构造期抛异常会让启动崩掉（WPF 兜不住启动期异常）。
        // 自愈方式：把模型从「自定义」退回方舟默认，这样用户还能正常用。
        if (IsCustomModel && !Uri.TryCreate(CustomBaseUrl, UriKind.Absolute, out _))
        {
            Model = "doubao-seed-2-1-pro-260915";
        }

        return this;
    }

    public string DatabasePath => Path.Combine(DefaultDirectory, "history.db");

    public string ScreenshotDirectory => Path.Combine(DefaultDirectory, "screenshots");
}
