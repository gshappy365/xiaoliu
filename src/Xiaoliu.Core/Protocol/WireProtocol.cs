namespace Xiaoliu.Core.Protocol;

/// <summary>
/// 线协议常量。路径与点点 <c>ProxyServiceEndpoint</c> 逐字一致（见 reference/Core/…/ProxyServiceEndpoint.cs），
/// 只有基地址由配置给出，所以不需要 hosts 劫持。改这里等于改协议，先看 ADR 0001。
/// </summary>
public static class WireProtocol
{
    public const string ChatPath = "chat";
    public const string TtsPath = "ws/tts/volcengine";
    public const string AsrPath = "ws/asr/volcengine";
    public const string CapabilitiesPath = "capabilities";
    public const string HealthPath = "healthz";

    /// <summary>服务器端不看值，只看这个头存不存在。</summary>
    public const string AuthorizationHeader = "authorization";

    /// <summary>点点会带，服务端不校验；带上便于日志对账。</summary>
    public const string ClientBuildHeader = "X-DianDian-Client-Build";
    public const string ClientBuild = "0.1.0-xiaoliu";

    /// <summary>Anthropic Messages SSE 的结束帧是 <c>data: [DONE]</c>，不是 message_stop。</summary>
    public const string DoneMarker = "[DONE]";

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
