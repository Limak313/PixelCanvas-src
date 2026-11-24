using System.Text.Json;
using PixelCanvas;
using Xunit;

namespace PixelCanvas.Tests;

public class PaletteTests
{
    [Fact]
    public void SaveLoad_RoundTripsColors()
    {
        using var dir = new TempDir();
        var path = dir.File("palette.pxpal");
        PaletteFile.Save(path, new[] { "123456", "ABCDEF" });

        var loaded = PaletteFile.Load(path);
        Assert.Equal(new[] { "123456", "ABCDEF" }, loaded);
    }

    [Fact]
    public void Load_BrokenPalette_Rejected()
    {
        using var dir = new TempDir();
        var path = dir.File("broken.pxpal");
        File.WriteAllText(path, "{\"format\":\"pixelcanvas-palette\",\"version\":1,\"colors\":[\"bad\"]}");

        Assert.Throws<ArgumentException>(() => PaletteFile.Load(path));
    }

    [Fact]
    public void Load_WrongFormat_Rejected()
    {
        using var dir = new TempDir();
        var path = dir.File("wrong.pxpal");
        File.WriteAllText(path, JsonSerializer.Serialize(new { format = "other", version = 1, colors = new[] { "#123456" } }));

        Assert.Throws<ArgumentException>(() => PaletteFile.Load(path));
    }

    [Fact]
    public void Validate_AcceptsHashPrefixAndNormalizes()
    {
        var colors = PaletteFile.Validate(JsonSerializer.SerializeToNode(new[] { "#123456", "abcdef" }));
        Assert.Equal(new[] { "123456", "ABCDEF" }, colors);
    }
}
