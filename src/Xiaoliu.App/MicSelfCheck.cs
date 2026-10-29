using System.Windows.Threading;
using Xiaoliu.App.Audio;
using Xiaoliu.Core.Configuration;

namespace Xiaoliu.App;

/// <summary>
/// 开发/验收用自检：录音设备清单 + 实际电平。
///
/// 为什么要这个：这台机器上挂了好几个虚拟声卡（VB-Audio 线缆、远程桌面虚拟设备），
/// 系统默认录音设备一旦落到一个没人往里灌声音的虚拟端点上，「按住说话」就会表现为
/// 完全没反应 —— 看着像程序坏了，其实是设备选错了。所以验收前先把设备和电平看清楚。
///
/// 用法：<c>xiaoliu.exe --demo mic [秒数]</c>。默认录 3 秒。
/// 退出码 0 = 设备能打开且采到了数据；1 = 打不开。
/// </summary>
internal static class MicSelfCheck
{
    public static void Run(int seconds = 3, int? deviceNumber = null)
    {
        var settings = AppSettings.Load();
        var device = deviceNumber ?? settings.CaptureDeviceNumber;

        var devices = AudioEngine.ListCaptureDevices();
        Log.Info("selftest", $"mic 可用录音设备 {devices.Count} 个：{string.Join(" | ", devices)}");
        Log.Info("selftest", $"mic 使用 CaptureDeviceNumber={device}（-1 = 跟随系统默认），录 {seconds} 秒");

        var engine = new AudioEngine { CaptureDeviceNumber = device };

        long sampleCount = 0;
        double sumOfSquares = 0;
        var peak = 0;
        var gate = new object();

        engine.AudioCaptured += chunk =>
        {
            lock (gate)
            {
                for (var i = 0; i + 1 < chunk.Length; i += 2)
                {
                    var sample = BitConverter.ToInt16(chunk, i);
                    sumOfSquares += (double)sample * sample;
                    sampleCount++;
                    var magnitude = Math.Abs((int)sample);
                    if (magnitude > peak)
                    {
                        peak = magnitude;
                    }
                }
            }
        };

        if (!engine.StartRecording())
        {
            Log.Info("selftest", $"mic 结论：设备打不开（{engine.LastError ?? "未知原因"}）");
            engine.Dispose();
            Environment.Exit(1);
            return;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            engine.StopRecording();

            long count;
            double squares;
            int top;
            lock (gate)
            {
                count = sampleCount;
                squares = sumOfSquares;
                top = peak;
            }

            var rms = count == 0 ? 0 : Math.Sqrt(squares / count) / 32768.0;
            var peakRatio = top / 32768.0;
            Log.Info("selftest", $"mic 设备={engine.LastCaptureDeviceName ?? "未知"} 采样={count} rms={rms:F4} peak={peakRatio:F4}");

            if (count == 0)
            {
                Log.Info("selftest", "mic 结论：设备打开了但一个采样都没收到");
                engine.Dispose();
                Environment.Exit(1);
                return;
            }

            if (peakRatio < 0.01)
            {
                Log.Info("selftest", "mic 结论：能采到数据但电平接近静音 —— 换一个 CaptureDeviceNumber 再试（对照上面的设备清单）");
                engine.Dispose();
                Environment.Exit(0);
                return;
            }

            Log.Info("selftest", "mic 结论：有声音，链路可用");
            engine.Dispose();
            Environment.Exit(0);
        };

        timer.Start();
    }
}
