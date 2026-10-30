using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xiaoliu.Core.Capture;
using Xiaoliu.Core.Configuration;
using Xiaoliu.Core.Interop;
using Xiaoliu.Core.Protocol;
using Xiaoliu.Core.Storage;
using Xiaoliu.Core.Visual;

namespace Xiaoliu.Core.Tools;

/// <summary>
/// 工具执行 + 策略。策略全部在客户端，服务器端一行不改（交接文档的硬约束）。
///
/// 三条策略在这里落地：
/// 1. 抓屏按 <see cref="AppSettings.AllowAutonomousCapture"/> 放行或拒绝（ADR 0010）；
///    关掉时原因码 capture_not_user_initiated，回到 ADR 0002 的旧立场；
/// 2. 指点必须引用一张可用的图（用户圈选的，或本轮自己抓的），且那张图对应屏幕内容没变（hash 判定）；
/// 3. 课程三件套与技能两件套不实现，收到即 rejected。
/// </summary>
public sealed class ToolDispatcher
{
    private const int MaxFactPerTurn = 1;
    private const int MaxFileBytes = 512 * 1024;

    private readonly AppSettings _settings;
    private readonly VisualEvidence _evidence;
    private readonly HistoryStore _history;
    private readonly IWindowWriter _writer;
    private readonly Func<(CaptureRect Bounds, CapturedImage Image)> _grabScreen;

    private int _factsThisTurn;
    private int _insertionSequence;
    private readonly Dictionary<string, int> _repeatCounts = new(StringComparer.Ordinal);

    /// <summary>一旦置位，本轮所有后续工具调用都直接返回这条失败，不再执行。</summary>
    private string? _terminalFailure;

    public ToolDispatcher(
        AppSettings settings,
        VisualEvidence evidence,
        HistoryStore history,
        IWindowWriter? writer = null,
        Func<(CaptureRect Bounds, CapturedImage Image)>? grabScreen = null)
    {
        _settings = settings;
        _evidence = evidence;
        _history = history;
        _writer = writer ?? NativeWindowWriter.Instance;
        // 真抓屏要 GDI，测试里换成桩，否则「允许抓屏」这条路根本没法断言。
        _grabScreen = grabScreen ?? (() =>
        {
            var bounds = ScreenCapture.VirtualScreenBounds();
            return (bounds, ScreenCapture.Capture(bounds));
        });
    }

    /// <summary>指点图形要画出来了。</summary>
    public Action<GuidanceMark>? GuidanceRequested { get; set; }

    /// <summary>要在光标旁显示一行提示。</summary>
    public Action<string>? NoticeRequested { get; set; }

    /// <summary>可填写的目标窗口。</summary>
    public Func<IReadOnlyList<TargetWindow>>? TargetProvider { get; set; }

    /// <summary>每次工具执行完都会触发，用于日志与界面反馈。</summary>
    public Action<ToolUseCall, JsonObject>? Executed { get; set; }

    public void ResetTurn()
    {
        _factsThisTurn = 0;
        _insertionSequence = 0;
        _repeatCounts.Clear();
        _terminalFailure = null;
    }

    public JsonObject Execute(ToolUseCall call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = ExecuteCore(call, cancellationToken);
        Executed?.Invoke(call, result);
        return result;
    }

    private JsonObject ExecuteCore(ToolUseCall call, CancellationToken cancellationToken)
    {
        if (_terminalFailure is not null)
        {
            return Rejected(call, "tool_round_stopped", _terminalFailure, "stop",
                "本轮已停止工具恢复，按真实回执收尾，明确说明没做到的部分。");
        }

        if (ToolCatalog.RejectedTools.Contains(call.Name))
        {
            return Rejected(call, "tool_not_supported",
                "小六不实现这个工具（课程与技能体系已移除），此操作未执行。",
                recovery: "stop",
                nextStep: "不要再次请求该工具；用已有信息回答，或如实说明这项能力不在当前客户端。");
        }

        JsonElement args;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            args = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Rejected(call, "invalid_arguments", "工具参数不是合法 JSON，此操作未执行。", "correct_arguments",
                "按 schema 重新给出参数。");
        }

