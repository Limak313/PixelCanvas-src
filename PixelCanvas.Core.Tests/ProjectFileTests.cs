using System.Text.Json;
using PixelCanvas;
using Xunit;

namespace PixelCanvas.Tests;

public class ProjectFileTests
{
    private static string JsonWithPalette(int count, string version = "2", string? layers = null)
    {
        var layersJson = layers ?? $"[{{\"name\":\"layer\",\"visible\":true,\"pixels\":[\"1x00000000\"]}}]";
        return JsonSerializer.Serialize(new
        {
            format = ProjectFile.FormatName,
            version = int.Parse(version),
            width = 1,
            height = 1,
            palette = Enumerable.Repeat("ABCDEF", count).ToArray(),
            layers = JsonSerializer.Deserialize<object[]>(layersJson),
            activeLayer = 0
        });
    }

    private static PixelGrid GridWith(int x, int y, int color)
    {
        var grid = new PixelGrid(4, 4);
        grid.Set(x, y, Argb.FromRgb(color));
        return grid;
    }

    [Fact]
    public void SaveLoad_SingleFrame_RoundTripsPixels()
    {
        using var dir = new TempDir();
        var path = dir.File("single.pxproj");
        var grid = GridWith(1, 2, 0x123456);
        ProjectFile.Save(path, new[] { new Layer("测试", grid) }, 0, new[] { "ABCDEF" });

        var loaded = ProjectFile.TryLoad(path, out var error);
        Assert.NotNull(loaded);
        Assert.Equal(Argb.FromRgb(0x123456), loaded!.Grid.Get(1, 2));
        Assert.Equal("", error);
    }

