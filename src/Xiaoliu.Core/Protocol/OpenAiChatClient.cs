using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xiaoliu.Core.Tools;

namespace Xiaoliu.Core.Protocol;

/// <summary>
/// 自定义 OpenAI 兼容端点的直连客户端（ADR 0011，用户 10B 决策：与方舟组能力对齐）。
///
/// 对外仍是 <see cref="IChatTransport"/>：收 Anthropic 形状的 <see cref="ChatRequest"/>，
/// 吐 Anthropic 形状的 <see cref="ChatRoundResult"/>，OpenAI 的协议差异全部在这一层翻译：
///
/// - 工具定义 → <c>tools:[{type:"function",function:{name,description,parameters}}]</c>
/// - 圈选图块 → <c>{type:"image_url",image_url:{url:"data:image/jpeg;base64,…"}}</c>
/// - <c>tool_use</c> / <c>tool_result</c> → <c>assistant.tool_calls</c> / <c>role:"tool"</c> 消息
/// - 工具回执里的图（capture_screen 的 [text, image] 块数组）OpenAI 没有承载位，
///   挂到紧随其后的 user 消息里并注明来源
/// - 流式回包 <c>choices[].delta</c> 拼回 content/tool_use 块，映射回 Anthropic 语义
/// </summary>
public sealed class OpenAiChatClient : IChatTransport, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _endpoint;

    public OpenAiChatClient(string baseUrl, string apiKey, string modelName, HttpMessageHandler? handler = null)
    {
        _endpoint = BuildEndpoint(baseUrl);
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.TryAddWithoutValidation(WireProtocol.AuthorizationHeader, "Bearer " + apiKey);
        ModelName = modelName;
    }

    public string ModelName { get; }

    /// <summary>基地址接 <c>/chat/completions</c>；已带路径后缀就不重复接。</summary>
    public static string BuildEndpoint(string? baseUrl)
    {
        var trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException("自定义端点未配置 base URL，请到设置 → 模型服务填写。");
        }

        return trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + "/chat/completions";
    }

    public async Task<ChatRoundResult> StreamAsync(
        ChatRequest request,
        Action<string>? onAccumulatedText,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(BuildBody(request), Encoding.UTF8, "application/json"),
        };

        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new ChatHttpException(response.StatusCode, body);
        }

        var accumulator = new OpenAiStreamAccumulator();
        var reader = new SseReader();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var text = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var buffer = new char[4096];

        while (!reader.IsFinished)
        {
            var read = await text.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            foreach (var frame in reader.Push(new string(buffer, 0, read)))
            {
                if (accumulator.Apply(frame, out var accumulated))
                {
                    onAccumulatedText?.Invoke(accumulated);
                }
            }
        }

        reader.Complete();
        return accumulator.Build();
    }

    public static string BuildBody(ChatRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = TranslateMessages(request.System, request.Messages),
            ["stream"] = true,
        };

        body["max_tokens"] = request.MaxTokens;

        if (!string.IsNullOrEmpty(request.ReasoningEffort))
        {
            // OpenAI 兼容口径的推理档位；不支持的服务端一般会忽略。
            body["reasoning_effort"] = request.ReasoningEffort;
        }

        if (request.Tools is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = JsonNode.Parse(tool.InputSchemaJson),
                    },
                });
            }

            body["tools"] = tools;
            body["tool_choice"] = "auto";
        }

        return body.ToJsonString(JsonDefaults.Wire);
    }

    /// <summary>Anthropic 形状的 messages → OpenAI 形状。见类头注释的翻译规则。</summary>
    public static JsonArray TranslateMessages(string? system, JsonArray messages)
    {
        var outMessages = new JsonArray();
        if (!string.IsNullOrEmpty(system))
        {
            outMessages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        }

        foreach (var message in messages)
        {
            if (message is not JsonObject obj)
            {
                continue;
            }

            var role = obj["role"]?.GetValue<string>() ?? "user";
            switch (role)
            {
                case "assistant":
                    TranslateAssistant(obj, outMessages);
                    break;
                case "user":
                    TranslateUser(obj, outMessages);
                    break;
                default:
                    // 未知角色按原文照搬，宁可让上游报错也不静默丢内容。
                    outMessages.Add(obj.DeepClone());
                    break;
            }
        }

        return outMessages;
    }

    private static void TranslateAssistant(JsonObject message, JsonArray outMessages)
    {
        var content = message["content"];
        var text = new StringBuilder();
        var toolCalls = new JsonArray();

        if (content is JsonValue plainValue && plainValue.TryGetValue<string>(out var plainText))
        {
            text.Append(plainText);
        }
        else if (content is JsonArray blocks)
        {
            foreach (var block in blocks)
            {
                if (block is not JsonObject blockObj)
                {
                    continue;
                }

                switch (blockObj["type"]?.GetValue<string>())
                {
                    case "text":
                        text.Append(blockObj["text"]?.GetValue<string>());
                        break;
                    case "tool_use":
                        toolCalls.Add(new JsonObject
                        {
                            ["id"] = blockObj["id"]?.GetValue<string>() ?? string.Empty,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = blockObj["name"]?.GetValue<string>() ?? string.Empty,
                                ["arguments"] = blockObj["input"]?.ToJsonString(JsonDefaults.Wire) ?? "{}",
                            },
                        });
                        break;
                    case "thinking":
                        // OpenAI 兼容端点没有可移植的思考块承载位；历史里直接略过。
                        break;
                }
            }
        }

        var assistant = new JsonObject { ["role"] = "assistant" };
        assistant["content"] = text.Length > 0 ? text.ToString() : null;
        if (toolCalls.Count > 0)
        {
            assistant["tool_calls"] = toolCalls;
        }

        outMessages.Add(assistant);
    }

    private static void TranslateUser(JsonObject message, JsonArray outMessages)
    {
        var content = message["content"];
        if (content is JsonValue plainValue && plainValue.TryGetValue<string>(out var plain))
        {
            outMessages.Add(new JsonObject { ["role"] = "user", ["content"] = plain });
            return;
        }

        if (content is not JsonArray blocks)
        {
            outMessages.Add(message.DeepClone());
            return;
        }

        var parts = new JsonArray();
        var toolResults = new JsonArray();
        var orphanImages = new JsonArray();

        foreach (var block in blocks)
        {
            if (block is not JsonObject blockObj)
            {
                continue;
            }

            switch (blockObj["type"]?.GetValue<string>())
            {
                case "text":
                    parts.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = blockObj["text"]?.GetValue<string>() ?? string.Empty,
                    });
                    break;

                case "image":
                    parts.Add(ImagePart(blockObj));
                    break;

                case "tool_result":
                {
                    var toolCallId = blockObj["tool_use_id"]?.GetValue<string>() ?? string.Empty;
                    var resultText = new StringBuilder();
                    CollectToolResultText(blockObj["content"], resultText, orphanImages);
                    toolResults.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = toolCallId,
                        ["content"] = resultText.ToString(),
                    });
                    break;
                }
            }
        }

        // role:"tool" 消息必须紧跟在带 tool_calls 的 assistant 消息后面。
        foreach (var toolResult in toolResults)
        {
            outMessages.Add(toolResult!.DeepClone());
        }

        if (orphanImages.Count > 0)
        {
            var imageMessage = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(),
            };
            ((JsonArray)imageMessage["content"]!).Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = "【工具回执附图】OpenAI 兼容端点的工具回执没有图片承载位，随下一条消息补发。",
            });
            foreach (var image in orphanImages)
            {
                ((JsonArray)imageMessage["content"]!).Add(image!.DeepClone());
            }

            outMessages.Add(imageMessage);
        }

        if (parts.Count > 0)
        {
            outMessages.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
        }
    }

    private static void CollectToolResultText(JsonNode? content, StringBuilder text, JsonArray images)
    {
        switch (content)
        {
            case JsonValue plainValue when plainValue.TryGetValue<string>(out var plain):
                text.Append(plain);
                break;
            case JsonArray blocks:
                foreach (var block in blocks)
                {
                    if (block is not JsonObject blockObj)
                    {
                        continue;
                    }

                    if (blockObj["type"]?.GetValue<string>() == "text")
                    {
                        text.Append(blockObj["text"]?.GetValue<string>());
                    }
                    else if (blockObj["type"]?.GetValue<string>() == "image")
                    {
                        images.Add(ImagePart(blockObj));
                    }
                    else if (blockObj["type"]?.GetValue<string>() == "tool_result")
                    {
                        // 理论上不嵌套，防御一下。
                        CollectToolResultText(blockObj["content"], text, images);
                    }
                }
                break;
            case not null:
                text.Append(content.ToJsonString(JsonDefaults.Wire));
                break;
        }
    }

    private static JsonObject ImagePart(JsonObject imageBlock)
    {
        var source = imageBlock["source"] as JsonObject ?? new JsonObject();
        var mediaType = source["media_type"]?.GetValue<string>() ?? "image/jpeg";
        var data = source["data"]?.GetValue<string>() ?? string.Empty;
        return new JsonObject
        {
            ["type"] = "image_url",
            ["image_url"] = new JsonObject { ["url"] = $"data:{mediaType};base64,{data}" },
        };
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// 把 OpenAI chat/completions 的 SSE 帧拼回 Anthropic 形状的一轮响应。
/// <c>delta.content</c> 是可见文本；<c>delta.tool_calls</c> 按 index 拼；
/// <c>finish_reason</c> 映射回 <c>tool_use</c> / <c>end_turn</c>。
/// </summary>
public sealed class OpenAiStreamAccumulator
{
    private sealed class CallBuilder
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public readonly StringBuilder Json = new();
    }

    private readonly StringBuilder _visibleText = new();
    private readonly Dictionary<int, CallBuilder> _calls = new();
    private string? _finishReason;

    public string Text => _visibleText.ToString();

    /// <summary>返回 true 表示可见文本变长了（刷字幕条用）。</summary>
    public bool Apply(SseFrame frame, out string accumulatedText)
    {
        accumulatedText = _visibleText.ToString();
        var textLengthBefore = _visibleText.Length;
        var json = frame.Json;
        if (json is null)
        {
            return false;
        }

        if (json["error"] is JsonObject error)
        {
            var message = error["message"]?.GetValue<string>() ?? "自定义端点返回错误";
            throw new ChatProtocolException(message);
        }

        var choice = json["choices"] is JsonArray choices && choices.Count > 0
            ? choices[0] as JsonObject
            : null;
        if (choice is null)
        {
            return false;
        }

        if (choice["finish_reason"] is JsonNode finish && finish.GetValue<string>() is { Length: > 0 } reason)
        {
            _finishReason = reason;
        }

        if (choice["delta"] is not JsonObject delta)
        {
            return false;
        }

        if (delta["content"] is JsonNode content && content.GetValue<string>() is { Length: > 0 } piece)
        {
            _visibleText.Append(piece);
            accumulatedText = _visibleText.ToString();
        }

        if (delta["tool_calls"] is JsonArray toolCalls)
        {
            foreach (var call in toolCalls)
            {
                if (call is not JsonObject callObj)
                {
                    continue;
                }

                var index = callObj["index"]?.GetValue<int>() ?? _calls.Count;
                if (!_calls.TryGetValue(index, out var builder))
                {
                    builder = new CallBuilder();
                    _calls[index] = builder;
                }

                if (callObj["id"] is JsonNode id)
                {
                    builder.Id = id.GetValue<string>();
                }

                if (callObj["function"] is JsonObject function)
                {
                    if (function["name"] is JsonNode name && name.GetValue<string>() is { Length: > 0 } nameValue)
                    {
                        builder.Name = nameValue;
                    }

                    if (function["arguments"] is JsonNode arguments)
                    {
                        builder.Json.Append(arguments.GetValue<string>());
                    }
                }
            }
        }

        return _visibleText.Length > textLengthBefore;
    }

    public ChatRoundResult Build()
    {
        var content = new JsonArray();
        var calls = new List<ToolUseCall>();

        if (_visibleText.Length > 0)
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = _visibleText.ToString() });
        }

        foreach (var (_, builder) in _calls.OrderBy(pair => pair.Key))
        {
            JsonNode input;
            try
            {
                input = builder.Json.Length == 0 ? new JsonObject() : JsonNode.Parse(builder.Json.ToString())!;
            }
            catch (JsonException)
            {
                input = new JsonObject();
            }

            content.Add(new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = builder.Id,
                ["name"] = builder.Name,
                ["input"] = input.DeepClone(),
            });
            calls.Add(new ToolUseCall(builder.Id, builder.Name, input.ToJsonString()));
        }

        var stopReason = _finishReason switch
        {
            "tool_calls" or "function_call" => "tool_use",
            null => calls.Count > 0 ? "tool_use" : "end_turn",
            _ => "end_turn",
        };

        return new ChatRoundResult
        {
            Text = _visibleText.ToString(),
            Content = content,
            ToolCalls = calls,
            StopReason = stopReason,
            Usage = null,
        };
    }
}
