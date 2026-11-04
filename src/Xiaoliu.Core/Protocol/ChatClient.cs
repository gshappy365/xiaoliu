using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xiaoliu.Core.Configuration;
using Xiaoliu.Core.Tools;

namespace Xiaoliu.Core.Protocol;

/// <summary>
/// 对话传输的接缝。一轮 = 一次流式请求，吐出 Anthropic 形状的
/// <see cref="ChatRoundResult"/>（content 块 + tool_use + stop_reason），
/// 上游是什么协议由实现类翻译。Agent 只跟这个接口说话（ADR 0011）。
/// </summary>
public interface IChatTransport
{
    Task<ChatRoundResult> StreamAsync(
        ChatRequest request,
        Action<string>? onAccumulatedText,
        CancellationToken cancellationToken);
}

/// <summary>
/// 按设置挑传输实现：模型是 <see cref="AppSettings.CustomModelKey"/> 时走自定义
/// OpenAI 兼容端点，否则走火山方舟的 Anthropic 兼容端点。
/// </summary>
public static class ChatTransportFactory
{
    public static IChatTransport Create(AppSettings settings)
    {
        if (settings.IsCustomModel)
        {
            return new OpenAiChatClient(
                settings.CustomBaseUrl,
                settings.CustomApiKeyPlain ?? string.Empty,
                settings.UpstreamModel);
        }

        return new ArkChatClient(settings.ArkApiKeyPlain ?? string.Empty);
    }
}

/// <summary>
/// 火山方舟 Anthropic 兼容端点的直连客户端（ADR 0011）。
///
/// 请求体就是 Anthropic Messages：model / max_tokens / stream / system / messages / thinking / tools。
/// 曾经借道自建服务器的私有字段（<c>main_reasoning_effort</c>、<c>vision_capable</c>、
/// <c>diandian_credits</c>）全部不再发送 —— 前者换成协议原生的 <c>thinking</c> 预算，
/// 后两者服务的功能（VL 转述、路由）在客户端侧已被 ADR 0011 取消。
/// </summary>
public sealed class ArkChatClient : IChatTransport, IDisposable
{
    private readonly HttpClient _http;

    public ArkChatClient(string apiKey, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.TryAddWithoutValidation(WireProtocol.AuthorizationHeader, "Bearer " + apiKey);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-version", "2023-06-01");
    }

    public async Task<ChatRoundResult> StreamAsync(
        ChatRequest request,
        Action<string>? onAccumulatedText,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, WireProtocol.ArkAnthropicEndpoint)
        {
            Content = new StringContent(BuildBody(request), Encoding.UTF8, "application/json"),
        };

        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new ChatHttpException(response.StatusCode, body);
        }

        var accumulator = new ChatStreamAccumulator();
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

    /// <summary>
    /// 推理强度映射成 Anthropic 原生 <c>thinking</c> 预算：low=1024、medium=4096、high=8191。
    /// 协议要求预算严格小于 max_tokens，不够就把 max_tokens 抬到预算 +1024（它是上限不是目标长度）。
    /// </summary>
    public static int ThinkingBudget(string? effort) => effort switch
    {
        "low" => 1024,
        "high" => 8191,
        _ => 4096,
    };

    public static string BuildBody(ChatRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxTokens,
            ["stream"] = true,
            ["system"] = request.System,
            ["messages"] = request.Messages.DeepClone(),
        };

        if (!string.IsNullOrEmpty(request.ReasoningEffort))
        {
            var budget = ThinkingBudget(request.ReasoningEffort);
            if (budget >= request.MaxTokens)
            {
                body["max_tokens"] = budget + 1024;
            }

            body["thinking"] = new JsonObject
            {
                ["type"] = "enabled",
                ["budget_tokens"] = budget,
            };
        }

        if (request.Tools is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["input_schema"] = JsonNode.Parse(tool.InputSchemaJson),
                });
            }

            body["tools"] = tools;
        }

        return body.ToJsonString(JsonDefaults.Wire);
    }

    public void Dispose() => _http.Dispose();
}

public sealed class ChatRequest
{
    public required string Model { get; init; }
    public required string System { get; init; }
    public required JsonArray Messages { get; init; }
    public IReadOnlyList<ToolDefinition>? Tools { get; init; }
    public int MaxTokens { get; init; } = 8192;
    public string? ReasoningEffort { get; init; }
}

public sealed class ChatHttpException(HttpStatusCode status, string body)
    : Exception($"chat 请求失败（HTTP {(int)status}）：{Truncate(body)}")
{
    public HttpStatusCode Status { get; } = status;
    public string Body { get; } = body;

    private static string Truncate(string value) =>
        value.Length <= 300 ? value : value[..300] + "…";
}
