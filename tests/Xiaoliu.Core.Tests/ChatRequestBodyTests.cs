using System.Text.Json.Nodes;
using Xiaoliu.Core.Configuration;
using Xiaoliu.Core.Protocol;
using Xiaoliu.Core.Tools;
using Xunit;

namespace Xiaoliu.Core.Tests;

/// <summary>
/// 云直连后的方舟请求体（ADR 0011）：就是干净的 Anthropic Messages。
/// 曾经借道服务器的私有字段（vision_capable / main_reasoning_effort / diandian_credits）
/// 一个都不能再出现；推理强度换成协议原生的 thinking 预算，映射在这里钉住。
/// </summary>
public sealed class ChatRequestBodyTests
{
    private static ChatRequest Request(string effort = "medium", int maxTokens = 8192) => new()
    {
        Model = "doubao-seed-2-1-pro-260915",
        System = "sys",
        Messages = [new JsonObject { ["role"] = "user", ["content"] = "你好" }],
        MaxTokens = maxTokens,
        ReasoningEffort = effort,
    };

    [Fact]
    public void Body_carries_no_private_server_fields_anymore()
    {
        var body = JsonNode.Parse(ArkChatClient.BuildBody(Request()))!;

        Assert.Null(body["vision_capable"]);
        Assert.Null(body["main_reasoning_effort"]);
        Assert.Null(body["diandian_credits"]);
    }

    [Fact]
    public void Reasoning_effort_maps_to_thinking_budget()
    {
        Assert.Equal(1024, ArkChatClient.ThinkingBudget("low"));
        Assert.Equal(4096, ArkChatClient.ThinkingBudget("medium"));
        Assert.Equal(8191, ArkChatClient.ThinkingBudget("high"));

        var body = JsonNode.Parse(ArkChatClient.BuildBody(Request("low")))!;
        var thinking = body["thinking"]!.AsObject();
        Assert.Equal("enabled", thinking["type"]!.GetValue<string>());
        Assert.Equal(1024, thinking["budget_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void Thinking_budget_never_reaches_max_tokens()
    {
        // 无工具轮 max_tokens=1024，medium 预算 4096 装不下：max_tokens 抬到预算 +1024。
        var body = JsonNode.Parse(ArkChatClient.BuildBody(Request("medium", maxTokens: 1024)))!;

        Assert.True(body["max_tokens"]!.GetValue<int>() > 4096);
        Assert.Equal(4096, body["thinking"]!["budget_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void Tools_keep_anthropic_shape()
    {
        var request = new ChatRequest
        {
            Model = "doubao-seed-2-1-pro-260915",
            System = "sys",
            Messages = [new JsonObject { ["role"] = "user", ["content"] = "hi" }],
            Tools = new[] { new ToolDefinition("capture_screen", "抓屏", """{"type":"object"}""") },
        };

        var body = JsonNode.Parse(ArkChatClient.BuildBody(request))!;
        var tool = body["tools"]![0]!.AsObject();

        Assert.Equal("capture_screen", tool["name"]!.GetValue<string>());
        Assert.NotNull(tool["input_schema"]);
        Assert.Null(tool["function"]); // OpenAI 形状不允许出现在方舟请求里
    }

    [Fact]
    public void Vision_catalog_still_separates_the_two_groups()
    {
        // 图片拒绝策略（ADR 0011）靠这份清单：视觉组 true，纯文本组 false。
        Assert.True(ModelCatalog.Vision.Count > 0);
        Assert.All(ModelCatalog.Vision, m => Assert.True(m.Vision, m.Id));
        Assert.All(ModelCatalog.TextOnly, m => Assert.False(m.Vision, m.Id));
    }
}
