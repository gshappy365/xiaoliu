using System.Net;
using System.Text;
using Xiaoliu.Core.Agent;
using Xiaoliu.Core.Capture;
using Xiaoliu.Core.Configuration;
using Xiaoliu.Core.Protocol;
using Xiaoliu.Core.Storage;
using Xiaoliu.Core.Tools;
using Xunit;

namespace Xiaoliu.Core.Tests;

/// <summary>
/// 上游偶尔回「没有内容 + end_turn」的空轮。实测 2026-10-29 15:31：语音问「你现在可以看到
/// 什么内容？」，空转 5 秒后返回空文本，字幕条只剩兜底句「这一轮没有说完，请再说一次」。
/// 空回复不算答案，应当原样重发一次，而不是把空轮当结论。
/// </summary>
public sealed class EmptyReplyRetryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppSettings _settings;
    private readonly HistoryStore _history;
    private readonly VisualEvidence _evidence;
    private readonly ToolDispatcher _dispatcher;

    public EmptyReplyRetryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "xiaoliu-tests", Guid.NewGuid().ToString("N") + ".db");
        _settings = new AppSettings();
        _history = new HistoryStore(_dbPath);
        _evidence = new VisualEvidence((_, _) => true);
        _dispatcher = new ToolDispatcher(_settings, _evidence, _history)
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
            // 文件被占用就算了，临时目录里不影响后续测试。
        }
    }

    private const string EmptyReply =
        "event: message_start\n"
        + "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"role\":\"assistant\",\"content\":[]}}\n\n"
        + "event: message_delta\n"
        + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\n"
        + "data: [DONE]\n\n";

    private static string TextReply(string text) =>
        "event: message_start\n"
        + "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_2\",\"role\":\"assistant\",\"content\":[]}}\n\n"
        + "event: content_block_start\n"
        + "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n"
        + "event: content_block_delta\n"
        + "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"" + text + "\"}}\n\n"
        + "event: content_block_stop\n"
        + "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
        + "event: message_delta\n"
        + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\n"
        + "data: [DONE]\n\n";

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<string> _bodies;
        public int Calls { get; private set; }

        public ScriptedHandler(params string[] bodies) => _bodies = new Queue<string>(bodies);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var body = _bodies.Count > 0 ? _bodies.Dequeue() : TextReply("（脚本用完了）");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    private ConversationAgent BuildAgent(ScriptedHandler handler) =>
        new(new ChatClient(_settings, handler), _settings, _dispatcher, _evidence);

    [Fact]
    public async Task RetriesOnceWhenReplyIsEmpty()
    {
        var handler = new ScriptedHandler(EmptyReply, TextReply("我能看到你圈选的画面。"));
        var agent = BuildAgent(handler);

        var result = await agent.RunTurnAsync("你现在可以看到什么内容？", null, CancellationToken.None);

        Assert.Equal("我能看到你圈选的画面。", result.Text);
        Assert.Equal(2, result.ModelRequests);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task GivesUpAfterOneRetryAndKeepsFallback()
    {
        // 连续两次空轮：不能无限重试，落回兜底句。
        var handler = new ScriptedHandler(EmptyReply, EmptyReply);
        var agent = BuildAgent(handler);

        var result = await agent.RunTurnAsync("在吗", null, CancellationToken.None);

        Assert.Equal("这一轮没有说完，请再说一次。", result.Text);
        Assert.Equal(2, result.ModelRequests);
    }

    [Fact]
    public async Task DoesNotRetryWhenReplyHasText()
    {
        var handler = new ScriptedHandler(TextReply("在。"));
        var agent = BuildAgent(handler);

        var result = await agent.RunTurnAsync("在吗", null, CancellationToken.None);

        Assert.Equal("在。", result.Text);
        Assert.Equal(1, result.ModelRequests);
    }
}
