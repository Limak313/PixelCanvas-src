using System.Drawing;
using PixelCanvas;
using Xunit;

namespace PixelCanvas.Tests;

/// <summary>新增导出格式：SVG 矢量色块（合并同类矩形、忽略透明）与逐帧 PNG 序列。</summary>
public class ExportFormatsTests
{
    private static readonly int Red = Argb.FromRgb(0xFF0000);
    private static readonly int Blue = Argb.FromRgb(0x0000FF);

    [Fact]
    public void Svg_MergesSameColorPixelsAndSkipsTransparent()
    {
        using var dir = new TempDir();
        var grid = new PixelGrid(6, 6);
        for (int y = 1; y < 4; y++) for (int x = 1; x < 4; x++) grid.Set(x, y, Red);
        grid.Set(4, 2, Blue);
        string path = dir.File("art.svg");

        int rectangles = SvgExport.Save(path, grid.RawPixels, 6, 6);

        string svg = File.ReadAllText(path);
        Assert.Contains("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"6\" height=\"6\"", svg);
        Assert.Contains("<rect x=\"1\" y=\"1\" width=\"3\" height=\"3\" fill=\"#FF0000\"/>", svg);
        Assert.Contains("<rect x=\"4\" y=\"2\" width=\"1\" height=\"1\" fill=\"#0000FF\"/>", svg);
        Assert.DoesNotContain("fill=\"#000000\"", svg);
        Assert.Equal(2, rectangles);
        Assert.EndsWith("</svg>\n", svg);
    }

    [Fact]
    public void Svg_RejectsPixelCountMismatch()
    {
        using var dir = new TempDir();
        Assert.Throws<ArgumentException>(() => SvgExport.Save(dir.File("bad.svg"), new int[10], 4, 4));
    }

    [Fact]
    public void Svg_CompositesVisibleLayersOnly()
    {
        using var dir = new TempDir();
        var bottom = new PixelGrid(4, 4);
        for (int i = 0; i < bottom.RawPixels.Length; i++) bottom.RawPixels[i] = Red;
        var top = new PixelGrid(4, 4);
        top.Set(0, 0, Blue);
        var hidden = new Layer("hidden", new PixelGrid(4, 4), visible: false);
        hidden.Grid.Set(3, 3, Blue);
        var frame = new AnimationFrame(new List<Layer> { new("bottom", bottom), new("top", top), hidden });

        int rectangles = SvgExport.Save(dir.File("layers.svg"), FrameComposite.Flatten(frame, 4, 4), 4, 4);

        string svg = File.ReadAllText(dir.File("layers.svg"));
        Assert.Contains("fill=\"#FF0000\"", svg);
        Assert.Contains("x=\"0\" y=\"0\" width=\"1\" height=\"1\" fill=\"#0000FF\"", svg);
        Assert.DoesNotContain("x=\"3\" y=\"3\"", svg);   // 隐藏图层不参与合成
        Assert.True(rectangles >= 2);
    }

    [Fact]
    public void FrameSequence_WritesNumberedPngFiles()
    {
        using var dir = new TempDir();
        var frames = new List<AnimationFrame>();
        for (int f = 0; f < 3; f++)
        {
            var grid = new PixelGrid(4, 4);
            grid.Set(f, 0, Red);
            frames.Add(new AnimationFrame(new List<Layer> { new($"f{f}", grid) }));
        }
        string directory = Path.Combine(dir.Path, "frames");

        var result = FrameSequenceExport.Save(directory, "walk", frames, scale: 2);

        Assert.Equal(3, result.FrameCount);
        Assert.Equal(3, result.Files.Length);
        Assert.Equal("walk-001.png", Path.GetFileName(result.Files[0]));
        Assert.Equal("walk-003.png", Path.GetFileName(result.Files[2]));
        foreach (string file in result.Files)
        {
            using var image = Image.FromFile(file);
            Assert.Equal(8, image.Width);
            Assert.Equal(8, image.Height);
        }
    }

    [Fact]
    public void FrameSequence_RefusesNonEmptyDirectoryAndBadNames()
    {
        using var dir = new TempDir();
        var frames = new List<AnimationFrame> { new(new List<Layer> { new("l", new PixelGrid(4, 4)) }) };
        string directory = Path.Combine(dir.Path, "frames");
        FrameSequenceExport.Save(directory, "frame", frames);

        Assert.Throws<IOException>(() => FrameSequenceExport.Save(directory, "frame", frames));
        Assert.Throws<ArgumentException>(() => FrameSequenceExport.Save(Path.Combine(dir.Path, "f2"), "a/b", frames));
        Assert.Throws<ArgumentException>(() => FrameSequenceExport.Save(Path.Combine(dir.Path, "f3"), "frame", frames, scale: 17));
    }
}