    [Fact]
    public void SaveLoad_Animation_PreservesFramesActiveFrameFps()
    {
        using var dir = new TempDir();
        var path = dir.File("animation.pxproj");
        var first = GridWith(1, 2, 0x123456);
        var second = new PixelGrid(4, 4);
        second.Set(3, 1, Argb.FromRgb(0xFEDCBA));
        var frames = new List<AnimationFrame>
        {
            new(new List<Layer> { new("first", first) }),
            new(new List<Layer> { new("second", second) })
        };
        ProjectFile.Save(path, frames[1].Layers, 0, new[] { "ABCDEF" }, frames: frames, activeFrame: 1, framesPerSecond: 12);

        var loaded = ProjectFile.TryLoad(path, out _);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Frames.Count);
        Assert.Equal(1, loaded.ActiveFrame);
        Assert.Equal(12, loaded.FramesPerSecond);
        Assert.Equal(Argb.FromRgb(0x123456), loaded.Frames[0].Layers[0].Grid.Get(1, 2));
        Assert.Equal(Argb.FromRgb(0xFEDCBA), loaded.Grid.Get(3, 1));
    }

    [Fact]
    public void Load_LegacyVersion1_ReadsPixels()
    {
        using var dir = new TempDir();
        var path = dir.File("legacy.pxproj");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            format = ProjectFile.FormatName,
            version = 1,
            width = 1,
            height = 1,
            palette = new[] { "ABCDEF" },
            pixels = new[] { "1xABCDEFff" }
        }));

        var loaded = ProjectFile.TryLoad(path, out _);
        Assert.NotNull(loaded);
        Assert.Equal(Argb.FromRgb(0xABCDEF), loaded!.Grid.Get(0, 0));
    }

    [Fact]
    public void Load_Version2Layers_ReadsLayers()
    {
        using var dir = new TempDir();
        var path = dir.File("v2.pxproj");
        File.WriteAllText(path, JsonWithPalette(1, "2"));

        var loaded = ProjectFile.TryLoad(path, out var error);
        Assert.NotNull(loaded);
        Assert.Single(loaded!.Layers);
        Assert.Equal("layer", loaded.Layers[0].Name);
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData("{not json", "不是有效的 JSON")]
    public void Load_CorruptJson_Rejected(string content, string reason)
    {
        using var dir = new TempDir();
        var path = dir.File("corrupt.pxproj");
        File.WriteAllText(path, content);

        Assert.Null(ProjectFile.TryLoad(path, out var error));
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_BadVersion_Rejected()
    {
        using var dir = new TempDir();
        var path = dir.File("bad-version.pxproj");
        File.WriteAllText(path, JsonWithPalette(1).Replace("\"version\":2", "\"version\":99"));

        Assert.Null(ProjectFile.TryLoad(path, out var error));
        Assert.Contains("工程版本不受支持", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_BadRows_Rejected()
    {
        using var dir = new TempDir();
        var path = dir.File("bad-rows.pxproj");
        File.WriteAllText(path, JsonWithPalette(1).Replace("1x00000000", "2x00000000"));

        Assert.Null(ProjectFile.TryLoad(path, out var error));
        Assert.Contains("图层数据不合法", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MissingFile_Rejected()
    {
        using var dir = new TempDir();
        Assert.Null(ProjectFile.TryLoad(dir.File("missing.pxproj"), out var error));
        Assert.Contains("无法读取文件", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_OversizedFile_Rejected()
    {
        using var dir = new TempDir();
        var path = dir.File("oversized.pxproj");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            file.SetLength(ProjectFile.MaxFileBytes + 1);

        Assert.Null(ProjectFile.TryLoad(path, out var error));
        Assert.Contains($"超过 {ProjectFile.MaxFileBytes / 1024 / 1024} MiB", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Palette_AtLimit_Reads()
    {
        using var dir = new TempDir();
        var path = dir.File("palette-limit.pxproj");
        File.WriteAllText(path, JsonWithPalette(ProjectFile.MaxPaletteColors));

        var loaded = ProjectFile.TryLoad(path, out _);
        Assert.NotNull(loaded);
        Assert.Equal(ProjectFile.MaxPaletteColors, loaded!.Palette.Count);
    }

    [Fact]
    public void Palette_OverLimit_Rejected()
    {
        using var dir = new TempDir();
        var path = dir.File("palette-over.pxproj");
        File.WriteAllText(path, JsonWithPalette(ProjectFile.MaxPaletteColors + 1));

        Assert.Null(ProjectFile.TryLoad(path, out var error));
        Assert.Contains("palette 颜色数量", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_OverLimitPalette_ThrowsAndKeepsOriginal()
    {
        using var dir = new TempDir();
        var path = dir.File("normal.pxproj");
        var grid = GridWith(1, 2, 0x123456);
        ProjectFile.Save(path, new[] { new Layer("测试", grid) }, 0, new[] { "ABCDEF" });
        var original = File.ReadAllText(path);

        Assert.Throws<ArgumentException>(() =>
            ProjectFile.Save(path, new[] { new Layer("测试", grid) }, 0,
                Enumerable.Repeat("ABCDEF", ProjectFile.MaxPaletteColors + 1).ToArray()));
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void Load_BadFormat_Rejected()
    {
        using var dir = new TempDir();
        var path = dir.File("bad-format.pxproj");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            format = "other-project", version = 2, width = 1, height = 1,
            palette = new[] { "ABCDEF" }, layers = new[] { new { name = "l", visible = true, pixels = new[] { "1x00000000" } } }, activeLayer = 0
        }));

        Assert.Null(ProjectFile.TryLoad(path, out var error));
        Assert.Contains("format", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_PaletteInvalidColor_Rejected()
    {
        using var dir = new TempDir();
        var path = dir.File("bad-palette.pxproj");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            format = ProjectFile.FormatName, version = 2, width = 1, height = 1,
            palette = new[] { "ZZZ" }, layers = new[] { new { name = "l", visible = true, pixels = new[] { "1x00000000" } } }, activeLayer = 0
        }));

        Assert.Null(ProjectFile.TryLoad(path, out var error));
        Assert.Contains("非法颜色值", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Load_BadDimensions_Rejected(string width)
    {
        using var dir = new TempDir();
        var path = dir.File("bad-dim.pxproj");
        var json = JsonSerializer.Serialize(new
        {
            format = ProjectFile.FormatName, version = 2, width = int.Parse(width), height = 1,
            palette = new[] { "ABCDEF" }, layers = new[] { new { name = "l", visible = true, pixels = new[] { "1x00000000" } } }, activeLayer = 0
        });
        File.WriteAllText(path, json);

        Assert.Null(ProjectFile.TryLoad(path, out var error));
        Assert.Contains("width / height", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1xZZZZZZZZ")]
    [InlineData("2x00000000")]
    public void Load_BadRleTokens_Rejected(string row)
    {
        using var dir = new TempDir();
        var path = dir.File("bad-rle.pxproj");
        File.WriteAllText(path, JsonWithPalette(1).Replace("1x00000000", row));

        Assert.Null(ProjectFile.TryLoad(path, out var error));
        Assert.Contains("图层数据不合法", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AnimationFpsZero_Rejected()
    {
        using var dir = new TempDir();
        var source = dir.File("animation.pxproj");
        var frames = new List<AnimationFrame> { new(new List<Layer> { new("f", new PixelGrid(4, 4)) }) };
        ProjectFile.Save(source, frames[0].Layers, 0, new[] { "ABCDEF" }, frames: frames, activeFrame: 0, framesPerSecond: 12);

        var path = dir.File("bad-fps.pxproj");
        File.WriteAllText(path, File.ReadAllText(source).Replace("\"fps\":12", "\"fps\":0"));

        Assert.Null(ProjectFile.TryLoad(path, out var error));
        Assert.Contains("动画帧或帧率不合法", error, StringComparison.Ordinal);
    }

    private static List<AnimationFrame> MakeFrames(int count)
    {
        var frames = new List<AnimationFrame>();
        for (int i = 0; i < count; i++)
            frames.Add(new AnimationFrame(new List<Layer> { new($"f{i}", new PixelGrid(4, 4)) }));
        return frames;
    }

    [Fact]
    public void SaveLoad_Animation_AtMaxFrames_RoundTrips()
    {
        using var dir = new TempDir();
        var path = dir.File("max-frames.pxproj");
        var frames = MakeFrames(ProjectFile.MaxFrames);
        ProjectFile.Save(path, frames[0].Layers, 0, new[] { "ABCDEF" },
            frames: frames, activeFrame: ProjectFile.MaxFrames - 1, framesPerSecond: 12);

        var loaded = ProjectFile.TryLoad(path, out var error);
        Assert.NotNull(loaded);
        Assert.Equal(ProjectFile.MaxFrames, loaded!.Frames.Count);
        Assert.Equal("", error);
    }

    [Fact]
    public void Save_TooManyFrames_Rejected()
    {
        using var dir = new TempDir();
        var path = dir.File("too-many.pxproj");
        var frames = MakeFrames(ProjectFile.MaxFrames + 1);

        Assert.Throws<ArgumentException>(() => ProjectFile.Save(path, frames[0].Layers, 0, new[] { "ABCDEF" },
            frames: frames, activeFrame: 0, framesPerSecond: 12));
    }

    [Fact]
    public void Load_TooManyFrames_Rejected()
    {
        using var dir = new TempDir();
        var path = dir.File("bad-count.pxproj");
        var frames = Enumerable.Range(0, ProjectFile.MaxFrames + 1)
            .Select(_ => new { layers = new[] { new { name = "f", visible = true, pixels = new[] { "1x00000000" } } }, activeLayer = 0 })
            .ToArray();
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            format = ProjectFile.FormatName,
            version = 3,
            width = 1,
            height = 1,
            palette = new[] { "ABCDEF" },
            frames,
            activeFrame = 0,
            fps = 12
        }));

        Assert.Null(ProjectFile.TryLoad(path, out var error));
        Assert.Contains("动画帧", error, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveLoad_Animation_AtMaxFps_RoundTrips()
    {
        using var dir = new TempDir();
        var path = dir.File("max-fps.pxproj");
        var frames = MakeFrames(2);
        ProjectFile.Save(path, frames[0].Layers, 0, new[] { "ABCDEF" },
            frames: frames, activeFrame: 0, framesPerSecond: ProjectFile.MaxFps);

        var loaded = ProjectFile.TryLoad(path, out var error);
        Assert.NotNull(loaded);
        Assert.Equal(ProjectFile.MaxFps, loaded!.FramesPerSecond);
        Assert.Equal("", error);
    }

    [Fact]
    public void Save_FpsAboveLimit_Rejected()
    {
        using var dir = new TempDir();
        var path = dir.File("bad-fps-save.pxproj");
        var frames = MakeFrames(2);

        Assert.Throws<ArgumentException>(() => ProjectFile.Save(path, frames[0].Layers, 0, new[] { "ABCDEF" },
            frames: frames, activeFrame: 0, framesPerSecond: ProjectFile.MaxFps + 1));
    }

    [Fact]
    public void Load_FpsAboveLimit_Rejected()
    {
        using var dir = new TempDir();
        var path = dir.File("bad-fps-load.pxproj");
        var frames = Enumerable.Range(0, 2)
            .Select(_ => new { layers = new[] { new { name = "f", visible = true, pixels = new[] { "1x00000000" } } }, activeLayer = 0 })
            .ToArray();
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            format = ProjectFile.FormatName,
            version = 3,
            width = 1,
            height = 1,
            palette = new[] { "ABCDEF" },
            frames,
            activeFrame = 0,
            fps = ProjectFile.MaxFps + 1
        }));

        Assert.Null(ProjectFile.TryLoad(path, out var error));
        Assert.Contains("动画帧", error, StringComparison.Ordinal);
    }
}