        if (args.ValueKind != JsonValueKind.Object)
        {
            return Rejected(call, "invalid_arguments", "工具参数必须是对象，此操作未执行。", "correct_arguments",
                "按 schema 重新给出参数。");
        }

        try
        {
            return call.Name switch
            {
                ToolCatalog.CaptureScreen => _settings.AllowAutonomousCapture
                    ? CaptureScreen(call)
                    : RejectCapture(call),
                ToolCatalog.PointAtScreen or ToolCatalog.HighlightRegion
                    or ToolCatalog.ConnectRegions or ToolCatalog.DemonstrateDrag => ExecuteGuidance(call, args),
                ToolCatalog.InsertText => ExecuteInsertText(call, args),
                ToolCatalog.ReportTextInsertion => ExecuteReportInsertion(call, args),
                ToolCatalog.CopyToClipboard => ExecuteCopy(call, args),
                ToolCatalog.ListTargetApplications => ExecuteListTargets(call),
                ToolCatalog.ReadUserFile => ExecuteReadFile(call, args),
                ToolCatalog.SearchHistory => ExecuteSearchHistory(call, args),
                ToolCatalog.RememberFact => ExecuteRememberFact(call, args),
                ToolCatalog.ShowNotice => ExecuteShowNotice(call, args),
                ToolCatalog.UpdateSetting => ExecuteUpdateSetting(call, args),
                _ => Rejected(call, "unknown_tool", "未提供此工具，此操作未执行。", "stop", "不要请求不存在的工具。"),
            };
        }
        catch (GuidanceException ex)
        {
            return Rejected(call, ex.Code, ex.Message, "correct_arguments",
                "按当前已观察的画面修正参数，无需重新截图。", includeCaptureContext: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Rejected(call, "execution_failed", $"工具执行失败：{ex.Message}", "stop",
                "如实说明未完成，不要假定成功。");
        }
    }

    // ---------------------------------------------------------------- 抓屏

