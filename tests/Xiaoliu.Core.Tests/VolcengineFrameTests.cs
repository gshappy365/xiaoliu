using System.Text;
using System.Text.Json.Nodes;
using Xiaoliu.Core.Voice;
using Xunit;

namespace Xiaoliu.Core.Tests;

/// <summary>
/// 火山 openspeech v3 二进制帧的编解码（ADR 0011 语音直连的底座）。
/// 这里错一个字节，ASR/TTS 握手就静默挂死，所以帧布局、序号、gzip、
/// 事件顺序全部在这层锁住 —— 不依赖网络就能验证。
/// </summary>
public sealed class VolcengineFrameTests
{
    [Fact]
    public void Full_request_frame_layout_matches_the_protocol()
    {
        var frame = VolcengineFrame.FullRequest(1, new JsonObject { ["model_name"] = "bigmodel" });

        // 4 字节头：版本|头长 0x11，full request|带序号 0x11，JSON|gzip 0x11，保留 0x00
        Assert.Equal(0x11, frame[0]);
        Assert.Equal(0x11, frame[1]);
        Assert.Equal(0x11, frame[2]);
        Assert.Equal(0x00, frame[3]);

        var parsed = VolcResponseParser.Parse(frame);
        Assert.Equal(VolcengineFrame.ClientFullRequest, parsed.MessageType);
        Assert.Equal(1, parsed.Sequence);
        Assert.False(parsed.IsLast);
        Assert.Equal("bigmodel", parsed.Json!["model_name"]!.GetValue<string>());
    }

    [Fact]
    public void Last_audio_packet_carries_a_negative_sequence()
    {
        var frame = VolcengineFrame.AudioRequest(7, new byte[] { 1, 2, 3, 4 }, isLast: true);

        Assert.Equal(0x23, frame[1]); // audio-only(0x20) | 负序号带序号(0x3)
        var parsed = VolcResponseParser.Parse(frame);
        Assert.True(parsed.IsLast);
        Assert.Equal(-7, parsed.Sequence);
    }

    [Fact]
    public void Regular_audio_packets_keep_positive_sequence()
    {
        var frame = VolcengineFrame.AudioRequest(3, new byte[] { 9, 9 }, isLast: false);

        Assert.Equal(0x21, frame[1]); // audio-only(0x20) | 正向序号(0x1)
        var parsed = VolcResponseParser.Parse(frame);
        Assert.False(parsed.IsLast);
        Assert.Equal(3, parsed.Sequence);
    }

    [Fact]
    public void Server_error_frame_surfaces_the_code()
    {
        // 服务端错误帧：error message type + [code:4][len:4][gzip json]
        var payload = VolcengineFrame.Gzip("""{"message":"凭证无效"}""");
        var frame = new byte[16 + payload.Length];
        frame[0] = 0x11;
        frame[1] = (byte)((VolcengineFrame.ServerErrorResponse << 4) | 0b0001);
        frame[2] = 0x11;
        frame[3] = 0x00;
        WriteInt(frame, 4, 1);              // seq
        WriteInt(frame, 8, -1);             // 错误码
        WriteInt(frame, 12, payload.Length);
        payload.CopyTo(frame, 16);

        var parsed = VolcResponseParser.Parse(frame);
        Assert.True(parsed.IsError);
        Assert.Equal(-1, parsed.ErrorCode);
        Assert.Equal("凭证无效", parsed.Json!["message"]!.GetValue<string>());
    }

    [Fact]
    public void Asr_text_extraction_prefers_result_text_then_utterances()
    {
        var withText = VolcResponseParser.ExtractText(
            (JsonObject)JsonNode.Parse("""{"result":{"text":"你好呀"}}""")!);
        Assert.Equal("你好呀", withText);

        var fromUtterances = VolcResponseParser.ExtractText((JsonObject)JsonNode.Parse(
            """{"result":{"utterances":[{"text":"第一句","definite":true},{"text":"第二句","definite":false}]}}""")!);
        Assert.Equal("第一句第二句", fromUtterances);

        Assert.Equal(string.Empty, VolcResponseParser.ExtractText(null));
    }

