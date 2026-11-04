using System.Text.Json.Nodes;
using Xiaoliu.Core.Protocol;
using Xiaoliu.Core.Tools;
using Xunit;

namespace Xiaoliu.Core.Tests;

/// <summary>
/// 自定义 OpenAI 兼容端点的翻译层（ADR 0011，10B 决策：能力与方舟组对齐）。
/// 工具、圈选图、工具回执在两种协议形状之间的翻译，每一处都锁进测试 ——
/// 这里译错一个字段，模型拿到的是自相矛盾的历史。
/// </summary>
public sealed class OpenAiChatClientTests
{
    private static ChatRequest Request(JsonArray messages, bool withTools = false) => new()
    {
        Model = "deepseek-chat",
        System = "你是小六",
        Messages = messages,
        ReasoningEffort = "medium",
        Tools = withTools
            ? new[] { new ToolDefinition("capture_screen", "抓屏", """{"type":"object"}""") }
            : null,
    };

    // ---------------------------------------------------------------- 端点与请求体

    [Theory]
    [InlineData("https://api.deepseek.com/v1", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://api.deepseek.com/v1/", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://gw.example.org/api/chat/completions", "https://gw.example.org/api/chat/completions")]
    public void Base_url_gets_the_completions_suffix(string baseUrl, string expected)
    {
        Assert.Equal(expected, OpenAiChatClient.BuildEndpoint(baseUrl));
    }

    [Fact]
    public void Empty_base_url_is_a_configuration_error_not_a_silent_failure()
    {
        Assert.Throws<InvalidOperationException>(() => OpenAiChatClient.BuildEndpoint("  "));
    }

    [Fact]
    public void Body_translates_tools_and_reasoning()
    {
        var body = JsonNode.Parse(OpenAiChatClient.BuildBody(
            Request([new JsonObject { ["role"] = "user", ["content"] = "hi" }], withTools: true)))!;

        Assert.Equal("deepseek-chat", body["model"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal("medium", body["reasoning_effort"]!.GetValue<string>());

        var tool = body["tools"]![0]!.AsObject();
        Assert.Equal("function", tool["type"]!.GetValue<string>());
        Assert.Equal("capture_screen", tool["function"]!["name"]!.GetValue<string>());
        Assert.Equal("auto", body["tool_choice"]!.GetValue<string>());
    }

    [Fact]
    public void System_prompt_becomes_the_system_message()
    {
        var messages = OpenAiChatClient.TranslateMessages(
            "你是小六", [new JsonObject { ["role"] = "user", ["content"] = "hi" }]);

        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("你是小六", messages[0]!["content"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- 消息翻译

    [Fact]
    public void Image_blocks_become_data_urls()
    {
        var messages = OpenAiChatClient.TranslateMessages(null,
        [
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = "image/jpeg",
                            ["data"] = "QUJD",
                        },
                    },
                    new JsonObject { ["type"] = "text", ["text"] = "这是什么" },
                },
            }
        ]);

        var parts = messages[0]!["content"]!.AsArray();
        Assert.Equal("image_url", parts[0]!["type"]!.GetValue<string>());
        Assert.Equal("data:image/jpeg;base64,QUJD", parts[0]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal("这是什么", parts[1]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Tool_loop_translates_to_function_calling()
    {
        // 一轮完整的工具回环：assistant(tool_use) → user(tool_result) → 继续对话。
        var messages = OpenAiChatClient.TranslateMessages(null,
        [
            new JsonObject { ["role"] = "user", ["content"] = "看看屏幕" },
            new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = "我抓一下" },
                    new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = "call_1",
                        ["name"] = "capture_screen",
                        ["input"] = new JsonObject { ["source"] = "current" },
                    },
                },
            },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = "call_1",
                        ["content"] = "已抓取当前屏幕",
                    },
                },
            },
        ]);

