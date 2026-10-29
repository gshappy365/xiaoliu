using System.Text.Json;
using Xiaoliu.Core.Capture;

namespace Xiaoliu.Core.Visual;

public enum GuidanceKind
{
    Point,
    Highlight,
    Connection,
    Drag,
}

/// <summary>
/// 一条已经换算到「屏幕绝对坐标」的指点图形。Core 只负责算，画由 App 的 overlay 负责。
/// 坐标来源是模型给的 0-1000 归一化值，基准是用户圈选的那块截图（不是整块屏幕）。
/// </summary>
public sealed record GuidanceMark
{
    public required Guid Id { get; init; }
    public required GuidanceKind Kind { get; init; }
    public required int ScreenNumber { get; init; }
    public required string Label { get; init; }
    public required CaptureRect Bounds { get; init; }
    public CaptureRect? Second { get; init; }
    public bool Underline { get; init; }
    public bool IsActIntent { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class GuidanceException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// 指点工具的参数校验与坐标换算。校验规则照抄点点 ScreenGuidanceTools.Parse，
/// 因为模型就是按那套 schema 学会的：多一个字段、少一个必填都会导致它乱试。
/// </summary>
public static class GuidanceParser
{
    private static readonly string[] PointFields = ["capture_call_id", "screen_number", "x", "y", "label", "purpose"];
    private static readonly string[] HighlightFields = ["capture_call_id", "screen_number", "bounds", "label", "style", "insertion_call_id"];
    private static readonly string[] ConnectFields = ["capture_call_id", "screen_number", "first", "second", "relation"];
    private static readonly string[] DragFields = ["capture_call_id", "screen_number", "start", "end", "label"];

    public static GuidanceMark Parse(string tool, JsonElement args, VisualEvidence.Shot shot)
    {
        var allowed = tool switch
        {
            "point_at_screen" => PointFields,
            "highlight_screen_region" => HighlightFields,
            "connect_screen_regions" => ConnectFields,
            "demonstrate_screen_drag" => DragFields,
            _ => throw new GuidanceException("unknown_tool", "未知教学图形。"),
        };

        RequireFields(args, allowed);
        var screen = args.GetProperty("screen_number").GetInt32();

        return tool switch
        {
            "point_at_screen" => ParsePoint(args, shot, screen),
            "highlight_screen_region" => ParseHighlight(args, shot, screen),
            "connect_screen_regions" => ParseConnect(args, shot, screen),
            _ => ParseDrag(args, shot, screen),
        };
    }

    private static GuidanceMark ParsePoint(JsonElement args, VisualEvidence.Shot shot, int screen)
    {
        var point = ToScreen(args.GetProperty("x").GetInt32(), args.GetProperty("y").GetInt32(), shot);
        var purpose = args.TryGetProperty("purpose", out var value) ? value.GetString() : "look";
        if (purpose is not ("look" or "act"))
        {
            throw new GuidanceException("invalid_geometry", "无效指点意图。");
        }

        return new GuidanceMark
        {
            Id = Guid.NewGuid(),
            Kind = GuidanceKind.Point,
            ScreenNumber = screen,
            Label = ReadLabel(args.GetProperty("label")),
            Bounds = new CaptureRect(point.X, point.Y, 1, 1),
            IsActIntent = purpose == "act",
        };
    }

    private static GuidanceMark ParseHighlight(JsonElement args, VisualEvidence.Shot shot, int screen)
    {
        var bounds = ReadBounds(args.GetProperty("bounds"), shot);
        var style = args.GetProperty("style").GetString();
        if (style is not ("outline" or "underline"))
        {
            throw new GuidanceException("invalid_geometry", "无效强调样式。");
        }

        return new GuidanceMark
        {
            Id = Guid.NewGuid(),
            Kind = GuidanceKind.Highlight,
            ScreenNumber = screen,
            Label = ReadLabel(args.GetProperty("label")),
            Bounds = bounds,
            Underline = style == "underline",
        };
    }

    private static GuidanceMark ParseConnect(JsonElement args, VisualEvidence.Shot shot, int screen)
    {
        var first = ReadRegion(args.GetProperty("first"), shot);
        var second = ReadRegion(args.GetProperty("second"), shot);
        if (Intersects(first.Bounds, second.Bounds))
        {
            throw new GuidanceException("invalid_geometry", "关系区域不能重叠。");
        }

        return new GuidanceMark
        {
            Id = Guid.NewGuid(),
            Kind = GuidanceKind.Connection,
            ScreenNumber = screen,
            Label = ReadLabel(args.GetProperty("relation")),
            Bounds = first.Bounds,
            Second = second.Bounds,
        };
    }

    private static GuidanceMark ParseDrag(JsonElement args, VisualEvidence.Shot shot, int screen)
    {
        var start = ReadPoint(args.GetProperty("start"), shot);
        var end = ReadPoint(args.GetProperty("end"), shot);
        if (start == end)
        {
            throw new GuidanceException("invalid_geometry", "演示路径不能为空。");
        }

        var rect = CaptureRect.FromPoints(start.X, start.Y, end.X, end.Y);
        return new GuidanceMark
        {
            Id = Guid.NewGuid(),
            Kind = GuidanceKind.Drag,
            ScreenNumber = screen,
            Label = ReadLabel(args.GetProperty("label")),
            Bounds = rect,
            Second = new CaptureRect(end.X, end.Y, 1, 1),
        };
    }

    private static (int X, int Y) ReadPoint(JsonElement value, VisualEvidence.Shot shot)
    {
        RequireFields(value, "x", "y");
        return ToScreen(value.GetProperty("x").GetInt32(), value.GetProperty("y").GetInt32(), shot);
    }

    private static (CaptureRect Bounds, string Label) ReadRegion(JsonElement value, VisualEvidence.Shot shot)
    {
        RequireFields(value, "bounds", "label");
        return (ReadBounds(value.GetProperty("bounds"), shot), ReadLabel(value.GetProperty("label")));
    }

    private static CaptureRect ReadBounds(JsonElement value, VisualEvidence.Shot shot)
    {
        RequireFields(value, "x1", "y1", "x2", "y2");
        var p1 = ToScreen(value.GetProperty("x1").GetInt32(), value.GetProperty("y1").GetInt32(), shot);
        var p2 = ToScreen(value.GetProperty("x2").GetInt32(), value.GetProperty("y2").GetInt32(), shot, boundary: true);
        if (p2.X <= p1.X || p2.Y <= p1.Y)
        {
            throw new GuidanceException("invalid_geometry", "区域必须有正面积。");
        }

        return CaptureRect.FromPoints(p1.X, p1.Y, p2.X, p2.Y);
    }

    /// <summary>0-1000 归一化 → 圈选图内的像素 → 屏幕绝对坐标。</summary>
    private static (int X, int Y) ToScreen(int x, int y, VisualEvidence.Shot shot, bool boundary = false)
    {
        if (x < 0 || y < 0 || x > 1000 || y > 1000)
        {
            throw new GuidanceException("invalid_geometry", "坐标越界。");
        }

        var px = (int)Math.Round(x / 1000.0 * shot.Rect.Width);
        var py = (int)Math.Round(y / 1000.0 * shot.Rect.Height);
        var limitX = boundary ? shot.Rect.Width : shot.Rect.Width - 1;
        var limitY = boundary ? shot.Rect.Height : shot.Rect.Height - 1;
        if (px > limitX || py > limitY)
        {
            throw new GuidanceException("invalid_geometry", "坐标越界。");
        }

        return (shot.Rect.X + px, shot.Rect.Y + py);
    }

    private static string ReadLabel(JsonElement value)
    {
        var text = value.GetString()?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 80)
        {
            throw new GuidanceException("invalid_geometry", "标签无效。");
        }

        return text;
    }

    private static void RequireFields(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new GuidanceException("invalid_geometry", "参数必须为对象。");
        }

        var names = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            names.Any(name => !allowed.Contains(name, StringComparer.Ordinal)))
        {
            throw new GuidanceException("invalid_geometry", "参数含未知或重复字段。");
        }
    }

    private static bool Intersects(CaptureRect a, CaptureRect b) =>
        a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;
}
