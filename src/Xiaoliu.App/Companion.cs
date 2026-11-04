using System.Diagnostics;
using System.Text;
using System.Windows;
using Xiaoliu.App.Audio;
using Xiaoliu.Core.Agent;
using Xiaoliu.Core.Capture;
using Xiaoliu.Core.Configuration;
using Xiaoliu.Core.Interop;
using Xiaoliu.Core.Protocol;
using Xiaoliu.Core.Storage;
using Xiaoliu.Core.Tools;
using Xiaoliu.Core.Voice;

namespace Xiaoliu.App;

/// <summary>
/// 一轮对话的全部编排：按住说话 → ASR → Agent（含工具回环）→ TTS → 落库。
///
/// 状态机很小，只有四个状态：空闲 / 在听 / 在想 / 在说。
/// 任意状态按 Esc 都能回到空闲，再按热键就是新的一轮。
/// </summary>
public sealed class Companion : IDisposable
{
    private readonly AppSettings _settings;
    private readonly SubtitleWindow _subtitle;
    private readonly OverlayWindow _overlay;
    private readonly AudioEngine _audio = new();
    private readonly VisualEvidence _evidence;
    private readonly HistoryStore _history;

    // transport 与 agent 不是 readonly：设置页改了连接方式/端点/密钥后必须重建，
    // 否则旧凭据会一直烤在构造时创建的那一份里 —— 设置页显示「通」但真实对话 401。
    private readonly object _transportGate = new();
    private IChatTransport _chat;
    private readonly ConversationAgent _agent;
    private readonly ToolDispatcher _dispatcher;

    private readonly object _gate = new();
    private readonly List<byte> _pendingAudio = new();
    private CancellationTokenSource? _turn;
    private IAsrSession? _asr;
    private ITtsSession? _tts;
    private DateTimeOffset _pressStartedAt;
    private bool _listening;
    private string _lastQuestion = string.Empty;
    private int _flushedLength;

    // 本轮采集的粗略电平，只为诊断「按住说话没反应」：到底是没录到声音，还是录到了但识别没结果。
    private int _capturePeak;
    private long _captureBytes;

    public Companion(AppSettings settings, SubtitleWindow subtitle, OverlayWindow overlay,
        VisualEvidence? evidence = null)
    {
        _settings = settings;
        _subtitle = subtitle;
        _overlay = overlay;

        _evidence = evidence ?? new VisualEvidence();
        _history = HistoryStore.OpenDefault(settings);
        _chat = ChatTransportFactory.Create(settings);
        _dispatcher = new ToolDispatcher(settings, _evidence, _history)
        {
            GuidanceRequested = mark => Ui(() =>
            {
                _overlay.Draw(mark);

                // 回答气泡跟着它指的地方走 —— 回答出现在被指的东西旁边，
                // 用户不用在「屏幕顶部」和「被指的位置」之间来回看。
                _subtitle.AnchorBounds = mark.Bounds;
            }),
            NoticeRequested = text => Ui(() => _subtitle.ShowTransient(text, "小六")),
            TargetProvider = () => NativeWindows.ListVisibleWindows(),
            Executed = (call, result) =>
            {
                var error = result["is_error"]?.GetValue<bool>() ?? false;
                Log.Info("tool", $"{call.Name} {(error ? "REJECTED" : "ok")}");
            },
        };
        _agent = new ConversationAgent(_chat, settings, _dispatcher, _evidence);
        _agent.TextUpdated += OnTextUpdated;
        _agent.ToolStarted += name => Ui(() => _subtitle.ShowStatus(DescribeTool(name)));

        _audio.AudioCaptured += OnAudioCaptured;
        _audio.CaptureDeviceNumber = settings.CaptureDeviceNumber;
    }

    /// <summary>
    /// 设置页改了连接方式、端点或密钥之后调用：重建 transport 与 agent。
    ///
    /// 不重建的话旧凭据会一直烤在构造时创建的那一份里 ——
    /// 设置页「保存并测试」测的是新建的请求，永远通；真实对话用的是旧凭据，永远 401。
    /// （上一轮 PR 审查的阻断项 B1。）
    /// </summary>
    public void RebuildTransport()
    {
        var previous = _chat;
        var replacement = ChatTransportFactory.Create(_settings);

        lock (_transportGate)
        {
            _chat = replacement;
            // 复用同一个 Agent，只换它的传输实现 —— 对话历史不能因为改了个设置就没了。
            _agent.ReplaceTransport(replacement);
        }

        if (previous is IDisposable disposable && !ReferenceEquals(previous, replacement))
        {
            disposable.Dispose();
        }

        Log.Info("transport", $"已重建（模式={_settings.ConnectionMode}）");
    }

