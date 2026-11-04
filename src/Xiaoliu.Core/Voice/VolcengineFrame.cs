using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace Xiaoliu.Core.Voice;

/// <summary>
/// 火山引擎语音网关（openspeech）v3 二进制帧协议的编解码。
///
/// 帧布局（4 字节头 + 按需扩展）：
/// <code>
/// byte0 = (protocol_version &lt;&lt; 4) | header_size      // 0x11
/// byte1 = (message_type &lt;&lt; 4) | flags                 // 类型在高位
/// byte2 = (serialization &lt;&lt; 4) | compression
/// byte3 = 0x00（保留）
/// [seq: i32]      flags 位 0 时
/// [len: u32][gzip 载荷]
/// </code>
///
/// ASR（saas/bigmodel）与 TTS（bidirection）共用这层；TTS 的事件帧在 flags 位 2
/// 额外带一个 i32 event，载荷前还有 session/connection id，见
/// <see cref="VolcEventFrame"/>。协议常量与官方 demo 一致，出处见 ADR 0011。
/// </summary>
public static class VolcengineFrame
{
    public const byte ProtocolVersion = 0b0001;
    public const byte HeaderSize = 0b0001;

    // message type
    public const byte ClientFullRequest = 0b0001;
    public const byte ClientAudioOnlyRequest = 0b0010;
    public const byte ServerFullResponse = 0b1001;
    public const byte ServerAck = 0b1011;
    public const byte ServerErrorResponse = 0b1111;

    // flags
    public const byte FlagNoSequence = 0b0000;
    public const byte FlagPosSequence = 0b0001;
    public const byte FlagNegSequence = 0b0010;          // 只置末包位（不带 seq）
    public const byte FlagNegWithSequence = 0b0011;      // 带 seq 的末包
    public const byte FlagWithEvent = 0b0100;            // TTS 双向协议的事件位

    // serialization / compression
    public const byte SerialNone = 0b0000;
    public const byte SerialJson = 0b0001;
    public const byte CompressionNone = 0b0000;
    public const byte CompressionGzip = 0b0001;

    public static byte[] Header(byte messageType, byte flags, byte serialization, byte compression)
    {
        return
        [
            (byte)((ProtocolVersion << 4) | HeaderSize),
            (byte)((messageType << 4) | flags),
            (byte)((serialization << 4) | compression),
            0x00,
        ];
    }

    /// <summary>ASR 首包：完整客户端请求（JSON + gzip + 正向序号）。</summary>
    public static byte[] FullRequest(int sequence, JsonObject payload)
    {
        var body = Gzip(payload.ToJsonString());
        return Build(ClientFullRequest, FlagPosSequence, SerialJson, CompressionGzip, sequence, body);
    }

    /// <summary>ASR 音频包。<paramref name="isLast"/> 用负序号收口（空载荷也走这里）。</summary>
    public static byte[] AudioRequest(int sequence, ReadOnlySpan<byte> pcm, bool isLast)
    {
        var flags = isLast ? FlagNegWithSequence : FlagPosSequence;
        var seq = isLast ? -Math.Abs(sequence) : Math.Abs(sequence);
        return Build(ClientAudioOnlyRequest, flags, SerialNone, CompressionGzip, seq, Gzip(pcm));
    }

    private static byte[] Build(byte messageType, byte flags, byte serialization, byte compression, int sequence, byte[] body)
    {
        var frame = new byte[HeaderSize * 4 + 4 + 4 + body.Length];
        Header(messageType, flags, serialization, compression).CopyTo(frame, 0);
        WriteInt(frame, 4, sequence);
        WriteInt(frame, 8, body.Length);
        body.CopyTo(frame, 12);
        return frame;
    }

    internal static void WriteInt(Span<byte> target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    internal static int ReadInt(ReadOnlySpan<byte> source, int offset) =>
        (source[offset] << 24) | (source[offset + 1] << 16) | (source[offset + 2] << 8) | source[offset + 3];

    public static byte[] Gzip(ReadOnlySpan<byte> payload)
    {
        using var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(payload);
        }

        return ms.ToArray();
    }

    public static byte[] Gzip(string payload) => Gzip(Encoding.UTF8.GetBytes(payload));

