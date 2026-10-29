using System.Text.Json;
using System.Text.Json.Nodes;
using Xiaoliu.Core.Protocol;

namespace Xiaoliu.Core.Tools;

public sealed record ToolDefinition(string Name, string Description, string InputSchemaJson);

/// <summary>
/// 14 个工具的声明。描述与 schema 尽量照抄点点（reference/tools-from-diandian.json），
/// 因为模型对这套措辞已经调过参；改动措辞会改变工具命中率，不是「优化文案」。
///
/// 排除的 5 个（课程三件套 + 技能两件套）不声明、收到即 rejected，
/// 见 docs/adr 与 rea/docs/adr/0005。
/// </summary>
public static class ToolCatalog
{
    public const string CaptureScreen = "capture_screen";
    public const string PointAtScreen = "point_at_screen";
    public const string HighlightRegion = "highlight_screen_region";
    public const string ConnectRegions = "connect_screen_regions";
    public const string DemonstrateDrag = "demonstrate_screen_drag";
    public const string InsertText = "insert_text";
    public const string ReportTextInsertion = "report_text_insertion";
    public const string CopyToClipboard = "copy_to_clipboard";
    public const string ListTargetApplications = "list_target_applications";
    public const string ReadUserFile = "read_user_file";
    public const string SearchHistory = "search_history";
    public const string RememberFact = "remember_fact";
    public const string ShowNotice = "show_notice";
    public const string UpdateSetting = "update_setting";

