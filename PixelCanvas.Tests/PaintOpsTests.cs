using PixelCanvas;
using Xunit;

namespace PixelCanvas.Tests;

public class PaintOpsTests
{
    [Fact]
    public void MirrorPoints_DualAxis_ProducesFourSymmetricPoints()
    {
        var grid = new PixelGrid(5, 5);
        var points = PaintOps.MirrorPoints(grid, 0, 1, mirrorH: true, mirrorV: true);

        Assert.Equal(4, points.Count);
        Assert.Contains((0, 1), points);
        Assert.Contains((4, 1), points);
        Assert.Contains((0, 3), points);
        Assert.Contains((4, 3), points);
    }

    [Fact]
    public void MirrorPoints_SingleAxis_DeduplicatesCenter()
    {
        var grid = new PixelGrid(5, 5);
        var points = PaintOps.MirrorPoints(grid, 2, 0, mirrorH: true, mirrorV: false);

        Assert.Single(points);
        Assert.Contains((2, 0), points);
    }

    [Fact]
    public void ApplyBrush_PaintsBrushSizeAndIgnoresOutOfBounds()
    {
        var grid = new PixelGrid(4, 4);
        var color = Argb.FromRgb(0xFF0000);

        Assert.True(PaintOps.ApplyBrush(grid, -1, -1, 2, color, false, false));
        Assert.Equal(color, grid.Get(0, 0));
        Assert.Equal(Argb.Transparent, grid.Get(3, 3));
    }

    [Fact]
    public void FloodFill_FillsConnectedSameColorRegion()
    {
        var grid = new PixelGrid(3, 3);
        PaintOps.ApplyBrush(grid, 0, 0, 1, Argb.FromRgb(0xFFFFFF), false, false);
        PaintOps.ApplyBrush(grid, 2, 2, 1, Argb.FromRgb(0x000000), false, false);

        Assert.True(PaintOps.FloodFill(grid, 0, 0, Argb.FromRgb(0xFF0000), false, false));
        Assert.Equal(Argb.FromRgb(0xFF0000), grid.Get(0, 0));
        Assert.Equal(Argb.Transparent, grid.Get(1, 1));
        Assert.Equal(Argb.FromRgb(0x000000), grid.Get(2, 2));
    }

    [Fact]
    public void FloodFill_MirrorFillsSymmetricRegions()
    {
        var grid = new PixelGrid(5, 1);
        PaintOps.ApplyBrush(grid, 0, 0, 1, Argb.FromRgb(0xFFFFFF), false, false);
        PaintOps.ApplyBrush(grid, 4, 0, 1, Argb.FromRgb(0xFFFFFF), false, false);

        PaintOps.FloodFill(grid, 0, 0, Argb.FromRgb(0x00FF00), mirrorH: true, false);
        Assert.Equal(Argb.FromRgb(0x00FF00), grid.Get(0, 0));
        Assert.Equal(Argb.FromRgb(0x00FF00), grid.Get(4, 0));
    }

    [Fact]
    public void DrawShape_Line_IncludesBothEndpoints()
    {
        var grid = new PixelGrid(4, 1);
        var color = Argb.FromRgb(0x0000FF);

        Assert.True(PaintOps.DrawShape(grid, ToolKind.Line, 0, 0, 3, 0, 1, color, false, false));
        Assert.Equal(color, grid.Get(0, 0));
        Assert.Equal(color, grid.Get(3, 0));
    }
}
