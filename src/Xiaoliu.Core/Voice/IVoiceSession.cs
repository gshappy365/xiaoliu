namespace Xiaoliu.Core.Voice;

/// <summary>
/// 语音识别的会话接缝（ADR 0012）。
///
/// 两套实现：
/// - <see cref="VolcengineAsrSession"/>：云直连，走火山语音技术原生二进制帧协议。
/// - <see cref="LocalServerAsrSession"/>：本地服务器，走点点的 JSON 帧中继协议。
///
/// 编排层（Companion）只跟这个接口说话，不关心底下是哪条线协议 ——
/// 两条线的协议形状完全不同，但对外的表达是一样的。
/// </summary>
public interface IAsrSession : IAsyncDisposable
{
    /// <summary>采样率（Hz）。录音端按这个率喂 PCM。</summary>
    int SampleRate { get; }

    /// <summary>识别中间结果。每来一次更长的累积文本就触发一次。</summary>
    event Action<string>? PartialReceived;

    /// <summary>送一段音频（裸 PCM，16-bit 单声道）。</summary>
    Task SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken);

    /// <summary>松手：结束识别并等最终文本。识别不出字时返回空串。</summary>
    Task<string> FinishAsync(CancellationToken cancellationToken);

    /// <summary>放弃这一轮（用户按 Esc 或改主意了）。</summary>
    Task CancelAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 语音合成的会话接缝（ADR 0012）。
///
/// 实现：
/// - <see cref="VolcengineTtsSession"/>：云直连，火山事件帧协议。
/// - <see cref="LocalServerTtsSession"/>：本地服务器，点点的 JSON 帧中继协议。
/// </summary>
public interface ITtsSession : IAsyncDisposable
{
    /// <summary>输出采样率（Hz）。播放端按这个率消费 PCM。</summary>
    int SampleRate { get; }

    /// <summary>收到一段音频（裸 PCM，16-bit 单声道）。</summary>
    event Action<byte[]>? AudioReceived;

    /// <summary>送一段文本去合成。</summary>
    Task SendTextAsync(string text, CancellationToken cancellationToken);

    /// <summary>结束合成并等音频放完。</summary>
    Task FinishAsync(CancellationToken cancellationToken);

    /// <summary>放弃这一轮（用户打断了）。</summary>
    Task CancelAsync(CancellationToken cancellationToken);
}
