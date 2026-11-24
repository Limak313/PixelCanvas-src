using PixelCanvas;
using Xunit;

namespace PixelCanvas.Tests;

public class AtomicFileTests
{
    [Fact]
    public void WriteText_CreatesTargetAndLeavesNoTempFile()
    {
        using var dir = new TempDir();
        var target = dir.File("note.txt");
        AtomicFile.WriteText(target, "内容");
        Assert.Equal("内容", File.ReadAllText(target));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    [Fact]
    public void Write_WriterThrows_KeepsOriginalAndCleansTemp()
    {
        using var dir = new TempDir();
        var target = dir.File("note.txt");
        File.WriteAllText(target, "旧内容");
        Assert.Throws<IOException>(() => AtomicFile.Write(target, stream =>
        {
            stream.WriteByte(0x41);
            throw new IOException("模拟写入中途失败");
        }));
        Assert.Equal("旧内容", File.ReadAllText(target));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    [Fact]
    public void Write_OverwriteFalse_LeavesExistingTargetUntouched()
    {
        using var dir = new TempDir();
        var target = dir.File("note.txt");
        File.WriteAllText(target, "旧内容");
        Assert.Throws<IOException>(() => AtomicFile.WriteText(target, "新内容", overwrite: false));
        Assert.Equal("旧内容", File.ReadAllText(target));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    [Fact]
    public void Write_OverwriteTrue_ReplacesContent()
    {
        using var dir = new TempDir();
        var target = dir.File("note.txt");
        File.WriteAllText(target, "旧内容");
        AtomicFile.WriteText(target, "新内容");
        Assert.Equal("新内容", File.ReadAllText(target));
    }

    /// <summary>导出到已存在路径必须失败且不删除用户原文件（旧实现会先建失败再删目标）。</summary>
    [Fact]
    public void GifExport_ExistingPath_KeepsUserFile()
    {
        using var dir = new TempDir();
        var path = dir.File("existing.gif");
        byte[] original = { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 };
        File.WriteAllBytes(path, original);
        var grid = new PixelGrid(4, 4);
        grid.Set(1, 1, Argb.FromRgb(0x336699));
        var frames = new List<AnimationFrame> { new(new List<Layer> { new("only", grid) }) };
        Assert.Throws<IOException>(() => GifExport.Save(path, frames, 8, 1));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void PaletteSave_ExistingPath_KeepsUserFile()
    {
        using var dir = new TempDir();
        var path = dir.File("palette.pxpal");
        File.WriteAllText(path, "{\"format\":\"pixelcanvas-palette\"}");
        Assert.Throws<IOException>(() => PaletteFile.Save(path, new[] { "112233" }));
        Assert.Equal("{\"format\":\"pixelcanvas-palette\"}", File.ReadAllText(path));
    }

    [Fact]
    public void ProjectSave_ExistingFileSurvivesFailedReplace()
    {
        using var dir = new TempDir();
        var path = dir.File("project.pxproj");
        var grid = new PixelGrid(2, 2);
        grid.Set(0, 0, Argb.FromRgb(0xAABBCC));
        var layers = new List<Layer> { new("base", grid) };
        ProjectFile.Save(path, layers, 0, new[] { "AABBCC" });
        var before = File.ReadAllBytes(path);
        // 目录不可写模拟：把目标目录换成同名文件后再次保存必然失败，原工程不应被截断
        Assert.ThrowsAny<IOException>(() => ProjectFile.Save(Path.Combine(dir.Path, "no-such-dir", "p.pxproj"), layers, 0, new[] { "AABBCC" }, overwrite: false));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }
}
