using System.Security.Cryptography;
using System.Text;

namespace Xiaoliu.Core.Configuration;

/// <summary>
/// 云凭证的落盘保护。云直连后客户端第一次持有真实密钥（方舟 API key、火山语音
/// appid/token），按 ADR 0011 用 Windows DPAPI（CurrentUser）加密后存进
/// <c>settings.json</c>，不进配置明文。
///
/// 存储格式：<c>dpapi:1:&lt;base64 密文&gt;</c>。前缀既是标记也是幂等闸 ——
/// 对同一个值重复 Protect 不会套两层。
///
/// DPAPI 绑定当前用户：备份/迁移 settings.json 不会带走密钥，换机器要重填。
/// 解不开（换机器、重装系统、密文损坏）返回 <c>null</c>，由调用方提示重填，
/// 绝不静默当成空串。
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "dpapi:1:";

    /// <summary>测试替身挂点：非 Windows 环境跑单测时替换成可逆的假实现。</summary>
    internal static Func<string, string>? ProtectOverride;
    internal static Func<string, string?>? UnprotectOverride;

    public static bool IsProtected(string? value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>明文 → 受保护串。已是受保护串时原样返回（幂等）。</summary>
    public static string Protect(string plaintext)
    {
        if (IsProtected(plaintext))
        {
            return plaintext;
        }

        if (ProtectOverride is not null)
        {
            return ProtectOverride(plaintext);
        }

        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(cipher);
    }

    /// <summary>受保护串 → 明文。不是受保护串就当明文返回；解不开返回 null。</summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        if (!IsProtected(stored))
        {
            return stored;
        }

        if (UnprotectOverride is not null)
        {
            return UnprotectOverride(stored[Prefix.Length..]);
        }

        try
        {
            var cipher = Convert.FromBase64String(stored[Prefix.Length..]);
            var plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or NotSupportedException
                                   or PlatformNotSupportedException)
        {
            // 换机器 / 非 Windows / 密文损坏：调用方要给出「请重新填写」的明确提示。
            return null;
        }
    }
}