    /// <summary>
    /// 模型自己抓一张当前屏幕（ADR 0010）。
    ///
    /// 图不能只放在回执文字里 —— 它必须真的回到模型眼前，所以 tool_result 的 content
    /// 用数组形式：一个 text（结构化回执，给模型引用坐标用）+ 一个 image（原图）。
    /// 纯文本上游那边，服务端会把这张图转成 VL 描述/OCR 文字再发，链路不用改。
    ///
    /// 一轮最多抓两次：屏幕变了再抓一次是合理的，再多就是在原地打转，
    /// 每次都是一整个上游往返，用户等不起。
    /// </summary>
    private JsonObject CaptureScreen(ToolUseCall call)
    {
        _repeatCounts.TryGetValue("capture_screen", out var attempts);
        attempts++;
        _repeatCounts["capture_screen"] = attempts;

        if (attempts > 2)
        {
            _terminalFailure = "本轮抓屏次数已用完，不再抓新的。";
            return Rejected(call, "capture_budget_exhausted",
                "本轮已经抓过两次屏幕，不再抓第三次。", "stop",
                "用手上已有的截图回答；确实看不到就说看不到，不要再请求抓屏。");
        }

        CaptureRect bounds;
        CapturedImage image;
        try
        {
            (bounds, image) = _grabScreen();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Rejected(call, "capture_failed", $"抓屏失败：{ex.Message}", "stop",
                "如实说明抓不到屏幕，请用户圈选一块区域。");
        }

        var shot = _evidence.Add(bounds, image, prefix: "capture");
        var payload = new JsonObject
        {
            ["status"] = "accepted",
            ["capture_call_id"] = shot.CaptureCallId,
            ["screen_number"] = shot.ScreenNumber,
            ["width"] = image.Width,
            ["height"] = image.Height,
            ["message"] = $"已抓取当前屏幕 {image.Width}x{image.Height}，图就在这条回执里。",
            ["next_step"] = $"指点工具引用 capture_call_id={shot.CaptureCallId}、screen_number={shot.ScreenNumber}，"
                            + "坐标按 0-1000 归一化。",
        };

        return new JsonObject
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = call.Id,
            ["is_error"] = false,
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = payload.ToJsonString() },
                new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = image.MediaType,
                        ["data"] = Convert.ToBase64String(image.Bytes),
                    },
                },
            },
        };
    }

    // ---------------------------------------------------------------- 抓屏策略

    /// <summary>
    /// 关闭「允许模型主动抓屏」时的拒绝路径（ADR 0002 的旧立场，现在是个开关）。
    /// 有圈选图时额外说明「图已经在手」，否则模型会以为抓屏失败要重试。
    ///
    /// 第二次起直接终止本轮工具恢复：实测模型会把「被拒」当成「临时故障」反复重试，
    /// 每次重试都是一整个上游往返（首字 30 秒级别），用户等不起。
    /// </summary>
    private JsonObject RejectCapture(ToolUseCall call)
    {
        _repeatCounts.TryGetValue("capture_screen", out var attempts);
        attempts++;
        _repeatCounts["capture_screen"] = attempts;

        var message = _evidence.IsEmpty
            ? "用户关掉了「允许主动看屏幕」，抓屏被策略禁用，重试也不会成功。"
            : $"用户关掉了「允许主动看屏幕」，抓屏被策略禁用。本轮已附带用户圈选的 {_evidence.Latest!.CaptureCallId}，直接用它。";

        if (attempts >= 2)
        {
            _terminalFailure = message + " 本轮停止工具恢复。";
        }

        return Rejected(call, "capture_not_user_initiated", message, recovery: "stop",
            nextStep: "不要再次调用 capture_screen。有图就用图；没有图就请用户圈选后重问，或如实说明看不到屏幕。");
    }

    // ---------------------------------------------------------------- 指点

    private JsonObject ExecuteGuidance(ToolUseCall call, JsonElement args)
    {
        if (_evidence.IsEmpty)
        {
            return Rejected(call, "capture_missing",
                "本轮没有用户圈选的截图，指点没有依据，未派发。", "stop",
                "请用户圈选后重问；不要凭想象指点。");
        }

        var captureCallId = args.TryGetProperty("capture_call_id", out var id) ? id.GetString() ?? "" : "";
        var screenNumber = args.TryGetProperty("screen_number", out var screen) && screen.TryGetInt32(out var number)
            ? number
            : 0;

        if (!_evidence.TryGet(captureCallId, screenNumber, out var shot))
        {
            var known = string.Join("、", _evidence.Shots.Select(s => $"{s.CaptureCallId}/screen_number={s.ScreenNumber}"));
            return Rejected(call, "capture_reference_mismatch",
                $"截图引用无效。可用引用：{known}。", "use_current_capture",
                "用本轮回执里给出的 capture_call_id 与 screen_number 重新调用。", includeCaptureContext: true);
        }

        if (!_evidence.StillValid(shot))
        {
            // 屏幕变了就不要再让它试：同一个引用重试多少次都是同一个结果，
            // 每试一次就是一整个上游往返（首轮 30 秒级），用户等不起。
            _terminalFailure = "截图对应的屏幕内容已经变化，本轮的这张图不再可用，已停止工具恢复。";
            return Rejected(call, "stale_capture",
                "截图对应的屏幕内容已经变化，这张图不再代表当前画面，未派发指点。", "stop",
                "不要重试本轮的指点；请用户重新圈选后再指。");
        }

        var mark = GuidanceParser.Parse(call.Name, args, shot);
        GuidanceRequested?.Invoke(mark);

        return Accepted(call, new JsonObject
        {
            ["status"] = "accepted",
            ["guidance_id"] = mark.Id.ToString(),
            ["capture_call_id"] = shot.CaptureCallId,
            ["screen_number"] = mark.ScreenNumber,
            ["effect"] = call.Name,
            ["verification"] = "presentation_only",
            ["message"] = "已派发指点；仅表示展示，不证明位置识别正确。",
        });
    }

    // ---------------------------------------------------------------- 写回与剪贴板

    private JsonObject ExecuteInsertText(ToolUseCall call, JsonElement args)
    {
        var target = args.TryGetProperty("target", out var t) ? t.GetString() ?? "" : "";
        var text = args.TryGetProperty("text", out var x) ? x.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(text))
        {
            return Rejected(call, "invalid_arguments", "target 与 text 都必填，未执行填写。", "correct_arguments",
                "补全参数后重试。");
        }

        var windows = TargetProvider?.Invoke() ?? NativeWindows.ListVisibleWindows();
        var match = windows.FirstOrDefault(w =>
            string.Equals(w.ProcessName, target, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileNameWithoutExtension(w.ProcessName), Path.GetFileNameWithoutExtension(target), StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            var available = string.Join("、", windows.Select(w => w.ProcessName).Distinct().Take(12));
            return Rejected(call, "target_not_found",
                $"目标 {target} 当前不在可填写清单里。当前可用：{available}。", "correct_arguments",
                "从清单里选一个目标，或让用户先把窗口打开。");
        }

        // 抢不到前台就一个字符都不能打。SendInput 是发给「当前前台窗口」的，
        // 不是发给 handle 的 —— 抢前台失败还硬打，字就进了用户当时正在用的那个程序。
        if (!_writer.Focus(match.Handle))
        {
            return Rejected(call, "target_not_focusable",
                $"{match.ProcessName} 没能切到前台，为避免把字打进你正在用的其他窗口，一个字符都没有键入。",
                "stop",
                "不要重试。请用户手动点一下目标窗口再让他说一次，或换个目标。");
        }

        Thread.Sleep(180);

        if (!_writer.IsForeground(match.Handle))
        {
            return Rejected(call, "target_lost_focus",
                $"{match.ProcessName} 在切换过程中又被别的窗口抢走了前台，未键入任何字符。",
                "stop",
                "不要重试。请用户关掉抢焦点的窗口后再试。");
        }

        var typed = _writer.Type(text);
        var insertionId = $"insertion-{++_insertionSequence}";

        return Accepted(call, new JsonObject
        {
            ["status"] = "accepted",
            ["insertion_call_id"] = insertionId,
            ["target"] = match.ProcessName,
            ["characters"] = typed,
            ["submitted"] = false,
            ["verification"] = "unverified",
            ["message"] = $"已向 {match.ProcessName} 键入 {typed} 个字符，未按回车、未提交。"
                          + "小六不自主抓屏，因此无法核对界面；请让用户确认，不要宣布已填写成功。",
        });
    }

    private JsonObject ExecuteReportInsertion(ToolUseCall call, JsonElement args)
    {
        var status = args.TryGetProperty("status", out var s) ? s.GetString() : "uncertain";
        if (status == "written")
        {
            return Rejected(call, "unverifiable_claim",
                "小六不自主抓屏，无法用画面核对填写结果，不接受 written 结论。", "correct_arguments",
                "改用 uncertain，并请用户确认输入框里的内容。");
        }

        return Accepted(call, new JsonObject
        {
            ["status"] = "accepted",
            ["verification"] = "uncertain",
            ["message"] = "已记录核对结论；本轮没有可用画面证据，以 uncertain 为准。",
        });
    }

    private JsonObject ExecuteCopy(ToolUseCall call, JsonElement args)
    {
        var text = args.TryGetProperty("text", out var value) ? value.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(text))
        {
            return Rejected(call, "invalid_arguments", "text 为空，未写入剪贴板。", "correct_arguments", "给出要复制的完整文字。");
        }

        if (!NativeWindows.SetClipboardText(text))
        {
            return Rejected(call, "clipboard_failed", "写剪贴板失败（可能被其它程序占用），未复制。", "stop",
                "告知用户复制失败，不要声称已复制。");
        }

        return Accepted(call, new JsonObject
        {
            ["status"] = "accepted",
            ["characters"] = text.Length,
            ["verification"] = "clipboard_written",
            ["message"] = $"已写入剪贴板（{text.Length} 字），可直接 Ctrl+V；尚未填入或发送。",
        });
    }

    private JsonObject ExecuteListTargets(ToolUseCall call)
    {
        var windows = TargetProvider?.Invoke() ?? NativeWindows.ListVisibleWindows();
        var array = new JsonArray();
        foreach (var window in windows.Take(20))
        {
            array.Add(new JsonObject
            {
                ["target"] = window.ProcessName,
                ["title"] = window.Title.Length > 60 ? window.Title[..60] : window.Title,
                ["foreground"] = window.IsForeground,
            });
        }

        return Accepted(call, new JsonObject
        {
            ["status"] = "accepted",
            ["applications"] = array,
            ["count"] = array.Count,
        });
    }

    private JsonObject ExecuteReadFile(ToolUseCall call, JsonElement args)
    {
        var path = args.TryGetProperty("path", out var value) ? value.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return Rejected(call, "invalid_path", "只接受完整绝对路径，未读取。", "correct_arguments",
                "向用户确认完整路径。");
        }

        if (Directory.Exists(path))
        {
            return Rejected(call, "not_a_file", "这是目录不是文件，未读取。", "correct_arguments", "请用户给出具体文件。");
        }

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return Rejected(call, "file_not_found", "文件不存在，未读取。", "stop", "向用户说明找不到该文件，不要重复尝试。");
        }

        if (info.Length > MaxFileBytes)
        {
            return Rejected(call, "file_too_large", $"文件 {info.Length / 1024}KB，超过 512KB 上限，未读取。", "stop",
                "说明文件过大，请用户给更小的片段。");
        }

        string content;
        try
        {
            content = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Rejected(call, "read_failed", $"读取失败：{ex.Message}", "stop", "向用户说明失败原因。");
        }

        return Accepted(call, new JsonObject
        {
            ["status"] = "accepted",
            ["path"] = path,
            ["characters"] = content.Length,
            ["text"] = content,
        });
    }

    private JsonObject ExecuteSearchHistory(ToolUseCall call, JsonElement args)
    {
        var query = args.TryGetProperty("query", out var value) ? value.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(query))
        {
            return Rejected(call, "invalid_arguments", "query 是空的，未检索历史。", "correct_arguments",
                "把用户问到的关键词填进 query；用户没给关键词就不要检索。");
        }

        var hits = _history.Search(query, 5);
        if (hits.Count == 0)
        {
            return Accepted(call, new JsonObject { ["status"] = "accepted", ["message"] = "没有找到相关的历史对话。" });
        }

        var lines = hits.Select(hit => $"[entry_id={hit.EntryId}，截图 {hit.ScreenshotCount} 张] {hit.Summary}");
        return Accepted(call, new JsonObject
        {
            ["status"] = "accepted",
            ["results"] = string.Join("\n", lines),
            ["count"] = hits.Count,
        });
    }

    private JsonObject ExecuteRememberFact(ToolUseCall call, JsonElement args)
    {
        if (_factsThisTurn >= MaxFactPerTurn)
        {
            return Rejected(call, "fact_budget_exhausted", "本轮已记录过一条事实，未重复记录。", "stop",
                "不要在同一轮重复记录事实。");
        }

        var fact = args.TryGetProperty("fact", out var f) ? f.GetString() ?? "" : "";
        var category = args.TryGetProperty("category", out var c) ? c.GetString() ?? "" : "";
        if (fact.Length is 0 or > 200 || category is not ("identity" or "habit" or "case" or "self_correction"))
        {
            return Rejected(call, "invalid_arguments", "事实需 ≤200 字且 category 合法，未记录。", "correct_arguments",
                "按 schema 修正后重试。");
        }

        _factsThisTurn++;
        _history.AddFact(category, fact);
        return Accepted(call, new JsonObject
        {
            ["status"] = "accepted",
            ["message"] = "已记住。",
        });
    }

    private JsonObject ExecuteShowNotice(ToolUseCall call, JsonElement args)
    {
        var text = args.TryGetProperty("text", out var value) ? value.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return Rejected(call, "invalid_arguments", "text 为空，未显示。", "correct_arguments", "给出一到三行文字。");
        }

        NoticeRequested?.Invoke(text);
        return Accepted(call, new JsonObject
        {
            ["status"] = "accepted",
            ["message"] = "已显示提示。",
        });
    }

    private JsonObject ExecuteUpdateSetting(ToolUseCall call, JsonElement args)
    {
        var setting = args.TryGetProperty("setting", out var value) ? value.GetString() ?? "" : "";
        var number = args.TryGetProperty("value", out var v) && v.TryGetInt32(out var n) ? n : (int?)null;
        var enabled = args.TryGetProperty("enabled", out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? e.GetBoolean()
            : (bool?)null;

        switch (setting)
        {
            case "text_retention_days" when number is > 0 and <= 3650:
                _settings.TextRetentionDays = number.Value;
                break;
            case "image_retention_days" when number is > 0 and <= 3650:
                _settings.ImageRetentionDays = number.Value;
                break;
            case "max_total_megabytes" when number is >= 16 and <= 102400:
                _settings.MaxTotalMegabytes = number.Value;
                break;
            case "show_cursor" when enabled.HasValue:
                _settings.ShowCursor = enabled.Value;
                break;
            case "show_asr_partial" when enabled.HasValue:
                _settings.ShowAsrPartial = enabled.Value;
                break;
            default:
                return Rejected(call, "setting_not_allowed",
                    "设置项不在白名单或取值非法，未修改。可改：text_retention_days / image_retention_days / max_total_megabytes / show_cursor / show_asr_partial。",
                    "correct_arguments", "从白名单里选一个，并给出合法取值。");
        }

        _settings.Save();
        return Accepted(call, new JsonObject
        {
            ["status"] = "accepted",
            ["setting"] = setting,
            ["value"] = number?.ToString() ?? enabled?.ToString(),
            ["message"] = "已修改并保存。",
        });
    }

    // ---------------------------------------------------------------- 结果构造

    /// <summary>
    /// 这条回执还值得再试吗。<c>retry_allowed=false</c> 表示「换个参数也没用」，
    /// Agent 据此决定是否直接收尾 —— 否则模型会把策略拒绝当成临时故障，一路重试到预算耗尽。
    /// </summary>
    public static bool IsRetryable(JsonObject result)
    {
        if (!(result["is_error"]?.GetValue<bool>() ?? false))
        {
            return true;
        }

        // content 可能是字符串，也可能是块数组（抓屏会把原图一起塞进回执），
        // 这里只关心字符串那种。
        var content = result["content"] is JsonValue value ? value.GetValue<string>() : null;
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        try
        {
            var payload = JsonNode.Parse(content) as JsonObject;
            return payload?["retry_allowed"]?.GetValue<bool>() ?? false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>策略拒绝时给用户的一句人话，免得模型在无话可说时反复道歉。</summary>
    public static string FallbackMessage(JsonObject result)
    {
        // 同上：块数组形式的回执不是拒绝，取不到字符串就当没有 error_code。
        var content = result["content"] is JsonValue value ? value.GetValue<string>() : null;
        var code = string.Empty;
        try
        {
            code = (JsonNode.Parse(content ?? "{}") as JsonObject)?["error_code"]?.GetValue<string>() ?? "";
        }
        catch (JsonException)
        {
        }

        return code switch
        {
            "capture_not_user_initiated" or "capture_missing" => "我看不到你的屏幕。圈选一块区域再问我，我就能看那张图。",
            "stale_capture" => "刚才那张截图已经过时了，重新圈选一次我再告诉你。",
            "capture_reference_mismatch" => "我没对上那张截图，重新圈选一次吧。",
            "tool_not_supported" or "tool_round_stopped" => "这件事我这边做不了。",
            _ => "这一步没做成，你再说一次我试试别的办法。",
        };
    }

    private static JsonObject Accepted(ToolUseCall call, JsonObject payload) => new()
    {
        ["type"] = "tool_result",
        ["tool_use_id"] = call.Id,
        ["content"] = payload.ToJsonString(),
        ["is_error"] = false,
    };

    private JsonObject Rejected(
        ToolUseCall call,
        string code,
        string message,
        string recovery,
        string nextStep,
        bool includeCaptureContext = false)
    {
        var payload = new JsonObject
        {
            ["status"] = "rejected",
            ["error_code"] = code,
            ["failure_reason"] = code,
            ["message"] = message,
            ["recovery"] = recovery,
            ["retry_allowed"] = recovery != "stop",
            ["next_step"] = nextStep,
        };

        if (includeCaptureContext && !_evidence.IsEmpty)
        {
            payload["capture_context"] = _evidence.DescribeForModel(_settings.AllowAutonomousCapture);
        }

        return new JsonObject
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = call.Id,
            ["content"] = payload.ToJsonString(),
            ["is_error"] = true,
        };
    }
}
