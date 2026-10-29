using Xiaoliu.Core.Capture;
using Xunit;

namespace Xiaoliu.Core.Tests;

public class ScreenSignatureTests
{
    [Fact]
    public void IdenticalSignaturesDifferByZero()
    {
        var a = new byte[1024];
        Assert.Equal(0, ScreenCapture.Difference(a, (byte[])a.Clone()));
    }

    [Fact]
    public void SmallLocalChangeStaysBelowStaleThreshold()
    {
        var a = new byte[1024];
        var b = new byte[1024];
        // 只有 40 个格子（约 3.9%）变了：光标闪一下、字幕条换一行都属于这种量级。
        for (var i = 0; i < 40; i++)
        {
            b[i] = 255;
        }

        var difference = ScreenCapture.Difference(a, b);
        Assert.True(difference < ScreenCapture.StaleThreshold, $"变化 {difference:P1} 应低于阈值");
    }

    [Fact]
    public void PageSwitchIsDetectedAsStale()
    {
        var a = new byte[1024];
        var b = new byte[1024];
        for (var i = 0; i < 600; i++)
        {
            b[i] = 200;
        }

        Assert.True(ScreenCapture.Difference(a, b) > ScreenCapture.StaleThreshold);
    }

    [Fact]
    public void MismatchedLengthsAreTreatedAsTotallyDifferent()
    {
        Assert.Equal(1, ScreenCapture.Difference(new byte[1024], new byte[512]));
    }

    [Fact]
    public void EvidenceWithoutSignatureIsNeverValid()
    {
        var evidence = new VisualEvidence((_, _) => true);
        var shot = evidence.Add(new CaptureRect(0, 0, 10, 10), new CapturedImage([1], "image/jpeg", 10, 10, "h"));

        Assert.False(evidence.StillValid(shot));
    }
}