    public static byte[] Ungzip(ReadOnlySpan<byte> payload)
    {
        using var input = new MemoryStream(payload.ToArray());
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}

/// <summary>ASR 一帧服务端响应：序号、末包位、错误码与解出的 JSON 载荷。</summary>
public sealed record VolcResponseFrame(
    byte MessageType,
    int Sequence,
    bool IsLast,
    int ErrorCode,
    JsonObject? Json)
{
    public bool IsError => ErrorCode != 0;
}

/// <summary>TTS 双向协议的一帧：event + 可选 session/connection id + 载荷。</summary>
public sealed record VolcEventFrame(
    int Event,
    string? SessionId,
    string? ConnectionId,
    byte MessageType,
    bool SessionFinished,
    byte[]? Audio,
    JsonObject? Json)
{
    public bool IsAudio => Audio is { Length: > 0 };
}

/// <summary>TTS 双向协议的事件号（与官方 SDK constants.py 一致）。</summary>
public static class VolcEvent
{
    public const int StartConnection = 1;
    public const int FinishConnection = 2;
    public const int ConnectionStarted = 50;
    public const int ConnectionFailed = 51;
    public const int ConnectionFinished = 52;
    public const int StartSession = 100;
    public const int FinishSession = 102;
    public const int SessionStarted = 150;
    public const int SessionFinished = 152;
    public const int SessionFailed = 153;
    public const int TaskRequest = 200;
    public const int TtsResponse = 352;
}

public static class VolcEventFrameCodec
{
    public const string Namespace = "BidirectionalTTS";

    /// <summary>事件帧：header + [event][conn_id?][session_id?][len][payload]。写法与官方 _write_message 对齐。</summary>
    public static byte[] Write(int @event, string? connectionId, string? sessionId, JsonObject payload)
    {
        var payloadBytes = Encoding.UTF8.GetBytes(payload.ToJsonString());

        using var ms = new MemoryStream();
        ms.WriteByte((byte)((VolcengineFrame.ProtocolVersion << 4) | VolcengineFrame.HeaderSize));
        ms.WriteByte((byte)((VolcengineFrame.ClientFullRequest << 4) | VolcengineFrame.FlagWithEvent));
        ms.WriteByte((byte)((VolcengineFrame.SerialJson << 4) | VolcengineFrame.CompressionNone));
        ms.WriteByte(0x00);

        WriteBlob(ms, @event);
        if (connectionId is not null)
        {
            WriteBlob(ms, connectionId);
        }

        if (sessionId is not null)
        {
            WriteBlob(ms, sessionId);
        }

        WriteBlob(ms, payloadBytes);
        return ms.ToArray();
    }

    /// <summary>解析 TTS 双向协议的下行帧。音频帧的载荷原样给出（无序列化）。</summary>
    public static VolcEventFrame Parse(byte[] data)
    {
        if (data.Length < 4)
        {
            throw new InvalidOperationException("火山 TTS 帧过短，无法解析。");
        }

        var headerSize = data[0] & 0x0F;
        var messageType = (byte)(data[1] >> 4);
        var flags = (byte)(data[1] & 0x0F);
        var serialization = (byte)(data[2] >> 4);
        var compression = (byte)(data[2] & 0x0F);
        var offset = headerSize * 4;

        var @event = 0;
        string? sessionId = null;
        string? connectionId = null;
        if ((flags & VolcengineFrame.FlagWithEvent) != 0)
        {
            @event = VolcengineFrame.ReadInt(data, offset);
            offset += 4;
            if (@event is not (VolcEvent.StartConnection or VolcEvent.FinishConnection
                               or VolcEvent.ConnectionStarted or VolcEvent.ConnectionFailed
                               or VolcEvent.ConnectionFinished))
            {
                var sessionIdLen = VolcengineFrame.ReadInt(data, offset);
                offset += 4;
                sessionId = Encoding.UTF8.GetString(data, offset, sessionIdLen);
                offset += sessionIdLen;
            }
            else
            {
                var connectionIdLen = VolcengineFrame.ReadInt(data, offset);
                offset += 4;
                connectionId = Encoding.UTF8.GetString(data, offset, connectionIdLen);
                offset += connectionIdLen;
            }
        }

        var payloadLen = VolcengineFrame.ReadInt(data, offset);
        offset += 4;
        var payload = data.AsSpan(offset, Math.Max(0, Math.Min(payloadLen, data.Length - offset)));

        if (compression == VolcengineFrame.CompressionGzip)
        {
            payload = VolcengineFrame.Ungzip(payload);
        }

        byte[]? audio = null;
        JsonObject? json = null;
        if (serialization == VolcengineFrame.SerialNone)
        {
            audio = payload.ToArray();
        }
        else if (serialization == VolcengineFrame.SerialJson && payload.Length > 0)
        {
            json = JsonNode.Parse(Encoding.UTF8.GetString(payload)) as JsonObject;
        }

        return new VolcEventFrame(
            @event,
            sessionId,
            connectionId,
            messageType,
            @event == VolcEvent.SessionFinished,
            audio,
            json);
    }

