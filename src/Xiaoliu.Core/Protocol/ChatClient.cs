using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xiaoliu.Core.Configuration;
using Xiaoliu.Core.Tools;

namespace Xiaoliu.Core.Protocol;

/// <summary>
/// <c>POST {base}/chat</c> 的流式客户端。
///
/// 请求体字段照抄点点 <c>ModelStreamingClient.BuildRequestBody</c>：
/// model / max_tokens / stream / system / messages / main_reasoning_effort / tools / diandian_credits。
/// 自建服务器不校验 diandian_credits，但保留字段形状，方便将来接回真实上游。
/// </summary>
public sealed class ChatClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly AppSettings _settings;

    public ChatClient(AppSettings settings, HttpMessageHandler? handler = null)
    {
        _settings = settings;
        _http = handler is null ? new HttpClient(CreateHandler(settings)) : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(5);
    }

    public static HttpMessageHandler CreateHandler(AppSettings settings)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        };

        if (settings.AllowUntrustedTls)
        {
            // v1 直连 127.0.0.1，自签证书的 SAN 是 botlearn 域名，主机名校验必然失败。
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        return handler;
    }

    public async Task<ChatRoundResult> StreamAsync(
        ChatRequest request,
        Action<string>? onAccumulatedText,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, WireProtocol.HttpUri(_settings.BaseUri, WireProtocol.ChatPath))
        {
            Content = new StringContent(BuildBody(request), Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation(WireProtocol.AuthorizationHeader, "Bearer " + _settings.AccessToken);
        message.Headers.TryAddWithoutValidation(WireProtocol.ClientBuildHeader, WireProtocol.ClientBuild);

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
            body["main_reasoning_effort"] = request.ReasoningEffort;
        }

        // 只在「当前模型自己能看图」时发。服务端据此把图片块原样透传，
        // 不再跑 VL 描述 / OCR —— 那是给纯文本上游准备的前处理。
        // 不发的效果和不支持视觉一样（照旧转文本），所以点点那条链路不受影响。
        if (request.VisionCapable)
        {
            body["vision_capable"] = true;
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

    /// <summary>
    /// 当前对话模型自己能看图（<see cref="ModelCatalog"/> 里的 Vision 组）。
    /// 为 true 时服务端直连透传图片；为 false 时服务端先转成文字再发。
    /// </summary>
    public bool VisionCapable { get; init; }
}

public sealed class ChatHttpException(HttpStatusCode status, string body)
    : Exception($"chat 请求失败（HTTP {(int)status}）：{Truncate(body)}")
{
    public HttpStatusCode Status { get; } = status;
    public string Body { get; } = body;

    private static string Truncate(string value) =>
        value.Length <= 300 ? value : value[..300] + "…";
}
