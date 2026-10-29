using System.Text.Json.Nodes;
using Xiaoliu.Core.Protocol;
using Xunit;

namespace Xiaoliu.Core.Tests;

public class SseReaderTests
{
    private const string TextStream =
        "event: message_start\n"
        + "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"role\":\"assistant\",\"content\":[],\"usage\":{\"input_tokens\":12,\"output_tokens\":0}}}\n\n"
        + "event: content_block_start\n"
        + "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n"
        + "event: content_block_delta\n"
        + "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"你好\"}}\n\n"
        + "event: content_block_stop\n"
        + "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
        + "event: message_delta\n"
        + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":3}}\n\n"
        + "event: message_stop\n"
        + "data: {\"type\":\"message_stop\"}\n\n"
        + "data: [DONE]\n\n";

    [Fact]
    public void ParsesWholeStreamAndStopsAtDone()
    {
        var reader = new SseReader();
        var frames = reader.Push(TextStream);

        Assert.True(reader.IsFinished);
        Assert.Equal(7, frames.Count);
        Assert.True(frames[^1].IsDone);
        Assert.Equal("message_start", frames[0].Event);
    }

    [Fact]
    public void ReassemblesFramesSplitAcrossChunks()
    {
        var reader = new SseReader();
        var frames = new List<SseFrame>();

        // 每 7 个字符喂一次：必然把 data 行、甚至 JSON 对象切在中间。
        for (var i = 0; i < TextStream.Length; i += 7)
        {
            frames.AddRange(reader.Push(TextStream.Substring(i, Math.Min(7, TextStream.Length - i))));
        }

        Assert.True(reader.IsFinished);
        Assert.Equal(7, frames.Count);
        Assert.Equal("end_turn", frames[4].Json!["delta"]!["stop_reason"]!.GetValue<string>());
    }

    [Fact]
    public void AccumulatesTextAndToolUse()
    {
        var accumulator = new ChatStreamAccumulator();
        var reader = new SseReader();
        var stream =
            "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n"
            + "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"我来\"}}\n\n"
            + "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"看看\"}}\n\n"
            + "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
            + "data: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"point_at_screen\",\"input\":{}}}\n\n"
            + "data: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"x\\\":100,\"}}\n\n"
            + "data: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"\\\"y\\\":200}\"}}\n\n"
            + "data: {\"type\":\"content_block_stop\",\"index\":1}\n\n"
            + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"}}\n\n"
            + "data: [DONE]\n\n";

        string? lastText = null;
        foreach (var frame in reader.Push(stream))
        {
            if (accumulator.Apply(frame, out var accumulated))
            {
                lastText = accumulated;
            }
        }

        var result = accumulator.Build();
        Assert.Equal("我来看看", lastText);
        Assert.Equal("tool_use", result.StopReason);
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("point_at_screen", call.Name);
        Assert.Equal("toolu_1", call.Id);

        var input = JsonNode.Parse(call.ArgumentsJson)!.AsObject();
        Assert.Equal(100, input["x"]!.GetValue<int>());
        Assert.Equal(200, input["y"]!.GetValue<int>());
    }

    [Fact]
    public void ErrorFrameBecomesProtocolException()
    {
        var accumulator = new ChatStreamAccumulator();
        var frame = new SseFrame("error", "{}", JsonNode.Parse(
            "{\"type\":\"error\",\"error\":{\"type\":\"api_error\",\"message\":\"上游超时\"}}")!.AsObject(), false);

        var ex = Assert.Throws<ChatProtocolException>(() => accumulator.Apply(frame, out _));
        Assert.Contains("上游超时", ex.Message);
    }
}
