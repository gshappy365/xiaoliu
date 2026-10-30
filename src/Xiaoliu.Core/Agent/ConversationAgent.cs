using System.Text;
using System.Text.Json.Nodes;
using Xiaoliu.Core.Capture;
using Xiaoliu.Core.Configuration;
using Xiaoliu.Core.Protocol;
using Xiaoliu.Core.Storage;
using Xiaoliu.Core.Tools;

namespace Xiaoliu.Core.Agent;

public sealed record AgentTurnResult(
    string Text,
    string StopReason,
    int ModelRequests,
    int ToolCalls,
    IReadOnlyList<CapturedImage> Screenshots);

/// <summary>
/// 一轮对话：提问 → 模型 → 工具 → 再问，直到模型不再要工具。
///
/// 结构与点点 MainAgentLoop 对齐（历史以问答对累积、工具结果按 tool_use_id 回传、
/// 每次请求重新拼 messages），但砍掉了课程、成长、上下文压缩、计费这些分支。
/// 上限也照抄：6 次请求、20 次工具调用。
/// </summary>
public sealed class ConversationAgent
{
    private sealed record Exchange(string Question, string Answer);

    private const int MaxHistoryExchanges = 10;

    private readonly ChatClient _chat;
    private readonly AppSettings _settings;
    private readonly ToolDispatcher _dispatcher;
    private readonly VisualEvidence _evidence;
    private readonly List<Exchange> _history = new();

    public ConversationAgent(
        ChatClient chat,
        AppSettings settings,
        ToolDispatcher dispatcher,
        VisualEvidence evidence)
    {
        _chat = chat;
        _settings = settings;
        _dispatcher = dispatcher;
        _evidence = evidence;
    }

    /// <summary>流式可见文本（累积全文），用来刷字幕条。</summary>
    public event Action<string>? TextUpdated;

    /// <summary>某个工具开始执行，用于界面提示「正在看你的图」之类。</summary>
    public event Action<string>? ToolStarted;

    public void ClearHistory() => _history.Clear();

