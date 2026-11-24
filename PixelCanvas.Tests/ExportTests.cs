using System.Drawing.Imaging;
using System.Text.Json;
using PixelCanvas;
using Xunit;

namespace PixelCanvas.Tests;

public class ExportTests
{
    private static List<AnimationFrame> SampleFrames()
    {
        var first = new PixelGrid(4, 4);
        first.Set(1, 2, Argb.FromRgb(0x123456));
        var second = new PixelGrid(4, 4);
        second.Set(3, 1, Argb.FromRgb(0xFEDCBA));
        return new List<AnimationFrame>
        {
            new(new List<Layer> { new("first", first) }),
            new(new List<Layer> { new("second", second) })
        };
    }

    private static string PrepareAnimationProject(TempDir dir)
    {
        var path = dir.File("animation.pxproj");
        ProjectFile.Save(path, SampleFrames()[1].Layers, 0, new[] { "ABCDEF" },
            frames: SampleFrames(), activeFrame: 1, framesPerSecond: 12);
        return path;
    }

    [Fact]
    public async Task Project_Exports_ClassifyByPurpose()
    {
        using var dir = new TempDir();
        var previous = Environment.GetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR");
        Environment.SetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR", dir.Path);
        try
        {
            var project = PrepareAnimationProject(dir);
            var png = await McpTestHost.ProjectActionAsync("project_export_png", new { path = project });
            var sheet = await McpTestHost.ProjectActionAsync("project_export_sprite_sheet", new { path = project });
            var gif = await McpTestHost.ProjectActionAsync("project_export_gif", new { path = project });
            var saved = await McpTestHost.ProjectActionAsync("project_add_layer", new { path = project });

            Assert.Equal(Path.Combine(dir.Path, "PNG"), Path.GetDirectoryName(png["path"]!.GetValue<string>()));
            Assert.Equal(Path.Combine(dir.Path, "动画"), Path.GetDirectoryName(sheet["path"]!.GetValue<string>()));
            Assert.Equal(Path.Combine(dir.Path, "动画"), Path.GetDirectoryName(gif["path"]!.GetValue<string>()));
            Assert.Equal(Path.Combine(dir.Path, "工程"), Path.GetDirectoryName(saved["path"]!.GetValue<string>()));
        }
        finally { Environment.SetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR", previous); }
    }

    [Fact]
    public async Task SpriteSheet_ExplicitPath_Preferred()
    {
        using var dir = new TempDir();
        var previous = Environment.GetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR");
        Environment.SetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR", dir.Path);
        try
        {
            var project = PrepareAnimationProject(dir);
            var explicitPath = Path.Combine(dir.Path, "explicit-sheet.png");
            var result = await McpTestHost.ProjectActionAsync("project_export_sprite_sheet", new { path = project, output_path = explicitPath });
            Assert.Equal(explicitPath, result["path"]!.GetValue<string>());
            Assert.True(File.Exists(explicitPath));
        }
        finally { Environment.SetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR", previous); }
    }

    [Fact]
    public async Task Export_CreatesNestedParentDirectory()
    {
        using var dir = new TempDir();
        var previous = Environment.GetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR");
        Environment.SetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR", dir.Path);
        try
        {
            var project = PrepareAnimationProject(dir);
            var nested = Path.Combine(dir.Path, "输出目录", "多层", "图片.png");
            var result = await McpTestHost.ProjectActionAsync("project_export_png", new { path = project, output_path = nested });
            Assert.Equal(nested, result["path"]!.GetValue<string>());
            Assert.True(File.Exists(nested));
        }
        finally { Environment.SetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR", previous); }
    }

    [Fact]
    public async Task Export_DoesNotOverwriteExisting()
    {
        using var dir = new TempDir();
        var previous = Environment.GetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR");
        Environment.SetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR", dir.Path);
        try
        {
            var project = PrepareAnimationProject(dir);
            var nested = Path.Combine(dir.Path, "多层", "图片.png");
            var first = await McpTestHost.ProjectActionAsync("project_export_png", new { path = project, output_path = nested });
            var bytes = File.ReadAllBytes(nested);

            var duplicate = await McpTestHost.InvokeAsync("pixelcanvas_project",
                new { action = "project_export_png", arguments = new { path = project, output_path = nested } });
            Assert.True(McpTestHost.IsError(duplicate));
            Assert.True(bytes.SequenceEqual(File.ReadAllBytes(nested)));
        }
        finally { Environment.SetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR", previous); }
    }

    [Fact]
    public async Task Project_Create_CreatesNestedParent()
    {
        using var dir = new TempDir();
        var previous = Environment.GetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR");
        Environment.SetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR", dir.Path);
        try
        {
            var nested = Path.Combine(dir.Path, "显式工程", "子目录", "工程.pxproj");
            var result = await McpTestHost.ProjectActionAsync("project_create", new { path = nested, width = 4, height = 4 });
            Assert.Equal(nested, result["path"]!.GetValue<string>());
            Assert.NotNull(ProjectFile.TryLoad(nested, out _));
        }
        finally { Environment.SetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR", previous); }
    }

    [Fact]
    public async Task Export_BlockedByFileParent_KeepsFile()
    {
        using var dir = new TempDir();
        var previous = Environment.GetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR");
        Environment.SetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR", dir.Path);
        try
        {
            var project = PrepareAnimationProject(dir);
            var blockedParent = Path.Combine(dir.Path, "not-a-directory");
            File.WriteAllText(blockedParent, "保留");

            var blocked = await McpTestHost.InvokeAsync("pixelcanvas_project",
                new { action = "project_export_png", arguments = new { path = project, output_path = Path.Combine(blockedParent, "image.png") } });
            Assert.True(McpTestHost.IsError(blocked));
            Assert.Equal("保留", File.ReadAllText(blockedParent));
        }
        finally { Environment.SetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR", previous); }
    }

    [Fact]
    public void Preset_NewOutputPath_ClassifiesAndRespectsDirectory()
    {
        using var dir = new TempDir();
        var previous = Environment.GetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR");
        Environment.SetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR", dir.Path);
        try
        {
            string PathFor(string format, string directory) =>
                ExportPresets.NewOutputPath(new ExportPreset("test", format, 1, 8, 1, directory));

            Assert.Equal(Path.Combine(dir.Path, "动画"), Path.GetDirectoryName(PathFor("sheet", "")));
            Assert.Equal(Path.Combine(dir.Path, "PNG"), Path.GetDirectoryName(PathFor("png", "")));
            Assert.Equal(dir.Path, Path.GetDirectoryName(PathFor("sheet", dir.Path)));
        }
        finally { Environment.SetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR", previous); }
    }

    [Fact]
    public void Gif_ContainsAllFrames_ColorsAndTransparency()
    {
        using var dir = new TempDir();
        var gifPath = dir.File("animation.gif");
        var frames = SampleFrames();
        GifExport.Save(gifPath, frames, 12, 1);

        using var gif = System.Drawing.Image.FromFile(gifPath);
        var dimension = new FrameDimension(gif.FrameDimensionsList[0]);
        Assert.Equal(2, gif.GetFrameCount(dimension));

        gif.SelectActiveFrame(dimension, 0);
        using (var frame = new System.Drawing.Bitmap(gif))
        {
            Assert.Equal(Argb.FromRgb(0x123456), frame.GetPixel(1, 2).ToArgb());
            Assert.Equal(0, frame.GetPixel(0, 0).A);
        }
        gif.SelectActiveFrame(dimension, 1);
        using (var frame = new System.Drawing.Bitmap(gif))
        {
            Assert.Equal(Argb.FromRgb(0xFEDCBA), frame.GetPixel(3, 1).ToArgb());
            Assert.Equal(0, frame.GetPixel(1, 2).A);
        }

        var delays = gif.GetPropertyItem(0x5100)!.Value!;
        Assert.Equal(8, BitConverter.ToInt32(delays, 0));
        Assert.Equal(9, BitConverter.ToInt32(delays, 4));
    }

    [Fact]
    public void Gif_FixedPalette_WritesIndexedTableAndMapsNearest()
    {
        using var dir = new TempDir();
        var grid = new PixelGrid(3, 1);
        grid.Set(0, 0, Argb.FromRgb(0x112233));
        grid.Set(1, 0, Argb.FromRgb(0x00FF00));
        grid.Set(2, 0, Argb.FromRgb(0x999999));
        var frames = new List<AnimationFrame> { new(new List<Layer> { new("art", grid) }) };
        var path = dir.File("indexed.gif");

        GifExport.Save(path, frames, 8, 1, palette: new[] { unchecked((int)0xFF112233), unchecked((int)0xFF00FF00) });

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(0x11, bytes[16]);
        Assert.Equal(0x22, bytes[17]);
        Assert.Equal(0x33, bytes[18]);
        Assert.Equal(0x00, bytes[19]);
        Assert.Equal(0xFF, bytes[20]);
        Assert.Equal(0x00, bytes[21]);

        using var gif = new Bitmap(path);
        Assert.Equal(0x112233, gif.GetPixel(0, 0).ToArgb() & 0xFFFFFF);
        Assert.Equal(0x00FF00, gif.GetPixel(1, 0).ToArgb() & 0xFFFFFF);
        Assert.Equal(0x112233, gif.GetPixel(2, 0).ToArgb() & 0xFFFFFF);
    }

    [Fact]
    public void Gif_FixedPalette_RejectsInvalidColors()
    {
        using var dir = new TempDir();
        var grid = new PixelGrid(1, 1);
        grid.Set(0, 0, Argb.FromRgb(0x123456));
        var frames = new List<AnimationFrame> { new(new List<Layer> { new("art", grid) }) };

        Assert.Throws<ArgumentException>(() => GifExport.Save(dir.File("empty.gif"), frames, 8, 1, palette: Array.Empty<int>()));
        Assert.Throws<ArgumentException>(() => GifExport.Save(dir.File("alpha.gif"), frames, 8, 1, palette: new[] { 0x00123456 }));
        var tooMany = Enumerable.Range(0, 256).Select(i => unchecked((int)(0xFF000000u | ((uint)i << 8) | (uint)i))).ToArray();
        Assert.Throws<ArgumentException>(() => GifExport.Save(dir.File("many.gif"), frames, 8, 1, palette: tooMany));
        Assert.False(File.Exists(dir.File("empty.gif")));
        Assert.False(File.Exists(dir.File("alpha.gif")));
        Assert.False(File.Exists(dir.File("many.gif")));
    }

    [Fact]
    public async Task ProjectExportGif_UsesProjectPalette()
    {
        using var dir = new TempDir();
        var previous = Environment.GetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR");
        Environment.SetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR", dir.Path);
        try
        {
            var first = new PixelGrid(2, 1);
            first.Set(0, 0, Argb.FromRgb(0x112233));
            first.Set(1, 0, Argb.FromRgb(0x00FF00));
            var project = dir.File("indexed.pxproj");
            ProjectFile.Save(project, new[] { new Layer("art", first) }, 0, new[] { "112233", "00FF00" },
                frames: new List<AnimationFrame> { new(new List<Layer> { new("art", first) }) }, framesPerSecond: 8);

            var result = await McpTestHost.ProjectActionAsync("project_export_gif", new { path = project, use_project_palette = true });

            Assert.Equal("custom", result["palette"]!.GetValue<string>());
            Assert.Equal(2, result["paletteColors"]!.GetValue<int>());
            var bytes = File.ReadAllBytes(result["path"]!.GetValue<string>());
            Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x00, 0xFF, 0x00 }, bytes[16..22]);
        }
        finally { Environment.SetEnvironmentVariable("PIXELCANVAS_OUTPUT_DIR", previous); }
    }

    [Fact]
    public void Gif_QuantizesOver255Colors()
    {
        using var dir = new TempDir();
        var many = new PixelGrid(16, 16);
        for (var i = 0; i < 256; i++)
            many.Set(i % 16, i / 16, Argb.FromRgb((i << 16) | ((255 - i) << 8) | i));
        var path = dir.File("quantized.gif");
        GifExport.Save(path, new List<AnimationFrame> { new(new List<Layer> { new("many", many) }) }, 8, 1);

        using var gif = new System.Drawing.Bitmap(path);
        Assert.Equal(16, gif.Width);
        Assert.Equal(16, gif.Height);
        Assert.True(gif.GetPixel(0, 0).G > 240);
        Assert.True(gif.GetPixel(15, 15).R > 240);
    }

    [Fact]
    public void SpriteSheet_OrderScaleHiddenLayerTransparentSpace()
    {
        using var dir = new TempDir();
        var third = new PixelGrid(4, 4);
        third.Set(0, 0, Argb.FromRgb(0xAA2200));
        var hidden = new PixelGrid(4, 4);
        hidden.Set(0, 0, Argb.FromRgb(0xFFFFFF));
        var frames = new List<AnimationFrame>(SampleFrames())
        {
            new(new List<Layer> { new("visible", third), new("hidden", hidden, false) })
        };
        var path = dir.File("sheet.png");
        SpriteSheetExport.Save(path, frames, 2, 2);

        using var sheet = new System.Drawing.Bitmap(path);
        Assert.Equal(16, sheet.Width);
        Assert.Equal(16, sheet.Height);
        Assert.Equal(Argb.FromRgb(0x123456), sheet.GetPixel(2, 4).ToArgb());
        Assert.Equal(Argb.FromRgb(0xFEDCBA), sheet.GetPixel(14, 2).ToArgb());
        Assert.Equal(Argb.FromRgb(0xAA2200), sheet.GetPixel(0, 8).ToArgb());
        Assert.Equal(0, sheet.GetPixel(15, 15).A);
    }

    [Fact]
    public void SpriteSheet_Oversize_RejectedAndNoFile()
    {
        using var dir = new TempDir();
        var large = new List<AnimationFrame> { new(new List<Layer> { new("large", new PixelGrid(512, 512)) }) };
        var path = dir.File("large-sheet.png");

        Assert.Throws<ArgumentException>(() => SpriteSheetExport.Save(path, large, 64, 16));
        Assert.False(File.Exists(path));
    }
}
