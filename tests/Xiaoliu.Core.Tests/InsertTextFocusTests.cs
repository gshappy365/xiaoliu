using System.Text.Json;
using System.Text.Json.Nodes;
using Xiaoliu.Core.Capture;
using Xiaoliu.Core.Configuration;
using Xiaoliu.Core.Interop;
using Xiaoliu.Core.Protocol;
using Xiaoliu.Core.Storage;
using Xiaoliu.Core.Tools;
using Xunit;

namespace Xiaoliu.Core.Tests;

/// <summary>
/// insert_text 的前台约束：抢不到前台就一个字符都不许打。
///
/// 这条不是洁癖。SendInput 投递给「当前前台窗口」，不是投递给句柄，
/// 所以抢前台失败还照打，字会进到用户当时正在用的那个程序里 —— 实测过：
/// 记事本占着前台、SetForegroundWindow 被前台锁挡掉，30 个字符全进了记事本。
/// </summary>
public sealed class InsertTextFocusTests : IDisposable
{
    private readonly string _dbPath;
    private readonly HistoryStore _history;
    private readonly FakeWriter _writer = new();
    private readonly ToolDispatcher _dispatcher;

    public InsertTextFocusTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "xiaoliu-tests", Guid.NewGuid().ToString("N") + ".db");
        _history = new HistoryStore(_dbPath);
        _dispatcher = new ToolDispatcher(
            new AppSettings(),
            new VisualEvidence((_, _) => true),
            _history,
            _writer)
        {
            TargetProvider = () => new[] { new TargetWindow("notepad.exe", "无标题 - 记事本", 4242, false) },
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

    private JsonObject RunInsert(string text = "你好，小柳")
    {
        var call = new ToolUseCall(
            "toolu_insert",
            "insert_text",
            JsonSerializer.Serialize(new { target = "notepad.exe", text }));
        return _dispatcher.Execute(call, CancellationToken.None);
    }

    private static JsonObject Payload(JsonObject result) =>
        JsonNode.Parse(result["content"]!.GetValue<string>())!.AsObject();

    [Fact]
    public void 抢不到前台时一个字符都不打()
    {
        _writer.FocusResult = false;

        var payload = Payload(RunInsert());

        Assert.Equal("rejected", payload["status"]!.GetValue<string>());
        Assert.Equal("target_not_focusable", payload["error_code"]!.GetValue<string>());
        Assert.Empty(_writer.Typed);
    }

    [Fact]
    public void 抢到前台但又被抢走时也不打()
    {
        _writer.FocusResult = true;
        _writer.ForegroundResult = false;

        var payload = Payload(RunInsert());

        Assert.Equal("target_lost_focus", payload["failure_reason"]!.GetValue<string>());
        Assert.Empty(_writer.Typed);
    }

    [Fact]
    public void 前台确认无误才把原文送进去()
    {
        var payload = Payload(RunInsert("小柳测试 123"));

        Assert.Equal("accepted", payload["status"]!.GetValue<string>());
        Assert.Equal(8, payload["characters"]!.GetValue<int>());
        Assert.Equal(new[] { "小柳测试 123" }, _writer.Typed);
    }

    [Fact]
    public void 前台失败不可重试()
    {
        _writer.FocusResult = false;

        var result = RunInsert();

        Assert.True(result["is_error"]!.GetValue<bool>());
        Assert.False(ToolDispatcher.IsRetryable(result));
    }

    private sealed class FakeWriter : IWindowWriter
    {
        public bool FocusResult { get; set; } = true;

        public bool ForegroundResult { get; set; } = true;

        public List<string> Typed { get; } = new();

        public bool Focus(nint handle) => FocusResult;

        public bool IsForeground(nint handle) => ForegroundResult;

        public int Type(string text)
        {
            Typed.Add(text);
            return text.Length;
        }
    }
}
