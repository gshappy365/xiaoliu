using System.Text.Json.Nodes;
using Xiaoliu.Core.Configuration;
using Xiaoliu.Core.Protocol;
using Xiaoliu.Core.Tools;
using Xunit;

namespace Xiaoliu.Core.Tests;

/// <summary>
/// 双连接模式（ADR 0012）的行为断言。
///
/// 重点是「本地服务器模式必须发服务器端读的那几个私有字段」——
/// 漏了 <c>vision_capable</c>，图片会被降级成文字描述；
/// 漏了 <c>main_reasoning_effort</c>，思考预算退回默认值。
/// </summary>
public sealed class DualConnectionTests
{
    private static ChatRequest Request(string? effort = "medium", bool vision = true) => new()
    {
        Model = "doubao-seed-2-1-pro-260915",
        System = "你是小六",
        Messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "你好" }),
        MaxTokens = 8192,
        ReasoningEffort = effort,
        VisionCapable = vision,
    };

    // ------------------------------------------------------------- 模式分派

    [Fact]
    public void 本地服务器模式_造出本地服务器传输()
    {
        var settings = new AppSettings { ConnectionMode = AppSettings.ConnectionModeLocalServer };

        var transport = ChatTransportFactory.Create(settings);

        Assert.IsType<LocalServerChatClient>(transport);
    }

    [Fact]
    public void 云直连模式_造出方舟传输()
    {
        var settings = new AppSettings { ConnectionMode = AppSettings.ConnectionModeCloud };

        var transport = ChatTransportFactory.Create(settings);

        Assert.IsType<ArkChatClient>(transport);
    }

    [Fact]
    public void 云直连加自定义模型_造出自定义端点传输()
    {
        var settings = new AppSettings
        {
            ConnectionMode = AppSettings.ConnectionModeCloud,
            Model = AppSettings.CustomModelKey,
            CustomBaseUrl = "https://api.deepseek.com/v1",
        };

        var transport = ChatTransportFactory.Create(settings);

        Assert.IsType<OpenAiChatClient>(transport);
    }

    [Fact]
    public void 本地服务器模式_盖过自定义模型设置()
    {
        // 本地服务器的 chat 端点本身就是 Anthropic 协议，不走 OpenAI 翻译层。
        var settings = new AppSettings
        {
            ConnectionMode = AppSettings.ConnectionModeLocalServer,
            Model = AppSettings.CustomModelKey,
            CustomBaseUrl = "https://api.deepseek.com/v1",
        };

        var transport = ChatTransportFactory.Create(settings);

        Assert.IsType<LocalServerChatClient>(transport);
    }

    [Fact]
    public void 未知模式_兜回云直连()
    {
        var settings = new AppSettings { ConnectionMode = "不存在的模式" };

        settings.Normalize();

        Assert.Equal(AppSettings.ConnectionModeCloud, settings.ConnectionMode);
        Assert.True(settings.IsCloudDirectMode);
        Assert.False(settings.IsLocalServerMode);
    }

    // ------------------------------------------------------------- 请求体

    [Fact]
    public void 本地服务器模式_发服务器端读的私有字段()
    {
        var body = JsonNode.Parse(LocalServerChatClient.BuildBody(Request("high")))!;

        // 服务器端 rewrite_request 读它映射上游 thinking 预算
        Assert.Equal("high", body["main_reasoning_effort"]?.GetValue<string>());

        // 服务器端读它决定图片原样透传还是跑 VL/OCR 转文本
        Assert.True(body["vision_capable"]?.GetValue<bool>());

        // 点点计费字段，xiaoliu 没有这套计费，不发
        Assert.Null(body["diandian_credits"]);
    }

    [Fact]
    public void 本地服务器模式_不发协议原生的thinking()
    {
        // 思考预算由服务器端按 main_reasoning_effort 统一决定，两边都设会打架。
        var body = JsonNode.Parse(LocalServerChatClient.BuildBody(Request("high")))!;

        Assert.Null(body["thinking"]);
    }

    [Fact]
    public void 本地服务器模式_纯文本模型不发vision_capable()
    {
        var body = JsonNode.Parse(LocalServerChatClient.BuildBody(Request("medium", vision: false)))!;

        Assert.Null(body["vision_capable"]);
    }

    [Fact]
    public void 本地服务器模式_不发推理强度时不带该字段()
    {
        var body = JsonNode.Parse(LocalServerChatClient.BuildBody(Request(null)))!;

        Assert.Null(body["main_reasoning_effort"]);
    }

    [Fact]
    public void 云直连模式_用协议原生的thinking不发私有字段()
    {
        var body = JsonNode.Parse(ArkChatClient.BuildBody(Request("high")))!;

        Assert.Equal("enabled", body["thinking"]?["type"]?.GetValue<string>());
        Assert.Null(body["main_reasoning_effort"]);
        Assert.Null(body["vision_capable"]);
    }

    [Fact]
    public void 本地服务器模式_带工具定义()
    {
        var withTools = new ChatRequest
        {
            Model = "doubao-seed-2-1-pro-260915",
            System = "你是小六",
            Messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "你好" }),
            MaxTokens = 8192,
            ReasoningEffort = "medium",
            VisionCapable = true,
            Tools = new[] { ToolCatalog.All[0] },
        };

        var body = JsonNode.Parse(LocalServerChatClient.BuildBody(withTools))!;

        Assert.NotNull(body["tools"]);
        Assert.Single(body["tools"]!.AsArray());
    }

    // ------------------------------------------------------------- 端点换算

    [Fact]
    public void WebSocket地址_把proxy前缀换成ws根()
    {
        // 服务器端 HTTP 挂在 /diandian/proxy/*，语音中继挂在 /diandian/ws/*
        var baseUri = new Uri("https://127.0.0.1/diandian/proxy/");

        var uri = WireProtocol.WebSocketUri(baseUri, WireProtocol.AsrPath);

        Assert.Equal("wss", uri.Scheme);
        Assert.Equal("/diandian/ws/asr/volcengine", uri.AbsolutePath);
    }

    [Fact]
    public void WebSocket地址_非默认端口保留()
    {
        var baseUri = new Uri("https://127.0.0.1:8443/diandian/proxy/");

        var uri = WireProtocol.WebSocketUri(baseUri, WireProtocol.TtsPath);

        Assert.Equal(8443, uri.Port);
        Assert.Equal("/diandian/ws/tts/volcengine", uri.AbsolutePath);
    }

    // ------------------------------------------------------------- 自愈

    [Fact]
    public void 空服务器地址_自愈回默认值()
    {
        var settings = new AppSettings
        {
            ConnectionMode = AppSettings.ConnectionModeLocalServer,
            BaseUrl = "   ",
        };

        settings.Normalize();

        Assert.Equal("https://127.0.0.1/diandian/proxy", settings.BaseUrl);
        // 构造期不能抛：抛了就是「重启即崩，崩在能进设置页之前」。
        var transport = ChatTransportFactory.Create(settings);
        Assert.IsType<LocalServerChatClient>(transport);
    }

    [Fact]
    public void 自定义模型加空地址_自愈回方舟模型()
    {
        var settings = new AppSettings
        {
            Model = AppSettings.CustomModelKey,
            CustomBaseUrl = "",
        };

        settings.Normalize();

        Assert.NotEqual(AppSettings.CustomModelKey, settings.Model);
        // 自愈后工厂要能造出东西，不能抛。
        var transport = ChatTransportFactory.Create(settings);
        Assert.IsType<ArkChatClient>(transport);
    }

    [Fact]
    public void 自定义模型加非法地址_自愈回方舟模型()
    {
        var settings = new AppSettings
        {
            Model = AppSettings.CustomModelKey,
            CustomBaseUrl = "不是 URL",
        };

        settings.Normalize();

        Assert.NotEqual(AppSettings.CustomModelKey, settings.Model);
    }

    [Fact]
    public void 本地服务器模式_地址以斜杠结尾也能正确拼路径()
    {
        var settings = new AppSettings
        {
            ConnectionMode = AppSettings.ConnectionModeLocalServer,
            BaseUrl = "https://127.0.0.1/diandian/proxy/",
        };

        settings.Normalize();

        Assert.Equal(
            "https://127.0.0.1/diandian/proxy/chat",
            WireProtocol.HttpUri(settings.ServerBaseUri, WireProtocol.ChatPath).ToString());
    }
}