    /// <summary>主窗口要读它来显示「这一轮可以看到的画面」，所以暴露出来而不是再开一份。</summary>
    public VisualEvidence Evidence => _evidence;

    /// <summary>一轮对话结束（语音和打字都算），给主窗口的对话页刷新用。</summary>
    public event Action<string, string>? TurnFinished;

    public VisualEvidence.Shot AddSelection(CaptureRect rect, CapturedImage image)
    {
        var shot = _evidence.Add(rect, image);
        Log.Info("capture", $"圈选 {shot.CaptureCallId} {rect.Width}x{rect.Height} hash={image.ContentHash[..8]}");
        return shot;
    }

    /// <summary>用户从磁盘选的一张图，和圈选走同一套证据池。</summary>
    public VisualEvidence.Shot AddImage(CapturedImage image)
    {
        var shot = _evidence.AddImage(image);
        Log.Info("capture", $"选图 {shot.CaptureCallId} {image.Width}x{image.Height} hash={image.ContentHash[..8]}");
        return shot;
    }

    /// <summary>
    /// 主窗口打字发问：同一套编排，但不接语音（用户正在看窗口，突然念出来很吵）。
    /// </summary>
    public Task AskTextAsync(string question) =>
        RunTurnAsync(question, _turn?.Token ?? CancellationToken.None, speak: false);

    /// <summary>
    /// 设置页换了录音设备：直接改到正在用的采集器上，下一轮就生效。
    /// 只在启动时读一次的话，用户选完设备按热键还是老设备，看起来像「选了没用」。
    /// </summary>
    public void ApplyCaptureDevice(int number) => _audio.CaptureDeviceNumber = number;

    // ---------------------------------------------------------------- 热键

    public void OnChordPressed()
    {
        _pressStartedAt = DateTimeOffset.UtcNow;

        // 正在说话时再按一次 = 打断。
        if (_tts is not null)
        {
            Interrupt();
            return;
        }

        if (_listening)
        {
            return;
        }

        _listening = true;
        _turn = new CancellationTokenSource();
        _pendingAudio.Clear();
        _flushedLength = 0;
        _ = StartListeningAsync(_turn.Token);
    }

    public void OnChordReleased()
    {
        if (!_listening)
        {
            return;
        }

        _listening = false;
        var held = DateTimeOffset.UtcNow - _pressStartedAt;
        _ = StopListeningAsync(held, _turn?.Token ?? CancellationToken.None);
    }

    public void OnEscape()
    {
        Interrupt();
        _overlay.Clear();
        _evidence.Clear();
        _subtitle.AnchorBounds = null;
        _subtitle.HideNow();
    }

