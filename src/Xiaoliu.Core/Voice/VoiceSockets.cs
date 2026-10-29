using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xiaoliu.Core.Voice;

/// <summary>
/// 语音中继的双 WebSocket。协议形状照抄点点抓包（见 rea/server/app/main.py 的两个 ws 端点）：
///
/// ASR：up start → down ready → down partial*（累积文本）→ up finish → down final
/// TTS：down connected → up start → down ready → up text* → up finish → down finished
///
/// 二进制帧是裸 PCM：ASR 16kHz、TTS 24kHz，都是 16-bit 单声道，没有容器头。
/// </summary>
public sealed class AsrSession : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly TaskCompletionSource<string> _final =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _pump;

    public int SampleRate { get; }

    public event Action<string>? PartialReceived;

    private AsrSession(ClientWebSocket socket, int sampleRate)
    {
        _socket = socket;
        SampleRate = sampleRate;
        _pump = Task.Run(PumpAsync);
    }

    public static async Task<AsrSession> ConnectAsync(Uri uri, int sampleRate, CancellationToken cancellationToken,
        bool allowUntrustedTls = false)
    {
        var socket = new ClientWebSocket();
        if (allowUntrustedTls)
        {
            socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        await socket.ConnectAsync(uri, cancellationToken);
        var session = new AsrSession(socket, sampleRate);
        await session.SendJsonAsync(new JsonObject { ["type"] = "start", ["sample_rate"] = sampleRate }, cancellationToken);
        await session._ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        return session;
    }

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken) =>
        SendRawAsync(pcm, WebSocketMessageType.Binary, cancellationToken);

    /// <summary>松手：送 finish，等 final。识别不出字时返回空串。</summary>
    public async Task<string> FinishAsync(CancellationToken cancellationToken)
    {
        await SendJsonAsync(new JsonObject { ["type"] = "finish" }, cancellationToken);
        return await _final.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
    }

    public Task CancelAsync(CancellationToken cancellationToken) =>
        SendJsonAsync(new JsonObject { ["type"] = "cancel" }, cancellationToken);

    private async Task PumpAsync()
    {
        var buffer = new byte[8192];
        var textBuffer = new StringBuilder();
        try
        {
            while (!_lifetime.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _lifetime.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    continue;
                }

                textBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var payload = textBuffer.ToString();
                textBuffer.Clear();
                Handle(payload);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // 关闭路径不记错误
        }
        finally
        {
            _ready.TrySetResult();
            _final.TrySetResult(string.Empty);
        }
    }

    private void Handle(string payload)
    {
        JsonObject? message;
        try
        {
            message = JsonNode.Parse(payload) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }

        switch (message?["type"]?.GetValue<string>())
        {
            case "ready":
                _ready.TrySetResult();
                break;
            case "partial":
                PartialReceived?.Invoke(message["text"]?.GetValue<string>() ?? string.Empty);
                break;
            case "final":
                _final.TrySetResult(message["text"]?.GetValue<string>() ?? string.Empty);
                break;
        }
    }

    private async Task SendJsonAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        await SendRawAsync(bytes, WebSocketMessageType.Text, cancellationToken);
    }

    private async Task SendRawAsync(ReadOnlyMemory<byte> bytes, WebSocketMessageType type, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.SendAsync(bytes, type, true, cancellationToken);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
        }

        _socket.Dispose();
        try
        {
            await _pump;
        }
        catch (Exception)
        {
        }

        _lifetime.Dispose();
        _sendGate.Dispose();
    }
}

/// <summary>TTS 会话。文本按句送，音频按帧回，播放由 App 侧排队。</summary>
public sealed class TtsSession : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _pump;

    public const int SampleRate = 24000;

    public event Action<byte[]>? AudioReceived;

    private TtsSession(ClientWebSocket socket)
    {
        _socket = socket;
        _pump = Task.Run(PumpAsync);
    }

    public static async Task<TtsSession> ConnectAsync(Uri uri, string voiceId, CancellationToken cancellationToken,
        bool allowUntrustedTls = false)
    {
        var socket = new ClientWebSocket();
        if (allowUntrustedTls)
        {
            socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        await socket.ConnectAsync(uri, cancellationToken);
        var session = new TtsSession(socket);
        await session.SendJsonAsync(new JsonObject { ["type"] = "start", ["voice_id"] = voiceId }, cancellationToken);
        await session._ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        return session;
    }

    public Task SendTextAsync(string text, CancellationToken cancellationToken) =>
        SendJsonAsync(new JsonObject { ["type"] = "text", ["text"] = text }, cancellationToken);

    public async Task FinishAsync(CancellationToken cancellationToken)
    {
        await SendJsonAsync(new JsonObject { ["type"] = "finish" }, cancellationToken);
        await _finished.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
    }

    public Task CancelAsync(CancellationToken cancellationToken) =>
        SendJsonAsync(new JsonObject { ["type"] = "cancel" }, cancellationToken);

    private async Task PumpAsync()
    {
        var buffer = new byte[16384];
        var textBuffer = new StringBuilder();
        try
        {
            while (!_lifetime.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _lifetime.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    var pcm = new byte[result.Count];
                    Array.Copy(buffer, pcm, result.Count);
                    AudioReceived?.Invoke(pcm);
                    continue;
                }

                textBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var payload = textBuffer.ToString();
                textBuffer.Clear();
                Handle(payload);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
        }
        finally
        {
            _ready.TrySetResult();
            _finished.TrySetResult();
        }
    }

    private void Handle(string payload)
    {
        JsonObject? message;
        try
        {
            message = JsonNode.Parse(payload) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }

        switch (message?["type"]?.GetValue<string>())
        {
            case "connected":
            case "ready":
                _ready.TrySetResult();
                break;
            case "finished":
                _finished.TrySetResult();
                break;
        }
    }

    private async Task SendJsonAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
        }

        _socket.Dispose();
        try
        {
            await _pump;
        }
        catch (Exception)
        {
        }

        _lifetime.Dispose();
        _sendGate.Dispose();
    }
}