    private static void WriteBlob(MemoryStream ms, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        VolcengineFrame.WriteInt(buffer, 0, value);
        ms.Write(buffer);
    }

    private static void WriteBlob(MemoryStream ms, string value) =>
        WriteBlob(ms, Encoding.UTF8.GetBytes(value));

    private static void WriteBlob(MemoryStream ms, byte[] bytes)
    {
        WriteBlob(ms, bytes.Length);
        ms.Write(bytes);
    }
}

public static class VolcResponseParser
{
    /// <summary>解析 ASR 的下行帧（full/ack/error 三种 message type）。</summary>
    public static VolcResponseFrame Parse(byte[] data)
    {
        if (data.Length < 4)
        {
            throw new InvalidOperationException("火山 ASR 帧过短，无法解析。");
        }

        var headerSize = data[0] & 0x0F;
        var messageType = (byte)(data[1] >> 4);
        var flags = (byte)(data[1] & 0x0F);
        var serialization = (byte)(data[2] >> 4);
        var compression = (byte)(data[2] & 0x0F);
        var offset = headerSize * 4;

        var sequence = 0;
        var isLast = false;
        var errorCode = 0;

        if ((flags & 0b0001) != 0 && offset + 4 <= data.Length)
        {
            sequence = VolcengineFrame.ReadInt(data, offset);
            offset += 4;
        }

        if ((flags & 0b0010) != 0)
        {
            isLast = true;
        }

        ReadOnlySpan<byte> payload;
        switch (messageType)
        {
            case VolcengineFrame.ServerErrorResponse:
                errorCode = offset + 4 <= data.Length ? VolcengineFrame.ReadInt(data, offset) : -1;
                offset += 4;
                if (offset + 4 <= data.Length)
                {
                    var len = VolcengineFrame.ReadInt(data, offset);
                    offset += 4;
                    payload = data.AsSpan(offset, Math.Max(0, Math.Min(len, data.Length - offset)));
                }
                else
                {
                    payload = ReadOnlySpan<byte>.Empty;
                }

                break;
            case VolcengineFrame.ServerFullResponse:
            case VolcengineFrame.ServerAck:
            case VolcengineFrame.ClientFullRequest:      // 回环测试：客户端帧同样带长度前缀
            case VolcengineFrame.ClientAudioOnlyRequest:
                if (offset + 4 <= data.Length)
                {
                    var len = VolcengineFrame.ReadInt(data, offset);
                    offset += 4;
                    payload = data.AsSpan(offset, Math.Max(0, Math.Min(len, data.Length - offset)));
                }
                else
                {
                    payload = ReadOnlySpan<byte>.Empty;
                }

                break;
            default:
                payload = data.AsSpan(Math.Min(offset, data.Length));
                break;
        }

        if (compression == VolcengineFrame.CompressionGzip && payload.Length > 0)
        {
            payload = VolcengineFrame.Ungzip(payload);
        }

        JsonObject? json = null;
        if (serialization == VolcengineFrame.SerialJson && payload.Length > 0)
        {
            json = JsonNode.Parse(Encoding.UTF8.GetString(payload)) as JsonObject;
        }

        return new VolcResponseFrame(messageType, sequence, isLast, errorCode, json);
    }

    /// <summary>从 ASR 载荷里取累计识别文本：优先 result.text，退化到拼接 utterances。</summary>
    public static string ExtractText(JsonObject? payload)
    {
        if (payload?["result"] is JsonObject result)
        {
            if (result["text"] is JsonNode text && text.GetValue<string>().Trim() is { Length: > 0 } value)
            {
                return value.Trim();
            }

            if (result["utterances"] is JsonArray utterances)
            {
                var builder = new StringBuilder();
                foreach (var utterance in utterances)
                {
                    var piece = utterance?["text"]?.GetValue<string>()?.Trim();
                    if (!string.IsNullOrEmpty(piece))
                    {
                        builder.Append(piece);
                    }
                }

                return builder.ToString();
            }
        }

        return payload?["text"]?.GetValue<string>()?.Trim() ?? string.Empty;
    }
}
