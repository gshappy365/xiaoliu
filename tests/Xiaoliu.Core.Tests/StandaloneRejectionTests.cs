using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xiaoliu.Core.Agent;
using Xiaoliu.Core.Capture;
using Xiaoliu.Core.Configuration;
using Xiaoliu.Core.Protocol;
using Xiaoliu.Core.Storage;
using Xiaoliu.Core.Tools;
using Xunit;

namespace Xiaoliu.Core.Tests;

/// <summary>
/// 云直连后没有服务端 VL 转述兜底（ADR 0011）：纯文本模型遇到图只能提前拦下。
/// 两个拦截点都钉住 —— 进轮前的圈选图拒绝，和轮内 capture_screen 的策略回执。
/// </summary>
public sealed class StandaloneRejectionTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppSettings _settings;
    private readonly HistoryStore _history;
    private readonly VisualEvidence _evidence;
    private readonly ToolDispatcher _dispatcher;

    /// <summary>不该被调用到的传输：被调即失败，证明拦截发生在请求之前。</summary>
    private sealed class MustNotCallTransport : IChatTransport
    {
        public Task<ChatRoundResult> StreamAsync(ChatRequest request, Action<string>? onAccumulatedText,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("纯文本模型带图轮次不应发起上游请求");
    }

    public StandaloneRejectionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "xiaoliu-tests", Guid.NewGuid().ToString("N") + ".db");
        _settings = new AppSettings
        {
            Model = "deepseek-v4-1-flash-261017", // 纯文本组
            AllowAutonomousCapture = true,
        };
        _history = new HistoryStore(_dbPath);
        _evidence = new VisualEvidence((_, _) => true);
        _dispatcher = new ToolDispatcher(_settings, _evidence, _history, grabScreen: () =>
        {
            var image = new CapturedImage(new byte[] { 9, 9, 9 }, "image/jpeg", 1920, 1080, "hash-grab", new byte[1024]);
            return (new CaptureRect(0, 0, 1920, 1080), image);
        })
        {
            GuidanceRequested = _ => { },
            NoticeRequested = _ => { },
            TargetProvider = () => Array.Empty<Xiaoliu.Core.Interop.TargetWindow>(),
        };
    }

    public void Dispose()
    {
        _history.Dispose();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    private void AddShot()
    {
        var image = new CapturedImage(new byte[] { 1, 2, 3 }, "image/jpeg", 800, 600, "hash-a", new byte[1024]);
        _evidence.Add(new CaptureRect(0, 0, 800, 600), image);
    }

    [Fact]
    public async Task Turn_with_a_shot_is_rejected_before_any_upstream_call()
    {
        AddShot();
        var agent = new ConversationAgent(new MustNotCallTransport(), _settings, _dispatcher, _evidence);

        var result = await agent.RunTurnAsync("我圈了这块，上面写的什么", null, CancellationToken.None);

        Assert.Equal("vision_unavailable", result.StopReason);
        Assert.Equal(0, result.ModelRequests);
        Assert.Contains("纯文本模型", result.Text);
        Assert.Contains("模型服务", result.Text);
    }

    [Fact]
    public async Task Custom_endpoint_models_keep_the_benefit_of_the_doubt()
    {
        // 自定义端点的模型能力客户端无从判断（10B 决策）：允许尝试，让上游错误自然暴露。
        AddShot();
        _settings.Model = AppSettings.CustomModelKey;
        _settings.CustomModelName = "some-vlm";
        var agent = new ConversationAgent(new MustNotCallTransport(), _settings, _dispatcher, _evidence);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.RunTurnAsync("看一眼", null, CancellationToken.None));
    }

    [Fact]
    public async Task Vision_model_turns_still_go_upstream()
    {
        AddShot();
        _settings.Model = "doubao-seed-2-1-pro-260915";
        var agent = new ConversationAgent(new ThrowingTransport(), _settings, _dispatcher, _evidence);

        // 视觉组模型不该被拦：轮次会真正发起请求（这里用抛错的传输证明走到了它）。
        await Assert.ThrowsAsync<ChatHttpException>(() => agent.RunTurnAsync("看一眼", null, CancellationToken.None));
    }

    [Fact]
    public void Capture_screen_by_a_text_only_model_is_rejected_with_a_clean_receipt()
    {
        var call = new ToolUseCall("toolu_x", "capture_screen", "{}");

        var result = _dispatcher.Execute(call, default);
        var payload = JsonNode.Parse(result["content"]!.GetValue<string>())!.AsObject();

        Assert.True(result["is_error"]!.GetValue<bool>());
        Assert.Equal("rejected", payload["status"]!.GetValue<string>());
        Assert.Equal("capture_model_cannot_see", payload["error_code"]!.GetValue<string>());
    }

    [Fact]
    public void Capture_screen_still_works_for_vision_models()
    {
        _settings.Model = "doubao-seed-2-1-pro-260915";
        var call = new ToolUseCall("toolu_x", "capture_screen", "{}");

        var result = _dispatcher.Execute(call, default);

        Assert.False(result["is_error"]!.GetValue<bool>());
    }

    private sealed class ThrowingTransport : IChatTransport
    {
        public Task<ChatRoundResult> StreamAsync(ChatRequest request, Action<string>? onAccumulatedText,
            CancellationToken cancellationToken) =>
            throw new ChatHttpException(HttpStatusCode.Unauthorized, "unauthorized");
    }
}
