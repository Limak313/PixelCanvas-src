using PixelCanvas;
using Xunit;

namespace PixelCanvas.Tests;

/// <summary>播放时钟：帧号由累计耗时反推，播放速率不随定时器精度累积漂移。</summary>
public class AnimationClockTests
{
    [Fact]
    public void StartTime_ShowsStartFrame()
    {
        Assert.Equal(2, AnimationClock.FrameIndex(0, 8, 5, startFrame: 2));
    }

    [Fact]
    public void ElapsedTime_AdvancesByFps()
    {
        // 8 fps：125 ms 前进 1 帧，1000 ms 前进 8 帧
        Assert.Equal(1, AnimationClock.FrameIndex(125, 8, 6, startFrame: 0));
        Assert.Equal(2, AnimationClock.FrameIndex(250, 8, 6, startFrame: 0));
        Assert.Equal(2, AnimationClock.FrameIndex(1000, 8, 6, startFrame: 0));
    }

    [Fact]
    public void NoDrift_OverLongPlayback()
    {
        // 60 fps 播 10 秒应为 600 帧；7 帧循环 → 600 % 7 = 5，与逐帧累加相比不受定时器取整影响
        Assert.Equal(5, AnimationClock.FrameIndex(10_000, 60, 7, startFrame: 0));
        // 30 fps 播 10 秒 = 300 帧；5 帧循环 → 0
        Assert.Equal(0, AnimationClock.FrameIndex(10_000, 30, 5, startFrame: 0));
    }

    [Fact]
    public void StartFrame_OffsetWrapsAround()
    {
        // 从第 3 帧起播 250 ms（8 fps 走 2 帧），5 帧循环 → (3+2)%5 = 0
        Assert.Equal(0, AnimationClock.FrameIndex(250, 8, 5, startFrame: 3));
    }

    [Fact]
    public void DegenerateInputs_AreClamped()
    {
        Assert.Equal(0, AnimationClock.FrameIndex(5000, 8, 1, startFrame: 0));      // 只有一帧：停在起始帧
        Assert.Equal(0, AnimationClock.FrameIndex(-100, 8, 4, startFrame: 0));     // 负耗时按起点处理
    }

    [Theory]
    [InlineData(60, 8)]
    [InlineData(30, 16)]
    [InlineData(8, 60)]
    [InlineData(1, 60)]
    public void PollInterval_IsHalfFrameWithinBounds(int fps, int expected)
    {
        Assert.Equal(expected, AnimationClock.PollIntervalMs(fps));
    }
}
