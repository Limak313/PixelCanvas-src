using System.Text.Json;
using System.Text.Json.Nodes;

namespace PixelCanvas;

internal static class PaletteFile
{
    internal static List<string> Validate(JsonNode? node)
    {
        if (node is not JsonArray array || array.Count is < 1 or > ProjectFile.MaxPaletteColors)
            throw new ArgumentException($"调色板必须包含 1–{ProjectFile.MaxPaletteColors} 种颜色。");
        var colors = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var hex) || hex is null) throw new ArgumentException("调色板颜色必须为 #RRGGBB。");
            if (hex.StartsWith('#')) hex = hex[1..];
            if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out _)) throw new ArgumentException("调色板颜色必须为 #RRGGBB。");
            colors.Add(hex.ToUpperInvariant());
        }
        return colors;
    }

    internal static List<string> Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 131072) throw new ArgumentException("调色板文件超过 128 KiB。");
        var json = JsonNode.Parse(stream) as JsonObject ?? throw new ArgumentException("调色板文件格式不正确。");
        if (json["format"]?.GetValue<string>() != "pixelcanvas-palette" || json["version"]?.GetValue<int>() != 1) throw new ArgumentException("调色板格式或版本不受支持。");
        return Validate(json["colors"]);
    }

    internal static void Save(string path, IReadOnlyList<string> colors, bool overwrite = false) =>
        AtomicFile.Write(path, stream => JsonSerializer.Serialize(stream, new { format = "pixelcanvas-palette", version = 1, colors }), overwrite);
}
