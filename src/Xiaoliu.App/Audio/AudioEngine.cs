using NAudio.Wave;

namespace Xiaoliu.App.Audio;

/// <summary>
/// 麦克风与扬声器。采集 16kHz / 16-bit / 单声道（SenseVoice 与服务器的约定），
/// 播放 24kHz / 16-bit / 单声道（TTS 中继的裸 PCM）。
///
/// 不做重采样：服务器两端都只吃固定采样率，客户端改一个数字就等于改协议。
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int CaptureSampleRate = 16000;
    public const int PlaybackSampleRate = 24000;

    private WaveInEvent? _capture;
    private WaveOutEvent? _output;
    private BufferedWaveProvider? _buffer;

    /// <summary>采集到的 PCM（16k/16bit/mono）。回调在音频线程上，别在里面做重活。</summary>
    public event Action<byte[]>? AudioCaptured;

    public bool IsRecording => _capture is not null;

    public string? LastError { get; private set; }

    public bool StartRecording()
    {
        if (_capture is not null)
        {
            return true;
        }

        try
        {
            _capture = new WaveInEvent
            {
                WaveFormat = new WaveFormat(CaptureSampleRate, 16, 1),
                BufferMilliseconds = 100,
                NumberOfBuffers = 3,
            };
            _capture.DataAvailable += (_, args) =>
            {
                var chunk = new byte[args.BytesRecorded];
                Array.Copy(args.Buffer, chunk, args.BytesRecorded);
                AudioCaptured?.Invoke(chunk);
            };
            _capture.RecordingStopped += (_, args) =>
            {
                if (args.Exception is not null)
                {
                    LastError = args.Exception.Message;
                    Log.Error("audio", args.Exception);
                }
            };
            _capture.StartRecording();
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Error("audio", ex);
            _capture?.Dispose();
            _capture = null;
            return false;
        }
    }

    public void StopRecording()
    {
        var capture = _capture;
        _capture = null;
        if (capture is null)
        {
            return;
        }

        try
        {
            capture.StopRecording();
        }
        catch (Exception ex)
        {
            Log.Error("audio", ex);
        }
        finally
        {
            capture.Dispose();
        }
    }

    /// <summary>开始一轮播放。缓冲区给 30 秒，够长；满了就丢新帧，宁可断音也不要卡住。</summary>
    public void StartPlayback()
    {
        StopPlayback();

        _buffer = new BufferedWaveProvider(new WaveFormat(PlaybackSampleRate, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(30),
            DiscardOnBufferOverflow = true,
        };
        _output = new WaveOutEvent { DesiredLatency = 120 };
        _output.Init(_buffer);
        _output.Play();
    }

    public void AddPcm(byte[] pcm) => _buffer?.AddSamples(pcm, 0, pcm.Length);

    public void StopPlayback()
    {
        try
        {
            _output?.Stop();
        }
        catch (Exception ex)
        {
            Log.Error("audio", ex);
        }

        _output?.Dispose();
        _output = null;
        _buffer = null;
    }

    public void Dispose()
    {
        StopRecording();
        StopPlayback();
    }
}
