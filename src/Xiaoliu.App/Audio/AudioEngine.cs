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

    /// <summary>
    /// 录音设备序号，-1 表示跟随系统默认。机器上挂虚拟声卡（VB-Audio、远程桌面虚拟设备）时，
    /// 默认设备很容易落到一个永远静音的端点上 —— 表现为「按住说话没反应」，看起来像程序坏了。
    /// </summary>
    public int CaptureDeviceNumber { get; set; } = -1;

    /// <summary>上一次实际打开的录音设备名，日志与自检用。</summary>
    public string? LastCaptureDeviceName { get; private set; }

    /// <summary>枚举系统里的录音设备，序号与 WaveInEvent.DeviceNumber 一致。</summary>
    public static IReadOnlyList<string> ListCaptureDevices()
    {
        var names = new List<string>();
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            names.Add($"{i}: {WaveInEvent.GetCapabilities(i).ProductName}");
        }

        return names;
    }

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
                DeviceNumber = CaptureDeviceNumber,
                WaveFormat = new WaveFormat(CaptureSampleRate, 16, 1),
                BufferMilliseconds = 100,
                NumberOfBuffers = 3,
            };

            try
            {
                LastCaptureDeviceName = WaveInEvent.GetCapabilities(CaptureDeviceNumber).ProductName;
            }
            catch (Exception)
            {
                LastCaptureDeviceName = null;
            }

            Log.Info("audio", $"开始采集 设备={CaptureDeviceNumber}（{LastCaptureDeviceName ?? "未知"}）16kHz/16bit/mono");
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
