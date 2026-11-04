using Xiaoliu.Core.Configuration;
using Xunit;

namespace Xiaoliu.Core.Tests;

/// <summary>
/// 模型清单是手工从 arkcli 抄下来的，最容易出的错是抄重复、把 id 抄成显示名、
/// 或者把纯文本模型混进「能看图」那一组 —— 用户按那个分组选模型，选错了
/// 圈选和截屏提问就全废。这几条钉住这些性质。
/// </summary>
public sealed class ModelCatalogTests
{
    [Fact]
    public void Ids_are_unique_across_all_groups()
    {
        var ids = ModelCatalog.All.Select(m => m.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Every_entry_has_id_and_display_name()
    {
        foreach (var model in ModelCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(model.Id), "Id 不能为空");
            Assert.False(string.IsNullOrWhiteSpace(model.Display), $"{model.Id} 缺显示名");

            // id 是发出去的标识，显示名是给人看的，两者抄串了很难发现 —— 这里挡一道。
            Assert.NotEqual(model.Id, model.Display);
        }
    }

    [Fact]
    public void Vision_group_is_all_vision_and_text_group_is_not()
    {
        Assert.NotEmpty(ModelCatalog.Vision);
        Assert.NotEmpty(ModelCatalog.TextOnly);

        Assert.All(ModelCatalog.Vision, m => Assert.True(m.Vision, $"{m.Id} 在视觉组但标了 false"));
        Assert.All(ModelCatalog.TextOnly, m => Assert.False(m.Vision, $"{m.Id} 在文本组但标了 true"));
    }

    [Fact]
    public void Every_id_is_an_ark_model_sent_verbatim()
    {
        Assert.NotEmpty(ModelCatalog.All);

        // 云直连后（ADR 0011）清单里的 id 原样发给方舟的 Anthropic 兼容端点，
        // 没有服务器再兜底路由。这组前缀是「这确实是方舟模型」的保守校验：
        // 自定义端点的模型走 CustomModelName，不该混进这份清单。
        string[] prefixes = ["doubao-", "deepseek-", "qwen", "glm-", "kimi-"];

        Assert.All(ModelCatalog.All, m =>
            Assert.True(prefixes.Any(p => m.Id.StartsWith(p, StringComparison.OrdinalIgnoreCase)),
                $"{m.Id} 不像方舟模型，直连会被上游拒绝"));
    }

    [Fact]
    public void Find_is_case_insensitive_and_returns_null_for_unknown()
    {
        var hit = ModelCatalog.Find("DOUBAO-SEED-2-1-PRO-260915");
        Assert.NotNull(hit);
        Assert.Equal("Doubao-Seed-2.1-pro", hit!.Display);

        Assert.Null(ModelCatalog.Find("some-model-that-does-not-exist"));
    }
}
