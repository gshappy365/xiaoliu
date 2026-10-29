using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Xiaoliu.Core.Protocol;

/// <summary>
/// 统一的 JSON 序列化参数。
///
/// 中文必须原样输出（<see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>）：
/// 默认编码器会把中文转成 \uXXXX，token 数翻好几倍，而且服务器日志里没法看。
/// <c>TypeInfoResolver</c> 不能省 —— <c>JsonNode.ToJsonString(options)</c> 在缺少它时会抛异常。
/// </summary>
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Wire = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };
}