        var assistant = messages[1]!.AsObject();
        Assert.Equal("我抓一下", assistant["content"]!.GetValue<string>());
        Assert.Equal("capture_screen", assistant["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("call_1", assistant["tool_calls"]![0]!["id"]!.GetValue<string>());

        var toolMessage = messages[2]!.AsObject();
        Assert.Equal("tool", toolMessage["role"]!.GetValue<string>());
        Assert.Equal("call_1", toolMessage["tool_call_id"]!.GetValue<string>());
        Assert.Equal("已抓取当前屏幕", toolMessage["content"]!.GetValue<string>());
    }

    [Fact]
    public void Images_inside_tool_results_move_to_the_next_user_message()
    {
        // capture_screen 的回执是 [text, image] 块数组；OpenAI 没有工具图片承载位，
        // 图必须补发到紧随其后的 user 消息里，否则模型永远「看不到」自己抓的图。
        var messages = OpenAiChatClient.TranslateMessages(null,
        [
            new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = "call_1",
                        ["name"] = "capture_screen",
                        ["input"] = new JsonObject(),
                    },
                },
            },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = "call_1",
                        ["content"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "text", ["text"] = "回执文字" },
                            new JsonObject
                            {
                                ["type"] = "image",
                                ["source"] = new JsonObject
                                {
                                    ["type"] = "base64",
                                    ["media_type"] = "image/jpeg",
                                    ["data"] = "WFla",
                                },
                            },
                        },
                    },
                },
            },
        ]);

        Assert.Equal("tool", messages[1]!["role"]!.GetValue<string>());
        Assert.Equal("回执文字", messages[1]!["content"]!.GetValue<string>());

        var imageMessage = messages[2]!.AsObject();
        Assert.Equal("user", imageMessage["role"]!.GetValue<string>());
        var parts = imageMessage["content"]!.AsArray();
        Assert.Contains("工具回执附图", parts[0]!["text"]!.GetValue<string>());
        Assert.Equal("data:image/jpeg;base64,WFla", parts[1]!["image_url"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void Thinking_blocks_are_dropped_from_history()
    {
        var messages = OpenAiChatClient.TranslateMessages(null,
        [
            new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "thinking", ["thinking"] = "内心戏" },
                    new JsonObject { ["type"] = "text", ["text"] = "答案" },
                },
            },
        ]);

        Assert.Equal("答案", messages[0]!["content"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- 流式拼装

    private const string ToolCallStream =
        "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"我看一下\"}}]}\n\n"
        + "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_9\",\"function\":{\"name\":\"capture_screen\",\"arguments\":\"{\\\"sou\"}}]}}]}\n\n"
        + "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"rce\\\":\\\"current\\\"}\"}}]}}]}\n\n"
        + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n"
        + "data: [DONE]\n\n";

    [Fact]
    public void Stream_fragments_reassemble_into_anthropic_blocks()
    {
        var accumulator = new OpenAiStreamAccumulator();
        foreach (var frame in ParseFrames(ToolCallStream))
        {
            accumulator.Apply(frame, out _);
        }

        var round = accumulator.Build();
        Assert.Equal("我看一下", round.Text);
        Assert.Equal("tool_use", round.StopReason);

        var call = Assert.Single(round.ToolCalls);
        Assert.Equal("call_9", call.Id);
        Assert.Equal("capture_screen", call.Name);
        Assert.Equal("current", JsonNode.Parse(call.ArgumentsJson)!["source"]!.GetValue<string>());

        // Content 也是 Anthropic 形状，Agent 才能不加区分地回填历史。
        Assert.Equal("text", round.Content[0]!["type"]!.GetValue<string>());
        Assert.Equal("tool_use", round.Content[1]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Error_chunk_becomes_a_protocol_exception()
    {
        var accumulator = new OpenAiStreamAccumulator();
        var frames = ParseFrames("data: {\"error\":{\"message\":\"余额不足\"}}\n\n");

        Assert.Throws<ChatProtocolException>(() => accumulator.Apply(frames[0], out _));
    }

    [Fact]
    public void Plain_text_stream_builds_end_turn()
    {
        var accumulator = new OpenAiStreamAccumulator();
        foreach (var frame in ParseFrames(
                     "data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n\n"
                     + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
                     + "data: [DONE]\n\n"))
        {
            accumulator.Apply(frame, out _);
        }

        var round = accumulator.Build();
        Assert.Equal("end_turn", round.StopReason);
        Assert.Empty(round.ToolCalls);
        Assert.Equal("你好", round.Text);
    }

    private static List<SseFrame> ParseFrames(string wire)
    {
        var reader = new SseReader();
        var frames = reader.Push(wire).ToList();
        reader.Complete();
        return frames;
    }
}
