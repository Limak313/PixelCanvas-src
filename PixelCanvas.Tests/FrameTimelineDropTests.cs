using PixelCanvas;
using Xunit;

namespace PixelCanvas.Tests;

/// <summary>时间轴落点判定：抽离鼠标捕获与屏幕拖拽阈值后的纯函数用例。</summary>
public class FrameTimelineDropTests
{
    [Fact]
    public void DragAcrossFrames_MovesToClampedTarget()
    {
        var drop = FrameTimeline.ResolveDrop(pressed: 0, downX: 10, upX: 170, frameCount: 3, dragThreshold: 4);
        Assert.Equal(0, drop.Pressed);
        Assert.Equal(2, drop.Target);
        Assert.True(drop.Move);
    }

    [Fact]
    public void DragBeyondRightEdge_ClampsToLastFrame()
    {
        var drop = FrameTimeline.ResolveDrop(0, 10, 999, 3, 4);
        Assert.Equal(2, drop.Target);
        Assert.True(drop.Move);
    }

    [Fact]
    public void ClickWithinSameCell_SelectsWithoutMoving()
    {
        var drop = FrameTimeline.ResolveDrop(0, 10, 70, 3, 4);
        Assert.Equal(0, drop.Target);
        Assert.False(drop.Move);
    }

    [Fact]
    public void DragShorterThanThreshold_DoesNotMove()
    {
        var drop = FrameTimeline.ResolveDrop(0, 10, 170, 3, dragThreshold: 10_000);
        Assert.Equal(0, drop.Pressed);
        Assert.False(drop.Move);
    }

    [Fact]
    public void NoPressedFrame_DoesNothing()
    {
        var drop = FrameTimeline.ResolveDrop(-1, 10, 170, 3, 4);
        Assert.Equal(-1, drop.Pressed);
        Assert.False(drop.Move);
    }

    [Fact]
    public void PressedFrameOutOfRange_DoesNothing()
    {
        var drop = FrameTimeline.ResolveDrop(5, 10, 170, 3, 4);
        Assert.Equal(-1, drop.Pressed);
        Assert.False(drop.Move);
    }
}
