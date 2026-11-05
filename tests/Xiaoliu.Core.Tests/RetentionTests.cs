using Xiaoliu.Core.Capture;
using Xiaoliu.Core.Configuration;
using Xiaoliu.Core.Storage;
using Xunit;

namespace Xiaoliu.Core.Tests;

public sealed class RetentionTests : IDisposable
{
    private readonly string _dbPath;

    public RetentionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "xiaoliu-tests", Guid.NewGuid().ToString("N") + ".db");
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    private static CapturedImage Image(int size = 1024) => new(new byte[size], "image/jpeg", 64, 64, "h");

    [Fact]
    public void TextAndImageRetentionAreIndependent()
    {
        var settings = new AppSettings { TextRetentionDays = 90, ImageRetentionDays = 7 };
        using var store = new HistoryStore(_dbPath);

        var old = store.AddEntry("十天前的问题", "十天前的回答", [Image()], DateTimeOffset.UtcNow.AddDays(-10));
        var fresh = store.AddEntry("今天的问题", "今天的回答", [Image()]);

        var report = store.Sweep(settings);

        Assert.Equal(1, report.DeletedImages);   // 图片超过 7 天，删
        Assert.Equal(0, report.DeletedTexts);    // 文本还在 90 天内，留
        var recent = store.Recent();
        Assert.Equal(2, recent.Count);
        Assert.Equal(0, recent.Single(e => e.Id == old).ScreenshotCount);
        Assert.Equal(1, recent.Single(e => e.Id == fresh).ScreenshotCount);
    }

    [Fact]
    public void TextOlderThanWindowIsDeleted()
    {
        var settings = new AppSettings { TextRetentionDays = 90, ImageRetentionDays = 7 };
        using var store = new HistoryStore(_dbPath);
        store.AddEntry("很久以前", "回答", [], DateTimeOffset.UtcNow.AddDays(-120));

        var report = store.Sweep(settings);

        Assert.Equal(1, report.DeletedTexts);
        Assert.Empty(store.Recent());
    }

    [Fact]
    public void TotalSizeCapEvictsOldestImagesFirst()
    {
        var settings = new AppSettings { TextRetentionDays = 3650, ImageRetentionDays = 3650, MaxTotalMegabytes = 1 };
        using var store = new HistoryStore(_dbPath);

        for (var i = 0; i < 6; i++)
        {
            store.AddEntry($"问题 {i}", "回答", [Image(300 * 1024)]);
        }

        Assert.True(store.TotalBytes() > 1024 * 1024);

        var report = store.Sweep(settings);

        Assert.True(report.EvictedImages > 0);
        Assert.Equal(0, report.EvictedTexts);
        Assert.True(store.TotalBytes() <= 1024 * 1024);
        // 文本一条都没丢：先删图，不删人看得见的内容
        Assert.Equal(6, store.Recent().Count);
    }

    [Fact]
    public void SearchFindsByQuestionAndAnswer()
    {
        using var store = new HistoryStore(_dbPath);
        store.AddEntry("怎么报销差旅", "在财务系统里提交", []);

        Assert.Single(store.Search("差旅"));
        Assert.Single(store.Search("财务系统"));
        Assert.Empty(store.Search("不存在的词"));
    }

    [Fact]
    public void FactsAreDeduplicatedByContent()
    {
        using var store = new HistoryStore(_dbPath);
        store.AddFact("identity", "用户叫张三。");
        store.AddFact("identity", "用户叫张三");

        Assert.Single(store.Facts());
    }
}
