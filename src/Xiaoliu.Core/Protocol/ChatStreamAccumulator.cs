using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xiaoliu.Core.Protocol;

/// <summary>
/// 把 Anthropic Messages SSE 帧拼回一轮完整响应（content 块 + stop_reason）。
///
/// 只做「拼装」，不做业务判断：工具调用在哪一轮、要不要执行，是 Agent 的事。
/// 帧顺序必须完整，否则带工具的请求会被上游判 malformed（rea/server/app/protocol.py 头部注释）。
/// </summary>
public sealed class ChatStreamAccumulator
{
    private sealed class BlockBuilder
    {
        public string Type = "text";
        public string? Id;
        public string? Name;
        public readonly StringBuilder Text = new();
        public readonly StringBuilder Json = new();
        public readonly StringBuilder Signature = new();
    }

    private readonly Dictionary<int, BlockBuilder> _blocks = new();
    private readonly StringBuilder _visibleText = new();

    public string? StopReason { get; private set; }

    public JsonNode? Usage { get; private set; }

    public string Text => _visibleText.ToString();

    public IReadOnlyList<ToolUseCall> ToolCalls { get; private set; } = Array.Empty<ToolUseCall>();

    /// <summary>返回 true 表示这一帧让可见文本变长了，调用方可以据此刷新字幕条。</summary>
    public bool Apply(SseFrame frame, out string accumulatedText)
    {
        accumulatedText = _visibleText.ToString();
        var json = frame.Json;
        if (json is null)
        {
            return false;
        }

        var type = json["type"]?.GetValue<string>() ?? frame.Event;
        switch (type)
        {
            case "content_block_start":
            {
                var index = json["index"]?.GetValue<int>() ?? _blocks.Count;
                var block = json["content_block"] as JsonObject ?? new JsonObject();
                var builder = new BlockBuilder
                {
                    Type = block["type"]?.GetValue<string>() ?? "text",
                    Id = block["id"]?.GetValue<string>(),
                    Name = block["name"]?.GetValue<string>(),
                };
                var seed = block["text"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(seed))
                {
                    builder.Text.Append(seed);
                    _visibleText.Append(seed);
                }

                _blocks[index] = builder;
                accumulatedText = _visibleText.ToString();
                return !string.IsNullOrEmpty(seed);
            }

            case "content_block_delta":
            {
                var index = json["index"]?.GetValue<int>() ?? 0;
                if (!_blocks.TryGetValue(index, out var builder))
                {
                    return false;
                }

                var delta = json["delta"] as JsonObject;
                var deltaType = delta?["type"]?.GetValue<string>();
                switch (deltaType)
                {
                    case "text_delta":
                    {
                        var piece = delta?["text"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(piece))
                        {
                            return false;
                        }

                        builder.Text.Append(piece);
                        _visibleText.Append(piece);
                        accumulatedText = _visibleText.ToString();
                        return true;
                    }

                    case "thinking_delta":
                        builder.Text.Append(delta?["thinking"]?.GetValue<string>());
                        return false;

                    case "signature_delta":
                        builder.Signature.Append(delta?["signature"]?.GetValue<string>());
                        return false;

                    case "input_json_delta":
                        builder.Json.Append(delta?["partial_json"]?.GetValue<string>());
                        return false;

                    default:
                        return false;
                }
            }

            case "message_delta":
            {
                StopReason = json["delta"]?["stop_reason"]?.GetValue<string>() ?? StopReason;
                Usage = json["usage"]?.DeepClone() ?? Usage;
                return false;
            }

            case "message_start":
            {
                Usage = json["message"]?["usage"]?.DeepClone();
                return false;
            }

            case "error":
            {
                var message = json["error"]?["message"]?.GetValue<string>() ?? "上游返回错误";
                throw new ChatProtocolException(message);
            }

            default:
                return false;
        }
    }

    public ChatRoundResult Build()
    {
        var content = new JsonArray();
        var calls = new List<ToolUseCall>();

        foreach (var (_, builder) in _blocks.OrderBy(pair => pair.Key))
        {
            switch (builder.Type)
            {
                case "text":
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = builder.Text.ToString() });
                    break;

                case "thinking":
                {
                    var block = new JsonObject
                    {
                        ["type"] = "thinking",
                        ["thinking"] = builder.Text.ToString(),
                    };
                    if (builder.Signature.Length > 0)
                    {
                        block["signature"] = builder.Signature.ToString();
                    }

                    content.Add(block);
                    break;
                }

                case "tool_use":
                {
                    var raw = builder.Json.ToString();
                    JsonNode input;
                    try
                    {
                        input = raw.Length == 0 ? new JsonObject() : JsonNode.Parse(raw)!;
                    }
                    catch (JsonException)
                    {
                        input = new JsonObject();
                    }

                    var id = builder.Id ?? string.Empty;
                    var name = builder.Name ?? string.Empty;
                    content.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = id,
                        ["name"] = name,
                        ["input"] = input.DeepClone(),
                    });
                    calls.Add(new ToolUseCall(id, name, input.ToJsonString()));
                    break;
                }
            }
        }

        ToolCalls = calls;
        return new ChatRoundResult
        {
            Text = _visibleText.ToString(),
            Content = content,
            ToolCalls = calls,
            StopReason = StopReason ?? (calls.Count > 0 ? "tool_use" : "end_turn"),
            Usage = Usage,
        };
    }
}

public sealed record ToolUseCall(string Id, string Name, string ArgumentsJson);

public sealed class ChatRoundResult
{
    public string Text { get; init; } = string.Empty;
    public JsonArray Content { get; init; } = new();
    public IReadOnlyList<ToolUseCall> ToolCalls { get; init; } = Array.Empty<ToolUseCall>();
    public string StopReason { get; init; } = "end_turn";
    public JsonNode? Usage { get; init; }
}

public sealed class ChatProtocolException(string message) : Exception(message);
