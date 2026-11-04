namespace Xiaoliu.Core.Protocol;

/// <summary>
/// 线协议常量。两套链路各有一半（ADR 0012）：
///
/// 云直连（ADR 0011）：对话与看图走方舟的 Anthropic 兼容端点，语音走火山语音技术的
/// openspeech 网关。这些地址是产品决策的一部分：改这里等于换上游，先看 docs/adr/0011。
///
/// 本地服务器（ADR 0012）：对话走 <c>{BaseUrl}/chat</c>，语音走 <c>{BaseUrl}/ws/*</c>。
/// 路径与点点 <c>ProxyServiceEndpoint</c> 逐字一致，服务器端据此提供同一套接口。
/// </summary>
public static class WireProtocol
{
    /// <summary>火山方舟 Anthropic 兼容端点（对话 + 看图 + 工具回环）。</summary>
    public const string ArkAnthropicEndpoint = "https://ark.cn-beijing.volces.com/api/anthropic/v1/messages";

    /// <summary>Anthropic Messages SSE 的结束帧是 <c>data: [DONE]</c>，不是 message_stop。</summary>
    public const string DoneMarker = "[DONE]";

    /// <summary>火山方舟（OpenAI 兼容 / Anthropic 兼容）都吃这个鉴权头。</summary>
    public const string AuthorizationHeader = "authorization";

    /// <summary>火山流式语音识别（大模型）WebSocket。凭证是语音技术 appid + access token。</summary>
    public const string AsrWebSocketUrl = "wss://openspeech.bytedance.com/api/v3/saas/bigmodel";

    /// <summary>大模型流式语音识别的资源 ID（按时长计费档）。</summary>
    public const string AsrResourceId = "volc.bigasr.sau.duration";

    /// <summary>大模型语音合成（双向流式）WebSocket。</summary>
    public const string TtsWebSocketUrl = "wss://openspeech.bytedance.com/api/v3/tts/bidirection";

    /// <summary>大模型语音合成的资源 ID。</summary>
    public const string TtsResourceId = "volc.service_type.10029";

    // ---------------------------------------------------------------- 本地服务器

    /// <summary>本地服务器：对话端点（相对 <c>BaseUrl</c>）。</summary>
    public const string ChatPath = "chat";

    /// <summary>本地服务器：语音合成中继 WebSocket（相对 <c>BaseUrl</c> 的 WS 根）。</summary>
    public const string TtsPath = "ws/tts/volcengine";

    /// <summary>本地服务器：语音识别中继 WebSocket（相对 <c>BaseUrl</c> 的 WS 根）。</summary>
    public const string AsrPath = "ws/asr/volcengine";

    /// <summary>本地服务器：健康检查（相对 <c>BaseUrl</c>）。</summary>
    public const string HealthPath = "healthz";

    /// <summary>本地服务器：能力查询（相对 <c>BaseUrl</c>）。</summary>
    public const string CapabilitiesPath = "capabilities";

    /// <summary>点点会带，服务端不校验；带上便于日志对账。</summary>
    public const string ClientBuildHeader = "X-DianDian-Client-Build";

    /// <summary>客户端标识，服务器端日志据此区分是点点还是 xiaoliu。</summary>
    public const string ClientBuild = "0.1.0-xiaoliu";

    /// <summary>HTTP 端点地址。</summary>
    public static Uri HttpUri(Uri baseUri, string path) => new(baseUri, path);

    /// <summary>
    /// WebSocket 地址。注意服务器把语音中继挂在 <c>/diandian/ws/*</c>，
    /// 而 HTTP 挂在 <c>/diandian/proxy/*</c> —— 两者不是同一个前缀，
    /// 所以这里把末尾的 <c>proxy</c> 去掉（点点的 ProxyWebSocket 常量也是这么配的）。
    /// </summary>
    public static Uri WebSocketUri(Uri baseUri, string path)
    {
        var root = new UriBuilder(baseUri);
        var trimmed = root.Path.TrimEnd('/');
        if (trimmed.EndsWith("/proxy", StringComparison.OrdinalIgnoreCase))
        {
            root.Path = trimmed[..^"/proxy".Length] + "/";
        }

        var builder = new UriBuilder(new Uri(root.Uri, path))
        {
            Scheme = baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
        };
        if (builder.Uri.IsDefaultPort)
        {
            builder.Port = -1;
        }

        return builder.Uri;
    }
}
