using System.Text.Json;
using System.Text.Json.Nodes;
using Xiaoliu.Core.Capture;
using Xiaoliu.Core.Configuration;
using Xiaoliu.Core.Protocol;
using Xiaoliu.Core.Storage;
using Xiaoliu.Core.Tools;
using Xiaoliu.Core.Visual;
using Xunit;

namespace Xiaoliu.Core.Tests;

public sealed class ToolPolicyTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppSettings _settings;
    private readonly HistoryStore _history;
    private readonly VisualEvidence _evidence;
    private readonly ToolDispatcher _dispatcher;
    private readonly List<GuidanceMark> _marks = new();

    public ToolPolicyTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "xiaoliu-tests", Guid.NewGuid().ToString("N") + ".db");
        _settings = new AppSettings();
        _history = new HistoryStore(_dbPath);
        // 桩：截图永远「内容没变」，测试不碰真实屏幕。
        _evidence = new VisualEvidence((_, _) => true);
        _dispatcher = new ToolDispatcher(_settings, _evidence, _history)
        {
            GuidanceRequested = mark => _marks.Add(mark),
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

    private static ToolUseCall Call(string name, object args) =>
        new("toolu_test", name, JsonNode.Parse(JsonSerializer.Serialize(args))!.ToJsonString());

    private static JsonObject Payload(JsonObject result) =>
        JsonNode.Parse(result["content"]!.GetValue<string>())!.AsObject();

    private VisualEvidence.Shot AddShot()
    {
        var image = new CapturedImage(new byte[] { 1, 2, 3 }, "image/jpeg", 800, 600, "hash-a", new byte[1024]);
        return _evidence.Add(new CaptureRect(100, 200, 800, 600), image);
    }

    [Fact]
    public void AutonomousCaptureIsRejectedWithPolicyCode()
    {
        var result = _dispatcher.Execute(Call("capture_screen", new { source = "current", purpose = "visible_content" }), default);

        Assert.True(result["is_error"]!.GetValue<bool>());
        var payload = Payload(result);
        Assert.Equal("rejected", payload["status"]!.GetValue<string>());
        Assert.Equal("capture_not_user_initiated", payload["error_code"]!.GetValue<string>());
        Assert.False(payload["retry_allowed"]!.GetValue<bool>());
    }

    [Fact]
    public void CaptureRejectionPointsAtTheUserSelectionWhenPresent()
    {
        var shot = AddShot();
        var result = _dispatcher.Execute(Call("capture_screen", new { source = "current", purpose = "visible_content" }), default);

        var message = Payload(result)["message"]!.GetValue<string>();
        Assert.Contains(shot.CaptureCallId, message);
    }

    [Theory]
    [InlineData("set_study_plan")]
    [InlineData("complete_plan_step")]
    [InlineData("clear_study_plan")]
    [InlineData("load_skill")]
    [InlineData("read_skill_resource")]
    public void OutOfScopeToolsAreRejected(string tool)
    {
        var result = _dispatcher.Execute(Call(tool, new { }), default);

        Assert.True(result["is_error"]!.GetValue<bool>());
        Assert.Equal("tool_not_supported", Payload(result)["error_code"]!.GetValue<string>());
    }

    [Fact]
    public void GuidanceWithoutEvidenceIsRejected()
    {
        var result = _dispatcher.Execute(Call("point_at_screen", new
        {
            capture_call_id = "selection-1", screen_number = 1, x = 500, y = 500, label = "这里",
        }), default);

        Assert.Equal("capture_missing", Payload(result)["error_code"]!.GetValue<string>());
        Assert.Empty(_marks);
    }

    [Fact]
    public void GuidanceWithUnknownReferenceIsRejected()
    {
        AddShot();
        var result = _dispatcher.Execute(Call("point_at_screen", new
        {
            capture_call_id = "selection-9", screen_number = 1, x = 500, y = 500, label = "这里",
        }), default);

        Assert.Equal("capture_reference_mismatch", Payload(result)["error_code"]!.GetValue<string>());
    }

    [Fact]
    public void GuidanceMapsNormalizedCoordinatesIntoSelectionRect()
    {
        var shot = AddShot();
        var result = _dispatcher.Execute(Call("point_at_screen", new
        {
            capture_call_id = shot.CaptureCallId, screen_number = 1, x = 500, y = 250, label = "这里",
        }), default);

        Assert.False(result["is_error"]!.GetValue<bool>());
        var mark = Assert.Single(_marks);
        // 圈选区域 (100,200) 800x600：x=500/1000 → +400，y=250/1000 → +150
        Assert.Equal(500, mark.Bounds.X);
        Assert.Equal(350, mark.Bounds.Y);
    }

    [Fact]
    public void GuidanceIsRejectedWhenScreenContentChanged()
    {
        var evidence = new VisualEvidence((_, _) => false);
        var dispatcher = new ToolDispatcher(_settings, evidence, _history) { GuidanceRequested = m => _marks.Add(m) };
        var shot = evidence.Add(new CaptureRect(0, 0, 100, 100), new CapturedImage([1], "image/jpeg", 100, 100, "h", new byte[1024]));

        var result = dispatcher.Execute(Call("highlight_screen_region", new
        {
            capture_call_id = shot.CaptureCallId,
            screen_number = 1,
            bounds = new { x1 = 10, y1 = 10, x2 = 200, y2 = 200 },
            label = "这里",
            style = "outline",
        }), default);

        Assert.Equal("stale_capture", Payload(result)["error_code"]!.GetValue<string>());
        Assert.Empty(_marks);
    }

    [Fact]
    public void HighlightRejectsZeroAreaBounds()
    {
        var shot = AddShot();
        var result = _dispatcher.Execute(Call("highlight_screen_region", new
        {
            capture_call_id = shot.CaptureCallId,
            screen_number = 1,
            bounds = new { x1 = 100, y1 = 100, x2 = 100, y2 = 300 },
            label = "这里",
            style = "outline",
        }), default);

        Assert.True(result["is_error"]!.GetValue<bool>());
        Assert.Equal("invalid_geometry", Payload(result)["error_code"]!.GetValue<string>());
    }

    [Fact]
    public void ConnectRejectsOverlappingRegions()
    {
        var shot = AddShot();
        var result = _dispatcher.Execute(Call("connect_screen_regions", new
        {
            capture_call_id = shot.CaptureCallId,
            screen_number = 1,
            first = new { bounds = new { x1 = 0, y1 = 0, x2 = 500, y2 = 500 }, label = "A" },
            second = new { bounds = new { x1 = 100, y1 = 100, x2 = 600, y2 = 600 }, label = "B" },
            relation = "对应",
        }), default);

        Assert.Equal("invalid_geometry", Payload(result)["error_code"]!.GetValue<string>());
    }

    [Fact]
    public void FactIsRecordedAtMostOncePerTurn()
    {
        _dispatcher.ResetTurn();
        var first = _dispatcher.Execute(Call("remember_fact", new { fact = "用户叫李平", category = "identity" }), default);
        var second = _dispatcher.Execute(Call("remember_fact", new { fact = "用户在北京", category = "identity" }), default);

        Assert.False(first["is_error"]!.GetValue<bool>());
        Assert.Equal("fact_budget_exhausted", Payload(second)["error_code"]!.GetValue<string>());
        Assert.Single(_history.Facts());
    }

    [Fact]
    public void SettingWhitelistIsEnforced()
    {
        var ok = _dispatcher.Execute(Call("update_setting", new { setting = "image_retention_days", value = 3 }), default);
        var bad = _dispatcher.Execute(Call("update_setting", new { setting = "auto_start", enabled = true }), default);

        Assert.False(ok["is_error"]!.GetValue<bool>());
        Assert.Equal(3, _settings.ImageRetentionDays);
        Assert.Equal("setting_not_allowed", Payload(bad)["error_code"]!.GetValue<string>());
    }

    [Fact]
    public void WrittenClaimIsRefusedBecauseScreenCannotBeVerified()
    {
        var result = _dispatcher.Execute(Call("report_text_insertion", new
        {
            insertion_call_id = "insertion-1", status = "written", reason = "看起来填进去了",
        }), default);

        Assert.Equal("unverifiable_claim", Payload(result)["error_code"]!.GetValue<string>());
    }

    [Fact]
    public void EveryDeclaredSchemaIsValidJson()
    {
        ToolCatalog.ValidateSchemas();
        Assert.Equal(14, ToolCatalog.All.Count);
    }
}