    public static readonly IReadOnlySet<string> RejectedTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "set_study_plan",
        "complete_plan_step",
        "clear_study_plan",
        "load_skill",
        "read_skill_resource",
    };

    public static readonly IReadOnlySet<string> GuidanceTools = new HashSet<string>(StringComparer.Ordinal)
    {
        PointAtScreen, HighlightRegion, ConnectRegions, DemonstrateDrag,
    };

    private const string CoordinateInstructions =
        " 只显示教学图形，不操作系统鼠标；必须引用本轮最新截图。"
        + "x、y 两轴分别归一化到 0 到 1000，原点左上角，x 向右、y 向下。";

    public static IReadOnlyList<ToolDefinition> All { get; } = Build();

    public static ToolDefinition? Find(string name) =>
        All.FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));

    /// <summary>schema 合法性自检：所有 schema 必须是可解析的 JSON 对象。</summary>
    public static void ValidateSchemas()
    {
        foreach (var tool in All)
        {
            using var document = JsonDocument.Parse(tool.InputSchemaJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"{tool.Name} 的 input_schema 不是对象。");
            }
        }
    }

    // ---------------------------------------------------------------- schema 拼装

    private static JsonObject Str(int? minLength = null, int? maxLength = null, string? description = null)
    {
        var schema = new JsonObject { ["type"] = "string" };
        if (minLength.HasValue) schema["minLength"] = minLength.Value;
        if (maxLength.HasValue) schema["maxLength"] = maxLength.Value;
        if (description is not null) schema["description"] = description;
        return schema;
    }

    private static JsonObject Int(int? minimum = null, string? description = null)
    {
        var schema = new JsonObject { ["type"] = "integer" };
        if (minimum.HasValue) schema["minimum"] = minimum.Value;
        if (description is not null) schema["description"] = description;
        return schema;
    }

    private static JsonObject Enum(params string[] values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return new JsonObject { ["type"] = "string", ["enum"] = array };
    }

    private static JsonObject Obj(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (required.Length > 0)
        {
            var array = new JsonArray();
            foreach (var field in required)
            {
                array.Add(field);
            }

            schema["required"] = array;
        }

        return schema;
    }

    private static JsonObject Bounds() => Obj(new JsonObject
    {
        ["x1"] = Int(0),
        ["y1"] = Int(0),
        ["x2"] = Int(0),
        ["y2"] = Int(0),
    }, "x1", "y1", "x2", "y2");

    private static JsonObject Region() => Obj(new JsonObject
    {
        ["bounds"] = Bounds(),
        ["label"] = Str(1, 80),
    }, "bounds", "label");

    private static JsonObject Point() => Obj(new JsonObject
    {
        ["x"] = Int(0),
        ["y"] = Int(0),
    }, "x", "y");

    /// <summary>指点工具共有的两个引用字段。</summary>
    private static void AddCaptureFields(JsonObject properties)
    {
        properties["capture_call_id"] = Str(1);
        properties["screen_number"] = Int(1);
    }

    private static ToolDefinition Guidance(string name, string description, JsonObject properties, params string[] required)
    {
        AddCaptureFields(properties);
        var schema = Obj(properties, new[] { "capture_call_id", "screen_number" }.Concat(required).ToArray());
        return new ToolDefinition(name, description + CoordinateInstructions, schema.ToJsonString(JsonDefaults.Wire));
    }

    private static ToolDefinition Simple(string name, string description, JsonObject properties, params string[] required) =>
        new(name, description, Obj(properties, required).ToJsonString(JsonDefaults.Wire));

    private static List<ToolDefinition> Build() =>
    [
        Simple(ShowNotice,
            "仅在内容必须被看清、复制或记住时，把一到三行简体中文显示在光标旁；新一轮对话开始后过期提醒被抑制",
            new JsonObject { ["text"] = Str(description: "一到三行简体中文") },
            "text"),

        Simple(RememberFact,
            "仅记录用户明确表达、至少一个月后仍有用的分类事实（≤200 字，category 必填）；每轮最多一条，客户端归一化去重",
            new JsonObject
            {
                ["fact"] = Str(description: "要记住的事实，≤200 字"),
                ["category"] = Enum("identity", "habit", "case", "self_correction"),
            },
            "fact", "category"),

        Simple(UpdateSetting,
            "按用户明确要求修改小柳自身设置。只支持本轮声明的设置项；设置项是本地留存与显示开关。",
            new JsonObject
            {
                ["setting"] = Enum("text_retention_days", "image_retention_days", "max_total_megabytes",
                    "show_cursor", "show_asr_partial"),
                ["value"] = Int(0, "数值型设置的取值"),
                ["enabled"] = new JsonObject { ["type"] = "boolean" },
            },
            "setting"),

        Simple(SearchHistory,
            "跨会话检索本机历史对话（文字摘要 + 本地 entry_id + 截图数量，倒序取 5 条，不自动读图）",
            new JsonObject { ["query"] = Str() },
            "query"),

        Simple(CaptureScreen,
            "读取回答所必需的一份视觉证据。小柳只在用户主动圈选时才有截图：模型发起本调用一律被拒绝，"
            + "用户圈选的那张图已经随本轮提问一并提供，不需要也无法再次抓屏。收到拒绝后不要重试，"
            + "直接依据已有图片或如实说明看不到屏幕。",
            new JsonObject
            {
                ["source"] = Enum("current", "history"),
                ["purpose"] = Enum("visible_content", "pointing", "course_verification",
                    "cross_screen_context", "historical_review"),
                ["scope"] = Enum("cursor_screen", "all_screens"),
                ["entry_id"] = Int(1),
                ["screenshot_index"] = Int(1),
            },
            "source", "purpose"),

        Simple(ReportTextInsertion,
            "查看填写后的新画面，回报文字是否进入目标输入框；不要求逐字一致。证据不足用 uncertain。只核对，不写入。",
            new JsonObject
            {
                ["insertion_call_id"] = Str(1),
                ["capture_call_id"] = Str(1),
                ["status"] = Enum("written", "not_written", "uncertain"),
                ["reason"] = Str(1, 2000),
            },
            "insertion_call_id", "status", "reason"),

        Guidance(PointAtScreen,
            "指出一个位置。purpose=look 看位置，act 请用户操作；省略为 look。",
            new JsonObject
            {
                ["x"] = Int(0),
                ["y"] = Int(0),
                ["label"] = Str(1, 80),
                ["purpose"] = Enum("look", "act"),
            },
            "x", "y", "label"),

        Guidance(HighlightRegion,
            "强调区域或单行文字；bounds 为左上与右下，outline 描边，underline 下划线。",
            new JsonObject
            {
                ["bounds"] = Bounds(),
                ["label"] = Str(1, 80),
                ["style"] = Enum("outline", "underline"),
            },
            "bounds", "label", "style"),

        Guidance(ConnectRegions,
            "依次强调同屏的两个不重叠区域并展示对应关系，不表示拖动。",
            new JsonObject
            {
                ["first"] = Region(),
                ["second"] = Region(),
                ["relation"] = Str(1, 80),
            },
            "first", "second", "relation"),

        Guidance(DemonstrateDrag,
            "演示从 start 到 end 的路径，真实拖动由用户完成。",
            new JsonObject
            {
                ["start"] = Point(),
                ["end"] = Point(),
                ["label"] = Str(1, 80),
            },
            "start", "end", "label"),

        Simple(InsertText,
            "把文字填入白名单目标窗口的输入框；不发送、不按回车、不提交。target 用 list_target_applications 给出的进程名。",
            new JsonObject
            {
                ["target"] = Str(description: "目标应用进程名，须在白名单内"),
                ["text"] = Str(),
            },
            "target", "text"),

        Simple(ListTargetApplications,
            "备用只读查询：列出本轮仍有效、可接收填写的目标窗口和 target。不激活窗口、不填写。",
            new JsonObject()),

        Simple(CopyToClipboard,
            "按用户本轮明确的复制要求，把完整文字写入系统剪贴板并核实，保留供用户 Ctrl+V。成功仅表示已复制，尚未填入或发送。",
            new JsonObject { ["text"] = Str(1, 50000) },
            "text"),

        Simple(ReadUserFile,
            "只读用户本轮明确请求的文件，完整路径可来自本轮原话或此前明确会话路径；仅限本机 UTF-8 普通文件，最多 512KB。"
            + "不接受猜测路径、目录或链接；失败后向用户说明，不重复尝试。",
            new JsonObject { ["path"] = Str() },
            "path"),
    ];
}
