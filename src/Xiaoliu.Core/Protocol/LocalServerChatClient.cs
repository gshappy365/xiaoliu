using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xiaoliu.Core.Configuration;
using Xiaoliu.Core.Tools;

namespace Xiaoliu.Core.Protocol;

/// <summary>
/// 本地服务器的对话传输（ADR 0012）。走 <c>POST {BaseUrl}/chat</c>，
/// 服务器端把请求转给上游的 Anthropic Messages 兼容端点。
///
/// 与 <see cref="ArkChatClient"/> 的差别只有三处，其余（SSE 解析、工具回环、
/// 圈选图编码）完全共用 —— 因为服务器端的 chat 端点本身就是 Anthropic Messages
/// 协议（见 rea/server/app/main.py）。
///
/// <b>请求体里的私有字段是有意发的</b>：服务器端的 <c>rewrite_request</c> 读
/// <c>main_reasoning_effort</c> 与 <c>vision_capable</c> 来决定上游的思考预算和
/// 图片透传方式，转发前才把它们丢掉。不发这两个字段的后果是：
/// 图片被降级成文字描述（模型看到的是「一张模糊的界面截图」而不是像素），
/// 思考预算退回默认值。
/// </summary>
public sealed class LocalServerChatClient : IChatTransport, IDisposable
{
    private readonly HttpClient _http;
    private readonly AppSettings _settings;

    public LocalServerChatClient(AppSettings settings, HttpMessageHandler? handler = null)
    {
        _settings = settings;
        _http = handler is null ? new HttpClient(CreateHandler(settings)) : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// 服务器端用的是自签证书（SAN 是 botlearn 域名），走 127.0.0.1 时主机名校验必然失败。
    /// 是否放行由设置里的「允许不信任证书」决定。
    /// </summary>
    public static HttpMessageHandler CreateHandler(AppSettings settings)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        };

        if (settings.AllowUntrustedTls)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        return handler;
    }

    public async Task<ChatRoundResult> StreamAsync(
        ChatRequest request,
        Action<string>? onAccumulatedText,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post, WireProtocol.HttpUri(_settings.ServerBaseUri, WireProtocol.ChatPath))
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

    /// <summary>
    /// 请求体。Anthropic Messages 形状 + 服务器端读的那几个私有字段。
    ///
    /// 刻意<b>不</b>发协议原生的 <c>thinking</c>：思考预算由服务器端按
    /// <c>main_reasoning_effort</c> 统一决定，两边都设会打架。
    /// </summary>
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

        // 只在「当前模型自己能看图」时发。服务器端据此把图片块原样透传，
        // 不跑 VL 描述 / OCR —— 那是给纯文本上游准备的前处理。
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
