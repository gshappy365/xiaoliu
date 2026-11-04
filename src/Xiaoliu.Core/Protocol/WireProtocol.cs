namespace Xiaoliu.Core.Protocol;

/// <summary>
/// 云直连的上游端点（ADR 0011）。客户端不再经过自建服务器：
/// 对话/看图走方舟的 Anthropic 兼容端点，语音走火山语音技术的 openspeech 网关。
/// 这些地址是产品决策的一部分：改这里等于换上游，先看 docs/adr/0011。
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
}