    public async Task<AgentTurnResult> RunTurnAsync(
        string userText,
        IReadOnlyDictionary<string, string>? runtimeContext,
        CancellationToken cancellationToken)
    {
        _dispatcher.ResetTurn();

        var messages = BuildHistoryPrefix();
        var screenshots = _evidence.Shots.Select(shot => shot.Image).ToList();
        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = BuildUserMessage(userText, _evidence),
        });

        var context = BuildRuntimeContext(runtimeContext);
        if (context is not null)
        {
            messages.Add(context);
        }

        var requests = 0;
        var calls = 0;
        var retriedEmptyReply = false;
        var visible = new StringBuilder();
        var finalText = string.Empty;
        var stopReason = "end_turn";

        while (requests < _settings.MaxModelRequestsPerTurn)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var allowTools = calls < _settings.MaxToolCallsPerTurn;
            var request = new ChatRequest
            {
                Model = _settings.Model,
                System = SystemPrompt.For(_settings.AllowAutonomousCapture),
                Messages = (JsonArray)messages.DeepClone(),
                Tools = allowTools ? ToolCatalog.All : null,
                MaxTokens = allowTools ? _settings.MaxTokensWithTools : _settings.MaxTokensWithoutTools,
                ReasoningEffort = _settings.ReasoningEffort,
                // 视觉组模型自己会看图，让服务端把图片原样透传（省一次 3.4s 的 VL 描述）。
                // 纯文本模型仍然要服务端先转成文字，否则它拿到的是自己看不见的图片块。
                VisionCapable = ModelCatalog.Find(_settings.Model)?.Vision ?? false,
            };

            requests++;
            var round = await _chat.StreamAsync(request, accumulated =>
            {
                visible.Clear();
                visible.Append(accumulated);
                TextUpdated?.Invoke(accumulated);
            }, cancellationToken);

            if (round.ToolCalls.Count == 0)
            {
                // 上游偶尔会回一个「没有内容 + end_turn」的空轮（实测 2026-10-29 15:31，
                // 空转 5 秒后返回空文本），用户只看到「这一轮没有说完，请再说一次」。
                // 空回复不算答案，原样重发一次；只重试一次，避免把上限耗在空转上。
                if (string.IsNullOrWhiteSpace(round.Text) && !retriedEmptyReply)
                {
                    retriedEmptyReply = true;
                    continue;
                }

                finalText = round.Text;
                stopReason = round.StopReason;
                break;
            }

            if (!allowTools)
            {
                // 工具预算用完了但模型还想要：按真实回执收尾，不给它「假装成功」的空间。
                finalText = round.Text.Length > 0
                    ? round.Text
                    : "这一轮工具调用次数用完了，剩下的没做完。";
                stopReason = "max_tool_calls";
                break;
            }

            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = round.Content.DeepClone(),
            });

            var results = new JsonArray();
            var retryable = false;
            JsonObject? firstFailure = null;
            foreach (var call in round.ToolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ToolStarted?.Invoke(call.Name);

                if (calls >= _settings.MaxToolCallsPerTurn)
                {
                    results.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = call.Id,
                        ["content"] = "工具调用预算已用完，此操作未执行。",
                        ["is_error"] = true,
                    });
                    continue;
                }

                calls++;
                var result = _dispatcher.Execute(call, cancellationToken);
                results.Add(result);
                retryable |= ToolDispatcher.IsRetryable(result);
                firstFailure ??= ToolDispatcher.IsRetryable(result) ? null : result;
            }

            // 整轮都是「换参数也没用」的拒绝：再问一次模型只会换来一段道歉，
            // 而且每次追问都是一整个上游往返。直接收尾。
            if (!retryable)
            {
                finalText = string.IsNullOrWhiteSpace(round.Text)
                    ? ToolDispatcher.FallbackMessage(firstFailure ?? new JsonObject())
                    : round.Text;
                stopReason = "tool_rejected";
                break;
            }

            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = results,
            });

            // 中途那段话已经念过一部分，最终文本以下一轮为准；这里只把可见文本留着给字幕条。
            finalText = round.Text;
        }

        if (string.IsNullOrWhiteSpace(finalText))
        {
            finalText = "这一轮没有说完，请再说一次。";
        }

        Remember(userText, finalText);
        return new AgentTurnResult(finalText, stopReason, requests, calls, screenshots);
    }

    private void Remember(string question, string answer)
    {
        _history.Add(new Exchange(question, answer));
        while (_history.Count > MaxHistoryExchanges)
        {
            _history.RemoveAt(0);
        }
    }

    private JsonArray BuildHistoryPrefix()
    {
        var messages = new JsonArray();
        foreach (var exchange in _history)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = exchange.Question });
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = exchange.Answer });
        }

        return messages;
    }

    /// <summary>
    /// 用户这一轮的内容块。圈选图在前、文字在后 —— 和点点一致，
    /// 模型对「先看图再读问题」的顺序更稳。
    /// </summary>
    public static JsonArray BuildUserMessage(string userText, VisualEvidence evidence)
    {
        var blocks = new JsonArray();
        foreach (var shot in evidence.Shots)
        {
            blocks.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = $"capture_call_id={shot.CaptureCallId}, screen_number={shot.ScreenNumber}, "
                           + $"{shot.Image.Width}x{shot.Image.Height} 像素（用户圈选）。"
                           + "坐标按 0-1000 归一化，原点左上角。",
            });
            blocks.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = shot.Image.MediaType,
                    ["data"] = shot.Image.Base64,
                },
            });
        }

        blocks.Add(new JsonObject { ["type"] = "text", ["text"] = userText });
        return blocks;
    }

    private JsonObject? BuildRuntimeContext(IReadOnlyDictionary<string, string>? extra)
    {
        var parts = new List<string> { "【画面来源与能力】\n" + _evidence.DescribeForModel(_settings.AllowAutonomousCapture) };
        if (extra is not null)
        {
            parts.AddRange(extra.Select(pair => $"【{pair.Key}】\n{pair.Value}"));
        }

        return new JsonObject
        {
            ["role"] = "user",
            ["content"] = "客户端运行状态更新（不是用户的新请求；未列出的状态保持不变）：\n"
                          + string.Join("\n\n", parts),
        };
    }
}
