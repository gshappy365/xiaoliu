using System.Text;
using Xiaoliu.Core.Configuration;
using Xunit;

namespace Xiaoliu.Core.Tests;

/// <summary>
/// 云凭证落盘保护（ADR 0011）。真实 DPAPI 只在 Windows 上有，单测里换上可逆替身，
/// 钉住的是存储格式约定与幂等性 —— 真加密本身是平台 API，不归测试管。
/// </summary>
public sealed class SecretProtectorTests : IDisposable
{
    public SecretProtectorTests()
    {
        // 替身用与真实现相同的 dpapi:1: 标记，幂等闸（IsProtected）才能按同一套规则工作。
        // Protect 产出完整存储格式；Unprotect 收到的载荷已经被真实现剥掉 "dpapi:1:" 前缀。
        const string storedMarker = "dpapi:1:TEST-";
        const string payloadMarker = "TEST-";
        SecretProtector.ProtectOverride = plain => storedMarker + Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));
        SecretProtector.UnprotectOverride = stored =>
        {
            if (stored is null || !stored.StartsWith(payloadMarker, StringComparison.Ordinal))
            {
                return null;
            }

            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(stored[payloadMarker.Length..]));
            }
            catch (FormatException)
            {
                return null;
            }
        };
    }

    public void Dispose()
    {
        SecretProtector.ProtectOverride = null;
        SecretProtector.UnprotectOverride = null;
    }

    [Fact]
    public void Protect_wraps_and_unprotect_restores()
    {
        var stored = SecretProtector.Protect("sk-abc");

        Assert.StartsWith("dpapi:1:TEST-", stored);
        Assert.Equal("sk-abc", SecretProtector.Unprotect(stored));
    }

    [Fact]
    public void Protect_is_idempotent()
    {
        // 双重加密会让密文永远解不回来：对已受保护的串必须原样返回。
        var once = SecretProtector.Protect("sk-abc");
        Assert.Equal(once, SecretProtector.Protect(once));
    }

    [Fact]
    public void Corrupt_ciphertext_yields_null_not_an_empty_string()
    {
        // 换机器 / 密文损坏：null 让调用方给出「请重填」提示；空串会被当成「没配」混过去。
        Assert.Null(SecretProtector.Unprotect("dpapi:1:!!!not-base64!!!"));
    }

    [Fact]
    public void Plaintext_passthrough_and_empty_handling()
    {
        Assert.Equal("legacy", SecretProtector.Unprotect("legacy"));
        Assert.Null(SecretProtector.Unprotect(null));
        Assert.Null(SecretProtector.Unprotect(""));
        Assert.False(SecretProtector.IsProtected("sk-plain"));
        Assert.True(SecretProtector.IsProtected("dpapi:1:TEST-aaaa"));
    }

    [Fact]
    public void Settings_roundtrip_keeps_protected_secrets_encrypted()
    {
        var settings = new AppSettings
        {
            ArkApiKey = SecretProtector.Protect("sk-ark"),
            VoiceAccessToken = SecretProtector.Protect("tok"),
            CustomModelName = "deepseek-chat",
        };
        settings.Normalize();

        Assert.Equal("sk-ark", settings.ArkApiKeyPlain);
        Assert.Equal("tok", settings.VoiceAccessTokenPlain);
        Assert.False(settings.IsCustomModel);
        // Model 还是默认的方舟模型：UpstreamModel 不该被未选中的自定义模型名污染。
        Assert.Equal("doubao-seed-2-1-pro-260915", settings.UpstreamModel);
    }

    [Fact]
    public void Custom_model_selection_routes_upstream_model()
    {
        var settings = new AppSettings { Model = AppSettings.CustomModelKey, CustomModelName = "glm-5" };

        Assert.True(settings.IsCustomModel);
        Assert.Equal("glm-5", settings.UpstreamModel);
    }

    [Fact]
    public void Legacy_server_dialect_voice_id_is_normalized()
    {
        // 旧 settings.json 里的 volcengine.xiaohe 是服务器方言，直连后上游不认识。
        var settings = new AppSettings { TtsVoiceId = "volcengine.xiaohe" };
        settings.Normalize();

        Assert.Equal("zh_female_tianmeixiaoyuan_moon_bigtts", settings.TtsVoiceId);
    }
}
