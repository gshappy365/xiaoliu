using Xiaoliu.Core.Capture;
using Xunit;

namespace Xiaoliu.Core.Tests;

/// <summary>
/// 证据池里现在有两种东西：抓屏来的圈选图，和用户从磁盘选的图。
/// 前者按内容判活（ADR 0003），后者没有「那块屏幕变了没有」这回事 ——
/// 如果按同一套规则判，选来的图会在下一次 Prune 里被当成过期图删掉。
/// </summary>
public sealed class VisualEvidenceTests
{
    private static CapturedImage FakeImage(byte signatureByte) =>
        new([1, 2, 3], "image/jpeg", 10, 10, "ABCDEF01", [signatureByte]);

    [Fact]
    public void PickedImageIsNeverPruned()
    {
        // 桩说「画面全变了」，选图仍然不该被删。
        var evidence = new VisualEvidence((_, _) => false);
        evidence.AddImage(FakeImage(9));

        Assert.Equal(0, evidence.Prune());
        Assert.Single(evidence.Shots);
    }

    [Fact]
    public void PickedImageCountsAsValid()
    {
        var evidence = new VisualEvidence((_, _) => false);
        var shot = evidence.AddImage(FakeImage(9));

        Assert.True(evidence.StillValid(shot));
    }

    [Fact]
    public void CaptureIsPrunedWhenScreenChanged()
    {
        var evidence = new VisualEvidence((_, _) => false);
        evidence.Add(new CaptureRect(0, 0, 100, 100), FakeImage(1));

        Assert.Equal(1, evidence.Prune());
        Assert.Empty(evidence.Shots);
    }

    [Fact]
    public void CaptureSurvivesWhenScreenUnchanged()
    {
        var evidence = new VisualEvidence((_, _) => true);
        evidence.Add(new CaptureRect(0, 0, 100, 100), FakeImage(1));

        Assert.Equal(0, evidence.Prune());
        Assert.Single(evidence.Shots);
    }
}
