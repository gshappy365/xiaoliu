using System.Text.Json.Nodes;
using Xiaoliu.Core.Configuration;
using Xiaoliu.Core.Protocol;
using Xunit;

namespace Xiaoliu.Core.Tests;

/// <summary>
/// 请求体里那个 <c>vision_capable</c> 字段决定了服务端「把图透传」还是「先转成文字」。
/// 它只在视觉组模型上出现：发错了会让纯文本模型收到它看不见的图片块，
/// 或者让能看图的模型白等 3.4 秒的 VL 描述。所以两个方向都钉住。
/// </summary>
public sealed class ChatRequestBodyTests
{
    private static ChatRequest Request(bool visionCapable) => new()
    {
        Model = "doubao-seed-2-1-pro-260915",
        System = "sys",
        Messages = [new JsonObject { ["role"] = "user", ["content"] = "你好" }],
        VisionCapable = visionCapable,
    };

    [Fact]
    public void Vision_capable_model_asks_server_to_pass_images_through()
    {
        var body = JsonNode.Parse(ChatClient.BuildBody(Request(visionCapable: true)))!;

        Assert.True(body["vision_capable"]!.GetValue<bool>());
    }

    [Fact]
    public void Text_only_model_does_not_send_the_flag()
    {
        // 字段缺席 = 让服务端照旧转文本。点点从来不发这个字段，行为因此不变。
        var body = JsonNode.Parse(ChatClient.BuildBody(Request(visionCapable: false)))!;

        Assert.Null(body["vision_capable"]);
    }

    [Fact]
    public void Vision_flag_follows_the_model_catalog()
    {
        // 真正决定发不发的是清单里的 Vision 标记，这里锁住那层映射：
        // 视觉组为 true，纯文本组为 false。
        Assert.True(ModelCatalog.Vision.Count > 0);
        Assert.All(ModelCatalog.Vision, m => Assert.True(m.Vision, m.Id));
        Assert.All(ModelCatalog.TextOnly, m => Assert.False(m.Vision, m.Id));
    }
}
