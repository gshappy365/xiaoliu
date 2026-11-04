using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Xiaoliu.Core.Protocol;

namespace Xiaoliu.Core.Voice;

/// <summary>
/// 语音直连火山语音技术网关（ADR 0011）。协议形状：
///
/// ASR（大模型流式识别）：一条 WS，up 完整请求（JSON）→ up 音频帧（裸 PCM 16k）→
/// 负序号空包收口；down 每帧 JSON 里 result.text 是累计文本，识别中持续下发。
///
/// TTS（大模型语音合成，双向流式）：StartConnection → StartSession → 每句一个
/// TaskRequest → FinishSession；down 音频帧（裸 PCM 24k）直到 SessionFinished。
///
/// 二进制帧编解码在 <see cref="VolcengineFrame"/> / <see cref="VolcEventFrameCodec"/> /
/// <see cref="VolcResponseParser"/>，纯逻辑可单测。
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

    private volatile string _lastText = string.Empty;
    private volatile string? _errorMessage;
    private volatile bool _lastPacketReceived;
    private int _sequence;

    public int SampleRate { get; }

    public event Action<string>? PartialReceived;

    private AsrSession(ClientWebSocket socket, int sampleRate)
    {
        _socket = socket;
        SampleRate = sampleRate;
        _sequence = 1;
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>连上大模型流式识别。凭证是语音技术的 appid + access token。</summary>
    public static async Task<AsrSession> ConnectAsync(
        string appId, string accessToken, int sampleRate, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("火山语音凭证未配置，请到设置 → 模型服务填写 App ID 和 Access Token。");
        }

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-Api-App-Key", appId);
        socket.Options.SetRequestHeader("X-Api-Access-Key", accessToken);
        socket.Options.SetRequestHeader("X-Api-Resource-Id", WireProtocol.AsrResourceId);
        socket.Options.SetRequestHeader("X-Api-Request-Id", Guid.NewGuid().ToString());
        await socket.ConnectAsync(new Uri(WireProtocol.AsrWebSocketUrl), cancellationToken);

        var session = new AsrSession(socket, sampleRate);
        var first = new JsonObject
        {
            ["user"] = new JsonObject { ["uid"] = "xiaoliu" },
            ["audio"] = new JsonObject
            {
                ["format"] = "pcm",
                ["codec"] = "raw",
                ["rate"] = sampleRate,
                ["bits"] = 16,
                ["channel"] = 1,
            },
            ["request"] = new JsonObject
            {
                ["model_name"] = "bigmodel",
                ["enable_punc"] = true,
                ["enable_itn"] = true,
                ["show_utterances"] = true,
            },
        };
        await session.SendFrameAsync(VolcengineFrame.FullRequest(1, first), cancellationToken);
        await session._ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        return session;
    }

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        return SendFrameAsync(VolcengineFrame.AudioRequest(sequence, pcm.Span, isLast: false), cancellationToken);
    }

    /// <summary>松手：负序号空包收口，等最终文本。识别不出字时返回空串。</summary>
    public async Task<string> FinishAsync(CancellationToken cancellationToken)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        await SendFrameAsync(VolcengineFrame.AudioRequest(sequence, ReadOnlySpan<byte>.Empty, isLast: true), cancellationToken);
        return await _final.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
    }

    public Task CancelAsync(CancellationToken cancellationToken) =>
        CloseSocketAsync(cancellationToken);

    private async Task PumpAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!_lifetime.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _lifetime.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (!result.EndOfMessage)
                {
                    continue; // 网关一帧不会超过 64KB，多段消息直接丢
                }

                var frame = VolcResponseParser.Parse(buffer.AsSpan(0, result.Count).ToArray());
                if (frame.IsError)
                {
                    _errorMessage = frame.Json?["message"]?.GetValue<string>()
                                    ?? $"火山识别错误（code={frame.ErrorCode}）";
                    continue;
                }

                _ready.TrySetResult();

                if (frame.Json is not null)
                {
                    var text = VolcResponseParser.ExtractText(frame.Json);
                    if (text.Length > 0 && text != _lastText)
                    {
                        _lastText = text;
                        PartialReceived?.Invoke(text);
                    }
                }

                if (frame.IsLast)
                {
                    _lastPacketReceived = true;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            if (_errorMessage is null)
            {
                _errorMessage = ex is OperationCanceledException ? null : "语音识别连接中断";
            }
        }
        finally
        {
            _ready.TrySetResult();
            _final.TrySetResult(_lastPacketReceived ? _lastText : string.Empty);
        }
    }

    private async Task SendFrameAsync(byte[] frame, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.SendAsync(frame, WebSocketMessageType.Binary, true, cancellationToken);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task CloseSocketAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cancellationToken);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await CloseSocketAsync(CancellationToken.None);
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
    private readonly string _connectionId = Guid.NewGuid().ToString();
    private readonly JsonObject _reqParams;

    private volatile string? _sessionId;
    private volatile string? _errorMessage;

    /// <summary>最近一次收到音频帧的时刻（ticks）。用来判断「还在出声」还是「真卡住了」。</summary>
    private long _lastAudioTicks = DateTimeOffset.UtcNow.UtcTicks;

    /// <summary>
    /// 多久没有新音频就判超时。不能拿「整轮总时长」当上限：合成是边合成边推流，
    /// 40 秒的答案本身就要念 40 秒以上，固定 60 秒会把长回答误判成卡死。
    /// </summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    public const int SampleRate = 24000;

    public event Action<byte[]>? AudioReceived;

    private TtsSession(ClientWebSocket socket, JsonObject reqParams)
    {
        _socket = socket;
        _reqParams = reqParams;
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>连上大模型语音合成（双向流式）。voiceId 是火山官方音色 ID。</summary>
    public static async Task<TtsSession> ConnectAsync(
        string appId, string accessToken, string voiceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("火山语音凭证未配置，请到设置 → 模型服务填写 App ID 和 Access Token。");
        }

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-Api-App-Key", appId);
        socket.Options.SetRequestHeader("X-Api-Access-Key", accessToken);
        socket.Options.SetRequestHeader("X-Api-Resource-Id", WireProtocol.TtsResourceId);
        var connectionId = Guid.NewGuid().ToString();
        socket.Options.SetRequestHeader("X-Api-Connect-Id", connectionId);
        await socket.ConnectAsync(new Uri(WireProtocol.TtsWebSocketUrl), cancellationToken);

        var reqParams = new JsonObject
        {
            ["audio_params"] = new JsonObject
            {
                ["format"] = "pcm",
                ["sample_rate"] = SampleRate,
            },
            ["speaker"] = voiceId,
        };
        var session = new TtsSession(socket, reqParams);

        // 握手两步：StartConnection → ConnectionStarted，StartSession → SessionStarted。
        await session.ExchangeAsync(
            VolcEventFrameCodec.Write(VolcEvent.StartConnection, session._connectionId, null, new JsonObject()),
            VolcEvent.ConnectionStarted, cancellationToken);
        var sessionPayload = new JsonObject
        {
            ["event"] = VolcEvent.StartSession,
            ["namespace"] = VolcEventFrameCodec.Namespace,
            ["req_params"] = reqParams.DeepClone(),
        };
        session._sessionId = await session.StartSessionAsync(sessionPayload, cancellationToken);
        return session;
    }

    private async Task ExchangeAsync(byte[] request, int expectEvent, CancellationToken cancellationToken)
    {
        await SendFrameAsync(request, cancellationToken);
        var response = await ReceiveFrameAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        if (response.Event != expectEvent)
        {
            var detail = response.Json?["message"]?.GetValue<string>();
            throw new InvalidOperationException(
                $"火山 TTS 握手失败：期望 event={expectEvent}，实际 event={response.Event}{(detail is null ? "" : $"（{detail}）")}。");
        }
    }

    private async Task<string> StartSessionAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        await SendFrameAsync(
            VolcEventFrameCodec.Write(VolcEvent.StartSession, _connectionId, null, payload), cancellationToken);
        var response = await ReceiveFrameAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        if (response.Event != VolcEvent.SessionStarted)
        {
            var detail = response.Json?["message"]?.GetValue<string>();
            throw new InvalidOperationException(
                $"火山 TTS 建会话失败：期望 SessionStarted，实际 event={response.Event}{(detail is null ? "" : $"（{detail}）")}。");
        }

        return response.SessionId ?? throw new InvalidOperationException("火山 TTS 建会话失败：没有 session id。");
    }

    public Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["event"] = VolcEvent.TaskRequest,
            ["namespace"] = VolcEventFrameCodec.Namespace,
            ["req_params"] = _reqParams.DeepClone(),
        };
        ((JsonObject)payload["req_params"]!)["text"] = text;
        return SendFrameAsync(
            VolcEventFrameCodec.Write(VolcEvent.TaskRequest, null, _sessionId, payload), cancellationToken);
    }

    public async Task FinishAsync(CancellationToken cancellationToken)
    {
        await SendFrameAsync(
            VolcEventFrameCodec.Write(VolcEvent.FinishSession, null, _sessionId, new JsonObject()), cancellationToken);

        // 等 SessionFinished 的同时盯着「有没有新音频」：只要还在出声音就一直等，
        // 超过 StallTimeout 一帧都没有才判卡住。这样长回答不会被误杀。
        while (true)
        {
            if (_finished.Task.IsCompleted)
            {
                break;
            }

            var idle = TimeSpan.FromTicks(DateTimeOffset.UtcNow.UtcTicks - Volatile.Read(ref _lastAudioTicks));
            if (idle >= StallTimeout)
            {
                throw new TimeoutException(
                    $"语音合成 {StallTimeout.TotalSeconds:0} 秒没有新音频，判定卡住。");
            }

            var done = await Task.WhenAny(_finished.Task, Task.Delay(StallTimeout - idle, cancellationToken))
                .ConfigureAwait(false);
            if (done == _finished.Task)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        if (_errorMessage is not null)
        {
            throw new InvalidOperationException(_errorMessage);
        }
    }

    public Task CancelAsync(CancellationToken cancellationToken) =>
        CloseSocketAsync(cancellationToken);

    private async Task PumpAsync()
    {
        var buffer = new byte[256 * 1024];
        try
        {
            while (!_lifetime.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _lifetime.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (!result.EndOfMessage)
                {
                    continue;
                }

                var frame = VolcEventFrameCodec.Parse(buffer.AsSpan(0, result.Count).ToArray());
                if (frame.Event == VolcEvent.SessionFailed
                    || frame.MessageType == VolcengineFrame.ServerErrorResponse)
                {
                    _errorMessage = frame.Json?["message"]?.GetValue<string>() ?? "火山语音合成失败";
                    continue;
                }

                if (frame.Audio is { Length: > 0 } pcm)
                {
                    // 先记活跃时间再交给播放：FinishAsync 靠它区分「还在出声」和「卡住了」。
                    Volatile.Write(ref _lastAudioTicks, DateTimeOffset.UtcNow.UtcTicks);
                    AudioReceived?.Invoke(pcm);
                    continue;
                }

                if (frame.SessionFinished)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            if (_errorMessage is null && ex is not OperationCanceledException)
            {
                _errorMessage = "语音合成连接中断";
            }
        }
        finally
        {
            _ready.TrySetResult();
            _finished.TrySetResult();
        }
    }

    private async Task SendFrameAsync(byte[] frame, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.SendAsync(frame, WebSocketMessageType.Binary, true, cancellationToken);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task<VolcEventFrame> ReceiveFrameAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[256 * 1024];
        while (true)
        {
            var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("火山 TTS 连接在握手阶段被关闭。");
            }

            if (!result.EndOfMessage)
            {
                continue;
            }

            return VolcEventFrameCodec.Parse(buffer.AsSpan(0, result.Count).ToArray());
        }
    }

    private async Task CloseSocketAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cancellationToken);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await CloseSocketAsync(CancellationToken.None);
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
