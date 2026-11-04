using Xiaoliu.Core.Configuration;
using Xiaoliu.Core.Protocol;

namespace Xiaoliu.Core.Voice;

/// <summary>
/// 按连接方式造语音会话（ADR 0012）。
///
/// 云直连走火山语音技术的原生 WS 协议；本地服务器走点点的 JSON 帧中继协议。
/// 两条线协议不兼容（ADR 0012 有对照表），所以实现是两套 —— 但对外的表达一致，
/// 编排层（<c>Companion</c>）只跟这个工厂说话。
/// </summary>
public static class VoiceSessionFactory
{
    /// <summary>连上语音识别。</summary>
    public static async Task<IAsrSession> ConnectAsrAsync(
        AppSettings settings, int sampleRate, CancellationToken cancellationToken)
    {
        if (settings.IsLocalServerMode)
        {
            var uri = WireProtocol.WebSocketUri(settings.ServerBaseUri, WireProtocol.AsrPath);
            return await LocalServerAsrSession.ConnectAsync(
                uri, sampleRate, cancellationToken, settings.AllowUntrustedTls);
        }

        return await VolcengineAsrSession.ConnectAsync(
            settings.VoiceAppIdPlain ?? string.Empty,
            settings.VoiceAccessTokenPlain ?? string.Empty,
            sampleRate,
            cancellationToken);
    }

    /// <summary>连上语音合成。</summary>
    public static async Task<ITtsSession> ConnectTtsAsync(
        AppSettings settings, CancellationToken cancellationToken)
    {
        if (settings.IsLocalServerMode)
        {
            var uri = WireProtocol.WebSocketUri(settings.ServerBaseUri, WireProtocol.TtsPath);
            return await LocalServerTtsSession.ConnectAsync(
                uri, settings.TtsVoiceId, cancellationToken, settings.AllowUntrustedTls);
        }

        return await VolcengineTtsSession.ConnectAsync(
            settings.VoiceAppIdPlain ?? string.Empty,
            settings.VoiceAccessTokenPlain ?? string.Empty,
            settings.TtsVoiceId,
            cancellationToken);
    }
}
