using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xiaoliu.Core.Protocol;

/// <summary>
/// 增量 SSE 解析器：喂字符串片段，吐出完整帧。
///
/// 帧以空行结束（<c>\n\n</c>），网络分片可能把一帧切成两半，所以必须自己缓冲。
/// 这不是通用 SSE 实现 —— 只保留 <c>event:</c> 与 <c>data:</c>，注释与 id 丢弃，
/// 因为服务端只发这两种（见 rea/server/app/protocol.py）。
/// </summary>
public sealed class SseReader
{
    private readonly StringBuilder _buffer = new();
    private string? _event;
    private readonly StringBuilder _data = new();

    /// <summary>是否已读到 <c>data: [DONE]</c>。客户端循环靠它退出。</summary>
    public bool IsFinished { get; private set; }

    public IReadOnlyList<SseFrame> Push(string chunk)
    {
        if (string.IsNullOrEmpty(chunk) || IsFinished)
        {
            return Array.Empty<SseFrame>();
        }

        _buffer.Append(chunk);
        var frames = new List<SseFrame>();

        while (true)
        {
            var raw = _buffer.ToString();
            var end = FindFrameEnd(raw);
            if (end < 0)
            {
                break;
            }

            var block = raw[..end];
            var consumed = SkipSeparator(raw, end);
            _buffer.Clear();
            _buffer.Append(raw, consumed, raw.Length - consumed);

            var frame = ParseBlock(block);
            if (frame is not null)
            {
                frames.Add(frame);
                if (frame.IsDone)
                {
                    IsFinished = true;
                    _buffer.Clear();
                    break;
                }
            }
        }

        return frames;
    }

    /// <summary>流结束时把残留的不完整帧丢掉（不完整帧本来就不可信）。</summary>
    public void Complete() => IsFinished = true;

    private static int FindFrameEnd(string text)
    {
        var lf = text.IndexOf("\n\n", StringComparison.Ordinal);
        var crlf = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (lf < 0) return crlf;
        if (crlf < 0) return lf;
        return Math.Min(lf, crlf);
    }

    private static int SkipSeparator(string text, int end)
    {
        if (end + 1 < text.Length && text[end] == '\r' && text[end + 1] == '\n')
        {
            return end + 4;
        }

        return end + 2;
    }

    private SseFrame? ParseBlock(string block)
    {
        _event = null;
        _data.Clear();

        foreach (var line in block.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0 || trimmed[0] == ':')
            {
                continue;
            }

            var colon = trimmed.IndexOf(':');
            var field = colon < 0 ? trimmed : trimmed[..colon];
            var value = colon < 0 ? string.Empty : trimmed[(colon + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            switch (field)
            {
                case "event":
                    _event = value;
                    break;
                case "data":
                    if (_data.Length > 0)
                    {
                        _data.Append('\n');
                    }

                    _data.Append(value);
                    break;
            }
        }

        var data = _data.ToString();
        if (data.Length == 0 && _event is null)
        {
            return null;
        }

        var isDone = data.Trim() == WireProtocol.DoneMarker;
        JsonObject? json = null;
        if (!isDone && data.Length > 0)
        {
            try
            {
                json = JsonNode.Parse(data) as JsonObject;
            }
            catch (JsonException)
            {
                // 坏帧不致命：上游偶尔会发半截 JSON，跳过比整轮失败好。
                return new SseFrame(_event, data, null, false);
            }
        }

        return new SseFrame(_event ?? json?["type"]?.GetValue<string>(), data, json, isDone);
    }
}

public sealed record SseFrame(string? Event, string Data, JsonObject? Json, bool IsDone);
