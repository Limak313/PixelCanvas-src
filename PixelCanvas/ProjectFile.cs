using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PixelCanvas;

/// <summary>.pxproj 工程文件读写：UTF-8 JSON + 按行 RLE 像素编码，载入时逐项校验。</summary>
public static class ProjectFile
{
    public const string FormatName = "pixel-canvas-project";
    public const int CurrentVersion = 1;

    /// <summary>实现保护上限：载入时宽/高超过该值直接拒绝，防止异常文件耗尽内存。</summary>
    public const int MaxSide = 1024;

    public sealed class LoadResult
    {
        public required PixelGrid Grid { get; init; }
        public required List<string> Palette { get; init; }
    }

    private sealed class Dto
    {
        [JsonPropertyName("format")] public string format { get; set; } = "";
        [JsonPropertyName("version")] public int version { get; set; }
        [JsonPropertyName("width")] public int width { get; set; }
        [JsonPropertyName("height")] public int height { get; set; }
        [JsonPropertyName("palette")] public string[] palette { get; set; } = [];
        [JsonPropertyName("pixels")] public string[] pixels { get; set; } = [];
    }

    // ---------- 保存 ----------

    /// <summary>
    /// 保存工程：UTF-8 JSON，pixels 为按行 RLE（token 形如 NxRRGGBBAA，逗号分隔）。
    /// 写入采用「临时文件 + 替换」方式，保证失败时原工程文件不被破坏。
    /// </summary>
    public static void Save(string path, PixelGrid grid, IReadOnlyList<string> palette)
    {
        var pixels = new string[grid.Height];
        for (int y = 0; y < grid.Height; y++)
        {
            var sb = new StringBuilder();
            int x = 0;
            while (x < grid.Width)
            {
                int color = grid.Get(x, y);
                int run = 1;
                while (x + run < grid.Width && grid.Get(x + run, y) == color) run++;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(run).Append('x').Append(ToRgbaHex(color));
                x += run;
            }
            pixels[y] = sb.ToString();
        }

        var dto = new Dto
        {
            format = FormatName,
            version = CurrentVersion,
            width = grid.Width,
            height = grid.Height,
            palette = palette.Select(p => p.ToUpperInvariant()).ToArray(),
            pixels = pixels,
        };

        // 原子写入：先写同目录临时文件再替换，避免保存中途失败把原工程截断成半截 JSON。
        string json = JsonSerializer.Serialize(dto);
        string tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 清理失败不掩盖原异常 */ }
            throw;
        }
    }

    /// <summary>规范格式的 8 位十六进制：RRGGBBAA 顺序；透明像素统一编码为 00000000。</summary>
    private static string ToRgbaHex(int argb)
    {
        if (argb == 0) return "00000000";
        return Argb.ToRgbHex(argb) + "FF";   // 本程序非透明像素 alpha 恒为 FF
    }

    // ---------- 载入 ----------

    /// <summary>
    /// 尝试载入工程；失败返回 null 并通过 error 给出中文原因（调用方保持当前画布不变）。
    /// 校验：format、version、width/height、palette、pixels（行数、每行 N 之和、十六进制合法性）。
    /// </summary>
    public static LoadResult? TryLoad(string path, out string error)
    {
        error = "";
        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception ex) { error = "无法读取文件：" + ex.Message; return null; }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { error = "不是有效的 JSON：" + ex.Message; return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            { error = "文件结构不正确：根元素必须是 JSON 对象。"; return null; }

            if (!root.TryGetProperty("format", out var fmt) || fmt.ValueKind != JsonValueKind.String || fmt.GetString() != FormatName)
            { error = $"format 字段必须是 \"{FormatName}\"。"; return null; }

            if (!root.TryGetProperty("version", out var ver) || ver.ValueKind != JsonValueKind.Number
                || !ver.TryGetInt32(out int version) || version != CurrentVersion)
            { error = "version 必须为 1，该文件不受支持。"; return null; }

            if (!TryGetDim(root, "width", out int width) || !TryGetDim(root, "height", out int height))
            { error = "width / height 必须是正整数。"; return null; }
            if (width > MaxSide || height > MaxSide)
            { error = $"画布尺寸 {width}×{height} 超出支持上限 {MaxSide}×{MaxSide}。"; return null; }

            if (!root.TryGetProperty("palette", out var pal) || pal.ValueKind != JsonValueKind.Array || pal.GetArrayLength() < 1)
            { error = "palette 必须是非空的颜色数组。"; return null; }
            var palette = new List<string>(pal.GetArrayLength());
            foreach (var item in pal.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !IsRgbHex(item.GetString()))
                { error = "palette 中存在非法颜色值（应为 6 位十六进制 RGB）。"; return null; }
                palette.Add(item.GetString()!.ToUpperInvariant());
            }

            if (!root.TryGetProperty("pixels", out var rows) || rows.ValueKind != JsonValueKind.Array
                || rows.GetArrayLength() != height)
            { error = $"pixels 必须是长度等于 height（{height}）的字符串数组。"; return null; }

            var grid = new PixelGrid(width, height);
            int y = 0;
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.String || !ParseRow(row.GetString()!, width, grid, y))
                {
                    error = $"第 {y + 1} 行像素数据不合法（RLE 编码错误，或每行像素总数不等于 {width}）。";
                    return null;
                }
                y++;
            }

            return new LoadResult { Grid = grid, Palette = palette };
        }
    }

    /// <summary>解析一行 RLE：形如 "3x00000000,2xFF0000FF"，所有 N 之和必须等于 width。</summary>
    private static bool ParseRow(string line, int width, PixelGrid grid, int y)
    {
        int written = 0;
        foreach (var token in line.Split(','))
        {
            int sep = token.IndexOf('x');
            if (sep <= 0) return false;                          // 缺少小写 'x' 或 N 为空
            if (!int.TryParse(token[..sep], out int n) || n < 1) return false;
            string colorHex = token[(sep + 1)..];
            if (colorHex.Length != 8 || !IsHex(colorHex)) return false;

            int argb;
            string alphaHex = colorHex[6..];
            if (alphaHex == "00") argb = Argb.Transparent;              // 规范：AA=00 一律视为透明
            else argb = Argb.FromRgbHex(colorHex[..6]);                 // 其余按不透明 RGB 处理

            for (int i = 0; i < n; i++)
            {
                if (written >= width) return false;                 // 超出 width
                grid.Set(written, y, argb);
                written++;
            }
        }
        return written == width;                                    // 不足 width 也判为非法
    }

    private static bool TryGetDim(JsonElement root, string name, out int value)
    {
        value = 0;
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number) return false;
        if (!el.TryGetInt32(out value)) return false;
        return value >= 1;
    }

    private static bool IsRgbHex(string? s) => s is { Length: 6 } && IsHex(s);

    private static bool IsHex(string s)
    {
        foreach (char c in s)
        {
            if (!(c is >= '0' and <= '9' || c is >= 'a' and <= 'f' || c is >= 'A' and <= 'F')) return false;
        }
        return true;
    }
}