    // ---------------------------------------------------------------- TTS 事件帧

    [Fact]
    public void Tts_start_connection_roundtrips()
    {
        var frame = VolcEventFrameCodec.Write(
            VolcEvent.StartConnection, "conn-1", null, new JsonObject());

        Assert.Equal(0x11, frame[0]);
        Assert.Equal(0x14, frame[1]); // full client | with event

        var parsed = VolcEventFrameCodec.Parse(frame);
        Assert.Equal(VolcEvent.StartConnection, parsed.Event);
        Assert.Equal("conn-1", parsed.ConnectionId);
        Assert.Null(parsed.SessionId);
        Assert.False(parsed.SessionFinished);
    }

    [Fact]
    public void Tts_task_request_roundtrips_with_session_id()
    {
        var frame = VolcEventFrameCodec.Write(
            VolcEvent.TaskRequest, null, "session-9",
            new JsonObject { ["event"] = VolcEvent.TaskRequest, ["req_params"] = new JsonObject { ["text"] = "第一句" } });

        var parsed = VolcEventFrameCodec.Parse(frame);
        Assert.Equal(VolcEvent.TaskRequest, parsed.Event);
        Assert.Equal("session-9", parsed.SessionId);
        Assert.Null(parsed.ConnectionId);
        Assert.Equal("第一句", parsed.Json!["req_params"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Tts_audio_frame_is_raw_payload()
    {
        // 音频下行：serialization=none，载荷就是 PCM 本体，不再有 JSON 壳。
        var audio = new byte[] { 0x10, 0x20, 0x30 };
        var frame = BuildAudioFrame(audio, VolcEvent.TtsResponse);

        var parsed = VolcEventFrameCodec.Parse(frame);
        Assert.True(parsed.IsAudio);
        Assert.Equal(VolcEvent.TtsResponse, parsed.Event);
        Assert.Equal(audio, parsed.Audio);
    }

    [Fact]
    public void Session_finished_is_recognized()
    {
        var frame = BuildJsonEventFrame(VolcEvent.SessionFinished, "session-9", new JsonObject());
        Assert.True(VolcEventFrameCodec.Parse(frame).SessionFinished);
    }

    private static byte[] BuildAudioFrame(byte[] audio, int @event)
    {
        // header(4) + event(4) + session id blob + len + payload，serialization=0
        var sessionId = Encoding.UTF8.GetBytes("session-9");
        using var ms = new MemoryStream();
        ms.Write([(byte)0x11, (byte)((VolcengineFrame.ClientFullRequest << 4) | VolcengineFrame.FlagWithEvent), 0x00, 0x00]);
        WriteBlob(ms, @event);
        WriteBlob(ms, sessionId.Length);
        ms.Write(sessionId);
        WriteBlob(ms, audio.Length);
        ms.Write(audio);
        return ms.ToArray();
    }

    private static byte[] BuildJsonEventFrame(int @event, string sessionId, JsonObject payload)
    {
        var payloadBytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var idBytes = Encoding.UTF8.GetBytes(sessionId);
        using var ms = new MemoryStream();
        ms.Write([(byte)0x11, (byte)((VolcengineFrame.ClientFullRequest << 4) | VolcengineFrame.FlagWithEvent), 0x10, 0x00]);
        WriteBlob(ms, @event);
        WriteBlob(ms, idBytes.Length);
        ms.Write(idBytes);
        WriteBlob(ms, payloadBytes.Length);
        ms.Write(payloadBytes);
        return ms.ToArray();
    }

    private static void WriteBlob(MemoryStream ms, int value)
    {
        ms.Write([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
    }

    private static void WriteInt(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }
}
