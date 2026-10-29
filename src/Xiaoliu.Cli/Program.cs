using System.Diagnostics;
using System.Text;
using Xiaoliu.Core.Agent;
using Xiaoliu.Core.Capture;
using Xiaoliu.Core.Configuration;
using Xiaoliu.Core.Interop;
using Xiaoliu.Core.Protocol;
using Xiaoliu.Core.Storage;
using Xiaoliu.Core.Tools;
using Xiaoliu.Core.Voice;

namespace Xiaoliu.Cli;

/// <summary>
/// 无界面的联调台。界面还没写完时，用它证明「线协议层通了」——
/// 每一条子命令都对应一个真实往返，不 mock。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var settings = AppSettings.Load();

        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            return args[0] switch
            {
                "health" => await HealthAsync(settings),
                "chat" => await ChatAsync(settings, args.Skip(1).ToArray()),
                "asr" => await AsrAsync(settings, args.Skip(1).ToArray()),
                "tts" => await TtsAsync(settings, args.Skip(1).ToArray()),
                "tools" => Tools(),
                "shot" => Shot(args.Skip(1).ToArray()),
                "windows" => Windows(args.Skip(1).ToArray()),
                "type" => TypeInto(args.Skip(1).ToArray()),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"失败：{ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            xiaoliu-cli —— 线协议联调台

              health                          读 /healthz 与 /capabilities
              chat "问题" [--tools] [--image 路径]   走一轮对话（--image 模拟用户圈选）
              asr 文件.wav                    把 16k/16bit 单声道 wav 送进 ASR 中继
              tts "文字" [--out 文件.wav]      合成并落盘
              tools                           打印本轮声明的 14 个工具
              windows [过滤词] [--hidden]      列出顶层窗口（句柄、进程、位置、可见性）
              type "窗口标题" "文本"            把文字打进那个窗口（先抢前台）

            配置：%LOCALAPPDATA%\xiaoliu\settings.json（不存在则用默认值）
            """);
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"未知命令：{command}");
        PrintUsage();
        return 1;
    }

    private static async Task<int> HealthAsync(AppSettings settings)
    {
        using var client = new HttpClient(ChatClient.CreateHandler(settings)) { Timeout = TimeSpan.FromSeconds(10) };
        var health = await client.GetStringAsync(WireProtocol.HttpUri(settings.BaseUri, WireProtocol.HealthPath));
        Console.WriteLine($"health  {WireProtocol.HttpUri(settings.BaseUri, WireProtocol.HealthPath)}");
        Console.WriteLine(Pretty(health));

        try
        {
            var capabilities = await client.GetStringAsync(WireProtocol.HttpUri(settings.BaseUri, WireProtocol.CapabilitiesPath));
            Console.WriteLine($"capabilities  {capabilities.Length} 字节");
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"capabilities 取不到：{ex.Message}");
        }

        return 0;
    }

    private static async Task<int> ChatAsync(AppSettings settings, string[] args)
    {
        var useTools = args.Contains("--tools");
        var imagePath = TakeOption(args, "--image");
        var liveCapture = args.Contains("--capture");
        var question = string.Join(" ", args.Where(a => !a.StartsWith("--", StringComparison.Ordinal) && a != imagePath));

        using var history = new HistoryStore(settings.DatabasePath);

        // --capture 走真实路径（真截图 + 真 hash），--image 是固定夹具：
        // 文件里的图不是现在抓的，hash 校验必然对不上，所以夹具模式下跳过校验。
        var evidence = new VisualEvidence(imagePath is not null ? (_, _) => true : null);
        if (liveCapture)
        {
            var bounds = ScreenCapture.VirtualScreenBounds();
            var captured = ScreenCapture.Capture(bounds);
            var shot = evidence.Add(bounds, captured);
            Console.WriteLine($"[圈选] {shot.CaptureCallId} ← 实时抓屏 {captured.Width}x{captured.Height} "
                              + $"{captured.Bytes.Length / 1024}KB hash={captured.ContentHash[..12]}");
        }
        else if (imagePath is not null)
        {
            var bytes = await File.ReadAllBytesAsync(imagePath);
            var image = new CapturedImage(bytes,
                imagePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg",
                1280, 720, "cli-fixture", new byte[32 * 32]);
            var shot = evidence.Add(new CaptureRect(0, 0, 1280, 720), image);
            Console.WriteLine($"[圈选] {shot.CaptureCallId} ← {imagePath}（夹具，跳过 hash 校验）");
        }

        var dispatcher = new ToolDispatcher(settings, evidence, history)
        {
            GuidanceRequested = mark =>
                Console.WriteLine($"[指点] {mark.Kind} {mark.Label} @ ({mark.Bounds.X},{mark.Bounds.Y}) {mark.Bounds.Width}x{mark.Bounds.Height}"),
            NoticeRequested = text => Console.WriteLine($"[提示] {text}"),
            Executed = (call, result) =>
            {
                var isError = result["is_error"]?.GetValue<bool>() ?? false;
                var content = result["content"]?.GetValue<string>() ?? "";
                var shown = content.Length > 220 ? content[..220] + "…" : content;
                Console.WriteLine($"  ↳ {call.Name} {(isError ? "REJECTED" : "ok")} {shown}");
            },
        };

        if (!useTools)
        {
            // 不声明工具时，用 dispatcher 执行不到任何东西；这里只是把开关显式化。
            Console.WriteLine("[提示] 未加 --tools：本轮不声明工具，模型只能直接回答。");
        }

        using var chat = new ChatClient(settings);
        var agent = new ConversationAgent(chat, settings, dispatcher, evidence);
        var stopwatch = Stopwatch.StartNew();
        var firstToken = -1L;

        agent.TextUpdated += text =>
        {
            if (firstToken < 0)
            {
                firstToken = stopwatch.ElapsedMilliseconds;
            }

            Console.Write($"\r{text}");
        };
        agent.ToolStarted += name => Console.WriteLine($"\n[tool] {name}");

        var result = await agent.RunTurnAsync(question, null, CancellationToken.None);
        Console.WriteLine();
        Console.WriteLine($"--- 首字 {firstToken}ms / 总 {stopwatch.ElapsedMilliseconds}ms / "
                          + $"请求 {result.ModelRequests} 次 / 工具 {result.ToolCalls} 次 / stop={result.StopReason}");

        history.AddEntry(question, result.Text, result.Screenshots);
        return 0;
    }

    private static async Task<int> AsrAsync(AppSettings settings, string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("需要 wav 路径");
            return 1;
        }

        var (pcm, sampleRate) = ReadWav(args[0]);
        var uri = WireProtocol.WebSocketUri(settings.BaseUri, WireProtocol.AsrPath);
        Console.WriteLine($"asr  {uri}  {pcm.Length / 2 / sampleRate:F2}s @ {sampleRate}Hz");

        await using var session = await AsrSession.ConnectAsync(uri, sampleRate, CancellationToken.None, settings.AllowUntrustedTls);
        session.PartialReceived += text => Console.WriteLine($"[partial] {text}");

        // 按 100ms 一帧推，模拟麦克风节奏。
        var frame = sampleRate / 10 * 2;
        for (var offset = 0; offset < pcm.Length; offset += frame)
        {
            var size = Math.Min(frame, pcm.Length - offset);
            await session.SendAudioAsync(pcm.AsMemory(offset, size), CancellationToken.None);
        }

        var final = await session.FinishAsync(CancellationToken.None);
        Console.WriteLine($"[final] {final}");
        return string.IsNullOrEmpty(final) ? 3 : 0;
    }

    private static async Task<int> TtsAsync(AppSettings settings, string[] args)
    {
        var outPath = TakeOption(args, "--out");
        var text = string.Join(" ", args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)));
        if (string.IsNullOrWhiteSpace(text))
        {
            Console.Error.WriteLine("需要要念的文字");
            return 1;
        }

        var uri = WireProtocol.WebSocketUri(settings.BaseUri, WireProtocol.TtsPath);
        var stopwatch = Stopwatch.StartNew();
        var pcm = new MemoryStream();
        long firstFrame = -1;

        await using var session = await TtsSession.ConnectAsync(uri, settings.TtsVoiceId, CancellationToken.None, settings.AllowUntrustedTls);
        session.AudioReceived += bytes =>
        {
            if (firstFrame < 0)
            {
                firstFrame = stopwatch.ElapsedMilliseconds;
            }

            pcm.Write(bytes);
        };

        await session.SendTextAsync(text, CancellationToken.None);
        await session.FinishAsync(CancellationToken.None);

        var seconds = pcm.Length / 2.0 / TtsSession.SampleRate;
        Console.WriteLine($"tts  首帧 {firstFrame}ms / 总 {stopwatch.ElapsedMilliseconds}ms / "
                          + $"{pcm.Length} 字节 ≈ {seconds:F2}s 音频");

        if (outPath is not null)
        {
            await File.WriteAllBytesAsync(outPath, BuildWav(pcm.ToArray(), TtsSession.SampleRate));
            Console.WriteLine($"已写出 {outPath}");
        }

        return pcm.Length > 0 ? 0 : 3;
    }

    private static int Tools()
    {
        ToolCatalog.ValidateSchemas();
        foreach (var tool in ToolCatalog.All)
        {
            Console.WriteLine($"{tool.Name,-26} {tool.Description.Length,5} 字  schema {tool.InputSchemaJson.Length,5} 字节");
        }

        Console.WriteLine($"\n不实现（收到即 rejected）：{string.Join("、", ToolCatalog.RejectedTools)}");
        return 0;
    }

    /// <summary>把整块虚拟屏幕抓成 jpg，给 chat --image 当圈选替身。</summary>
    private static int Windows(string[] args)
    {
        var includeHidden = args.Contains("--hidden");
        var filter = args.FirstOrDefault(a => a != "--hidden" && a != "--all");
        var all = NativeWindows.ListVisibleWindows(
            includeUntitled: true,
            includeHidden: includeHidden,
            dedupeByProcess: !args.Contains("--all"));

        Console.WriteLine($"顶层窗口 {all.Count} 个（按进程名去重，含无标题{(includeHidden ? "、含隐藏" : "")}）");
        foreach (var window in all)
        {
            if (filter is not null &&
                !window.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !window.Title.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var bounds = NativeWindows.WindowBounds(window.Handle);
            Console.WriteLine(
                $"0x{window.Handle:X8}  {window.ProcessName,-28} {bounds.X,6},{bounds.Y,-6} " +
                $"{bounds.Width,5}x{bounds.Height,-5} {(window.Visible ? "可见" : "隐藏")} {(window.IsForeground ? "前台" : "    ")} {window.Title}");
        }

        return 0;
    }

    /// <summary>把文字打进标题匹配的第一个窗口，用来调试主窗口和 insert_text 的底层。</summary>
    private static int TypeInto(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法：type <窗口标题关键字> <文本>");
            return 1;
        }

        var match = NativeWindows.ListVisibleWindows()
            .FirstOrDefault(w => w.Title.Contains(args[0], StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            Console.Error.WriteLine($"找不到标题含「{args[0]}」的窗口");
            return 1;
        }

        if (!NativeWindows.Focus(match.Handle))
        {
            Console.Error.WriteLine($"没能把 {match.ProcessName} 切到前台，一个字符都没发");
            return 1;
        }

        Thread.Sleep(200);
        var typed = NativeWindows.TypeText(args[1]);
        Console.WriteLine($"已向 {match.ProcessName}（{match.Title}）发送 {typed} 个字符");
        return 0;
    }

    private static int Shot(string[] args)
    {
        var outPath = args.FirstOrDefault() ?? "screen.jpg";
        var bounds = ScreenCapture.VirtualScreenBounds();
        var image = ScreenCapture.Capture(bounds);
        File.WriteAllBytes(outPath, image.Bytes);
        Console.WriteLine($"{bounds.Width}x{bounds.Height} → {image.Width}x{image.Height} "
                          + $"{image.Bytes.Length / 1024}KB hash={image.ContentHash[..12]} → {outPath}");
        return 0;
    }

    private static string? TakeOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string Pretty(string json)
    {
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(json)?.ToJsonString(JsonDefaults.Pretty) ?? json;
        }
        catch (Exception)
        {
            return json;
        }
    }

    private static (byte[] Pcm, int SampleRate) ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF")
        {
            throw new InvalidDataException("不是 RIFF/WAV 文件。");
        }

        var sampleRate = BitConverter.ToInt32(bytes, 24);
        var channels = BitConverter.ToInt16(bytes, 22);
        var bits = BitConverter.ToInt16(bytes, 34);

        var offset = 12;
        while (offset + 8 < bytes.Length)
        {
            var id = Encoding.ASCII.GetString(bytes, offset, 4);
            var size = BitConverter.ToInt32(bytes, offset + 4);
            if (id == "data")
            {
                var pcm = new byte[size];
                Array.Copy(bytes, offset + 8, pcm, 0, Math.Min(size, bytes.Length - offset - 8));
                if (channels == 2)
                {
                    pcm = ToMono(pcm);
                }

                if (bits != 16)
                {
                    throw new InvalidDataException($"只支持 16-bit PCM，当前 {bits}-bit。");
                }

                return (pcm, sampleRate);
            }

            offset += 8 + size + (size % 2);
        }

        throw new InvalidDataException("wav 里没有 data 块。");
    }

    private static byte[] ToMono(byte[] stereo)
    {
        var mono = new byte[stereo.Length / 2];
        for (var i = 0; i < mono.Length / 2; i++)
        {
            var left = BitConverter.ToInt16(stereo, i * 4);
            var right = BitConverter.ToInt16(stereo, i * 4 + 2);
            mono[i * 2] = (byte)((left + right) / 2);
            mono[i * 2 + 1] = (byte)(((left + right) / 2) >> 8);
        }

        return mono;
    }

    private static byte[] BuildWav(byte[] pcm, int sampleRate)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcm.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcm.Length);
        writer.Write(pcm);
        return stream.ToArray();
    }
}