    private void Interrupt()
    {
        try
        {
            _turn?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _audio.StopRecording();
        _audio.StopPlayback();
        _ = DisposeQuietlyAsync(_asr);
        _ = DisposeQuietlyAsync(_tts);
        _asr = null;
        _tts = null;
        _listening = false;
    }

    // ---------------------------------------------------------------- 听

    private async Task StartListeningAsync(CancellationToken token)
    {
        try
        {
            Ui(() => _subtitle.ShowStatus("在听…"));

            if (!_audio.StartRecording())
            {
                Ui(() => _subtitle.ShowTransient("麦克风打不开：" + (_audio.LastError ?? "未知原因"), "小六"));
                return;
            }

            _capturePeak = 0;
            _captureBytes = 0;

            var session = await VoiceSessionFactory.ConnectAsrAsync(
                _settings, AudioEngine.CaptureSampleRate, token);
            session.PartialReceived += text =>
            {
                if (_settings.ShowAsrPartial && !string.IsNullOrWhiteSpace(text))
                {
                    Ui(() => _subtitle.ShowText(text, "在听…"));
                }
            };

            lock (_gate)
            {
                _asr = session;
            }
        }
        catch (Exception ex)
        {
            Log.Error("asr", ex);
            _audio.StopRecording();
            Ui(() => _subtitle.ShowTransient("语音识别连不上：" + ex.Message, "小六"));
        }
    }

    private void OnAudioCaptured(byte[] chunk)
    {
        TrackLevel(chunk);

        var session = _asr;
        if (session is null)
        {
            lock (_gate)
            {
                _pendingAudio.AddRange(chunk);
                // 连接还没建立时的音频先攒着，最多攒 2 秒，避免爆内存。
                if (_pendingAudio.Count > AudioEngine.CaptureSampleRate * 2 * 2)
                {
                    _pendingAudio.RemoveRange(0, _pendingAudio.Count - AudioEngine.CaptureSampleRate * 2 * 2);
                }
            }

            return;
        }

        _ = SendAudioAsync(session, chunk);
    }

    /// <summary>只统计峰值和字节数 —— 这段跑在音频线程上，别的什么都不做。</summary>
    private void TrackLevel(byte[] chunk)
    {
        _captureBytes += chunk.Length;
        for (var i = 0; i + 1 < chunk.Length; i += 2)
        {
            var magnitude = Math.Abs((int)BitConverter.ToInt16(chunk, i));
            if (magnitude > _capturePeak)
            {
                _capturePeak = magnitude;
            }
        }
    }

    private async Task SendAudioAsync(IAsrSession session, byte[] chunk)
    {
        try
        {
            await session.SendAudioAsync(chunk, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error("asr", ex);
        }
    }

    private async Task StopListeningAsync(TimeSpan held, CancellationToken token)
    {
        _audio.StopRecording();
        var session = _asr;
        _asr = null;

        var seconds = _captureBytes / 2.0 / AudioEngine.CaptureSampleRate;
        var peakRatio = _capturePeak / 32768.0;
        Log.Info("audio", $"本轮采集 {seconds:F1}s peak={peakRatio:F4} 设备={_audio.LastCaptureDeviceName ?? "未知"}");

        // 一点声音都没有时直接说清楚，别让服务器那边的静音检测去猜。
        if (peakRatio < 0.005 && session is not null)
        {
            await DisposeQuietlyAsync(session);
            Log.Info("audio", "采集电平接近静音，未送识别");
            Ui(() => _subtitle.ShowTransient(
                $"没收到声音（设备：{_audio.LastCaptureDeviceName ?? "未知"}）。检查麦克风，或用 --demo mic 换个设备序号。", "小六"));
            return;
        }

        // 按不到 250ms 是误触（和点点的阈值一致）：直接丢弃，不要白跑一次识别。
        if (held < TimeSpan.FromMilliseconds(_settings.PushToTalkHoldMilliseconds) || session is null)
        {
            if (session is not null)
            {
                await DisposeQuietlyAsync(session);
            }

            Ui(() => _subtitle.HideNow());
            return;
        }

        try
        {
            Ui(() => _subtitle.ShowStatus("在听…"));
            var text = await session.FinishAsync(token);
            await DisposeQuietlyAsync(session);

            if (string.IsNullOrWhiteSpace(text))
            {
                Ui(() => _subtitle.ShowTransient("没听到你说什么，再说一次？", "小六"));
                return;
            }

            _lastQuestion = text.Trim();
            Ui(() => _subtitle.ShowText(_lastQuestion, "你"));
            await RunTurnAsync(_lastQuestion, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("asr", ex);
            Ui(() => _subtitle.ShowTransient("识别失败：" + ex.Message, "小六"));
        }
    }

    // ---------------------------------------------------------------- 想

    private async Task RunTurnAsync(string question, CancellationToken token, bool speak = true)
    {
        try
        {
            // 上一轮的标注不跨轮；截图按内容判活，过时的丢掉，还准的留着。
            Ui(() =>
            {
                _overlay.Clear();
                _subtitle.AnchorBounds = null;
            });
            var pruned = _evidence.Prune();
            if (pruned > 0)
            {
                Log.Info("capture", $"丢掉 {pruned} 张已过时的截图");
            }

            Ui(() => _subtitle.ShowStatus("在想…"));

            // TTS 与模型请求并行起来：等模型答完再连语音，首声会多等一个握手。
            // 打字提问不念出来，就别白连一条 WebSocket。
            if (speak)
            {
                _tts = await TryConnectTtsAsync(token);
            }

            var result = await _agent.RunTurnAsync(question, BuildRuntimeContext(), token);

            Ui(() => _subtitle.ShowText(result.Text, "小六"));
            if (speak)
            {
                await FinishSpeechAsync(result.Text, token);
            }

            Save(question, result);
            TurnFinished?.Invoke(question, result.Text);
            Log.Info("turn", $"stop={result.StopReason} 请求={result.ModelRequests} 工具={result.ToolCalls} 字数={result.Text.Length}");
        }
        catch (OperationCanceledException)
        {
            Log.Info("turn", "已打断");
        }
        catch (Exception ex)
        {
            Log.Error("turn", ex);
            Ui(() => _subtitle.ShowTransient("这一轮出错了：" + ex.Message, "小六"));
        }
        finally
        {
            _audio.StopPlayback();
            var tts = _tts;
            _tts = null;
            if (tts is not null)
            {
                await DisposeQuietlyAsync(tts);
            }
        }
    }

    private async Task<ITtsSession?> TryConnectTtsAsync(CancellationToken token)
    {
        try
        {
            var session = await VoiceSessionFactory.ConnectTtsAsync(_settings, token);
            session.AudioReceived += pcm => _audio.AddPcm(pcm);
            _audio.StartPlayback();
            return session;
        }
        catch (Exception ex)
        {
            // 语音合成连不上不该毁掉整轮：文字照常显示，只是没声音。
            Log.Error("tts", ex);
            Ui(() => _subtitle.ShowStatus("在想…（语音不可用，只显示文字）"));
            return null;
        }
    }

    private void OnTextUpdated(string accumulated)
    {
        Ui(() => _subtitle.ShowText(accumulated, "小六"));
        FeedSpeech(accumulated);
    }

    /// <summary>
    /// 边生成边念：攒到句末或 30 字就送一段。
    /// 一次送整段会让首声等到模型说完（十几秒），逐字送则每字一个语调，听感很碎。
    /// </summary>
    private void FeedSpeech(string accumulated)
    {
        var session = _tts;
        if (session is null || accumulated.Length <= _flushedLength)
        {
            return;
        }

        var pending = accumulated[_flushedLength..];
        if (pending.Length < 30 && !EndsSentence(pending))
        {
            return;
        }

        _flushedLength = accumulated.Length;
        _ = SendSpeechAsync(session, pending);
    }

    private static bool EndsSentence(string text)
    {
        var trimmed = text.TrimEnd();
        return trimmed.Length > 0 && "。！？!?…\n".Contains(trimmed[^1]);
    }

    private async Task SendSpeechAsync(ITtsSession session, string text)
    {
        try
        {
            await session.SendTextAsync(text, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error("tts", ex);
        }
    }

    private async Task FinishSpeechAsync(string finalText, CancellationToken token)
    {
        var session = _tts;
        if (session is null)
        {
            return;
        }

        // 最后一段没送出去的补上（例如答案没有句号结尾）。
        if (finalText.Length > _flushedLength)
        {
            await SendSpeechAsync(session, finalText[_flushedLength..]);
            _flushedLength = finalText.Length;
        }

        await session.FinishAsync(token);
        Ui(() => _subtitle.HideSoon(3));
    }

    // ---------------------------------------------------------------- 收尾

    private void Save(string question, AgentTurnResult result)
    {
        try
        {
            _history.AddEntry(question, result.Text, result.Screenshots);
        }
        catch (Exception ex)
        {
            Log.Error("history", ex);
        }
    }

    private Dictionary<string, string> BuildRuntimeContext()
    {
        var context = new Dictionary<string, string>
        {
            ["本机时间"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
        };

        var windows = NativeWindows.ListVisibleWindows();
        if (windows.Count > 0)
        {
            context["可填写目标"] = string.Join("、", windows.Select(w => w.ProcessName).Distinct().Take(12));
        }

        return context;
    }

    private static string DescribeTool(string name) => name switch
    {
        "point_at_screen" or "highlight_screen_region" or "connect_screen_regions" or "demonstrate_screen_drag" => "在指给你看…",
        "insert_text" => "在填字…",
        "copy_to_clipboard" => "在复制…",
        "read_user_file" => "在读文件…",
        "search_history" => "在翻历史…",
        "capture_screen" => "在想…",
        _ => "在想…",
    };

    private static void Ui(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }

    private static async Task DisposeQuietlyAsync(IAsyncDisposable? disposable)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            await disposable.DisposeAsync();
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        Interrupt();
        _audio.Dispose();
        if (_chat is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _history.Dispose();
    }
}
