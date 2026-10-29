using System.Text.Json;
using System.Text.Json.Nodes;
using Xiaoliu.Core.Capture;
using Xiaoliu.Core.Configuration;
using Xiaoliu.Core.Protocol;
using Xiaoliu.Core.Storage;
using Xiaoliu.Core.Tools;
using Xunit;

namespace Xiaoliu.Core.Tests;

/// <summary>
/// 历史检索的边界。原来的 SQL 是 <c>LIKE '%{query}%'</c>，两处会漏：
/// 空查询变成 <c>LIKE '%%'</c>，命中全部并按 id 倒序取 5 条 —— 模型不提任何关键词
/// 就能把最近 5 段对话翻出来；查询里带 % 或 _ 又会被当通配符。
/// </summary>
public sealed class HistorySearchTests : IDisposable
{
    private readonly string _dbPath;
    private readonly HistoryStore _history;
    private readonly ToolDispatcher _dispatcher;

    public HistorySearchTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "xiaoliu-tests", Guid.NewGuid().ToString("N") + ".db");
        _history = new HistoryStore(_dbPath);
        _history.AddEntry("怎么把窗口置顶", "用 SetWindowPos。", Array.Empty<CapturedImage>());
        _history.AddEntry("截图为什么是黑的", "DRM 保护。", Array.Empty<CapturedImage>());
        _history.AddEntry("热键冲突怎么办", "和点点二选一。", Array.Empty<CapturedImage>());

        _dispatcher = new ToolDispatcher(
            new AppSettings(),
            new VisualEvidence((_, _) => true),
            _history);
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

    private JsonObject RunSearch(string query)
    {
        var call = new ToolUseCall("toolu_search", "search_history", JsonSerializer.Serialize(new { query }));
        var result = _dispatcher.Execute(call, CancellationToken.None);
        return JsonNode.Parse(result["content"]!.GetValue<string>())!.AsObject();
    }

    [Fact]
    public void 空查询不返回任何历史()
    {
        Assert.Empty(_history.Search(""));
        Assert.Empty(_history.Search("   "));
        Assert.Empty(_history.Search(null!));
    }

    [Fact]
    public void 通配符按字面处理而不是匹配全部()
    {
        Assert.Empty(_history.Search("%"));
        Assert.Empty(_history.Search("_"));
        Assert.Empty(_history.Search("%%"));
    }

    [Fact]
    public void 正常关键词能命中()
    {
        var hits = _history.Search("截图");

        Assert.Single(hits);
        Assert.Contains("截图", hits[0].Summary);
    }

    [Fact]
    public void 空查询走工具层是参数错误而不是空结果()
    {
        var payload = RunSearch("  ");

        Assert.Equal("invalid_arguments", payload["error_code"]!.GetValue<string>());
        Assert.True(payload["retry_allowed"]!.GetValue<bool>());
    }

    [Fact]
    public void 通配符查询走工具层也拿不到历史()
    {
        var payload = RunSearch("%");

        Assert.Equal("accepted", payload["status"]!.GetValue<string>());
        Assert.Equal("没有找到相关的历史对话。", payload["message"]!.GetValue<string>());
    }
}
