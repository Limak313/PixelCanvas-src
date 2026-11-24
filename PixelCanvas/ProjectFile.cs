using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PixelCanvas;

/// <summary>.pxproj 工程文件读写：UTF-8 JSON + 按行 RLE 像素编码，载入时逐项校验。</summary>
public static class ProjectFile
{
    public const string FormatName = "pixel-canvas-project";
    public const int CurrentVersion = 3;

    /// <summary>实现保护上限：载入时宽/高超过该值直接拒绝，防止异常文件耗尽内存。</summary>
    public const int MaxSide = 1024;
    public const long MaxFileBytes = 256L * 1024 * 1024;
    public const int MaxPaletteColors = 1024;
    public const int MaxFrames = 128;
    public const int MaxFps = 60;
    // 保留 JSON/RLE 体积余量，确保多帧作品最坏情况下仍能低于文件上限。
    public const long MaxAnimationPixels = 22_000_000;

    public sealed class LoadResult
    {
        public required PixelGrid Grid { get; init; }
        public required List<string> Palette { get; init; }
        public required List<Layer> Layers { get; init; }
        public int ActiveLayer { get; init; }
        public List<AnimationFrame> Frames { get; init; } = new();
        public int ActiveFrame { get; init; }
        public int FramesPerSecond { get; init; } = 8;
    }

    private sealed class Dto
    {
        [JsonPropertyName("format")] public string format { get; set; } = "";
        [JsonPropertyName("version")] public int version { get; set; }
        [JsonPropertyName("width")] public int width { get; set; }
        [JsonPropertyName("height")] public int height { get; set; }
        [JsonPropertyName("palette")] public string[] palette { get; set; } = [];
        [JsonPropertyName("pixels")] public string[] pixels { get; set; } = [];
        [JsonPropertyName("layers")] public LayerDto[]? layers { get; set; }
        [JsonPropertyName("activeLayer")] public int activeLayer { get; set; }
        [JsonPropertyName("frames"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public FrameDto[]? frames { get; set; }
        [JsonPropertyName("activeFrame"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? activeFrame { get; set; }
        [JsonPropertyName("fps"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? fps { get; set; }
    }

    private sealed class FrameDto
    {
        [JsonPropertyName("layers")] public LayerDto[] layers { get; set; } = [];
        [JsonPropertyName("activeLayer")] public int activeLayer { get; set; }
    }

    private sealed class LayerDto
    {
        [JsonPropertyName("name")] public string name { get; set; } = "";
        [JsonPropertyName("visible")] public bool visible { get; set; }
        [JsonPropertyName("pixels")] public string[] pixels { get; set; } = [];
    }

    // ---------- 保存 ----------

    /// <summary>
    /// 保存工程：UTF-8 JSON，pixels 为按行 RLE（token 形如 NxRRGGBBAA，逗号分隔）。
    /// 写入采用「临时文件 + 替换」方式，保证失败时原工程文件不被破坏。
    /// </summary>
    public static void Save(string path, IReadOnlyList<Layer> layers, int activeLayer, IReadOnlyList<string> palette, bool overwrite = true,
        IReadOnlyList<AnimationFrame>? frames = null, int activeFrame = 0, int framesPerSecond = 8)
    {
        if (palette.Count is < 1 or > MaxPaletteColors) throw new ArgumentException($"调色板颜色数量必须为 1–{MaxPaletteColors}。");
        var grid = layers[activeLayer].Grid;
        if (frames is not null && (frames.Count is < 1 or > MaxFrames || activeFrame < 0 || activeFrame >= frames.Count || framesPerSecond is < 1 or > MaxFps ||
            (frames.Count > 1 && frames.Sum(f => (long)f.Layers.Count * grid.Width * grid.Height) > MaxAnimationPixels) ||
            frames.Any(f => f.Layers.Count is < 1 or > 32 || f.ActiveLayer < 0 || f.ActiveLayer >= f.Layers.Count || f.Layers.Any(l => l.Grid.Width != grid.Width || l.Grid.Height != grid.Height))))
            throw new ArgumentException("动画帧、图层、尺寸或帧率超出支持范围。");
        bool animation = frames is not null && (frames.Count > 1 || framesPerSecond != 8);
        LayerDto[] EncodeLayers(IReadOnlyList<Layer> source) => source.Select(l => new LayerDto { name = l.Name, visible = l.Visible, pixels = EncodeRows(l.Grid) }).ToArray();
        var dto = new Dto
        {
            format = FormatName, version = animation ? CurrentVersion : 2, width = grid.Width, height = grid.Height,
            palette = palette.Select(p => p.ToUpperInvariant()).ToArray(),
            layers = animation ? null : EncodeLayers(layers),
            activeLayer = activeLayer,
            frames = animation ? frames!.Select(f => new FrameDto { layers = EncodeLayers(f.Layers), activeLayer = f.ActiveLayer }).ToArray() : null,
            activeFrame = animation ? activeFrame : null,
            fps = animation ? framesPerSecond : null,
        };
        string json = JsonSerializer.Serialize(dto);
        if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes) throw new IOException($"工程文件超过 {MaxFileBytes / 1024 / 1024} MiB 上限，无法保存。");
        AtomicFile.WriteText(path, json, overwrite);
    }

    private static string[] EncodeRows(PixelGrid grid)
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

        return pixels;
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
        JsonDocument doc;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxFileBytes) { error = $"工程文件超过 {MaxFileBytes / 1024 / 1024} MiB 上限。"; return null; }
            doc = JsonDocument.Parse(stream);
        }
        catch (JsonException ex) { error = "不是有效的 JSON：" + ex.Message; return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { error = "无法读取文件：" + ex.Message; return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            { error = "文件结构不正确：根元素必须是 JSON 对象。"; return null; }

            if (!root.TryGetProperty("format", out var fmt) || fmt.ValueKind != JsonValueKind.String || fmt.GetString() != FormatName)
            { error = $"format 字段必须是 \"{FormatName}\"。"; return null; }

            if (!root.TryGetProperty("version", out var ver) || ver.ValueKind != JsonValueKind.Number
                || !ver.TryGetInt32(out int version) || (version != 1 && version != 2 && version != CurrentVersion))
            { error = "工程版本不受支持。"; return null; }

            if (!TryGetDim(root, "width", out int width) || !TryGetDim(root, "height", out int height))
            { error = "width / height 必须是正整数。"; return null; }
            if (width > MaxSide || height > MaxSide)
            { error = $"画布尺寸 {width}×{height} 超出支持上限 {MaxSide}×{MaxSide}。"; return null; }

            if (!root.TryGetProperty("palette", out var pal) || pal.ValueKind != JsonValueKind.Array || pal.GetArrayLength() is < 1 or > MaxPaletteColors)
            { error = $"palette 颜色数量必须为 1–{MaxPaletteColors}。"; return null; }
            var palette = new List<string>(pal.GetArrayLength());
            foreach (var item in pal.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !IsRgbHex(item.GetString()))
                { error = "palette 中存在非法颜色值（应为 6 位十六进制 RGB）。"; return null; }
                palette.Add(item.GetString()!.ToUpperInvariant());
            }

            var layers = new List<Layer>();
            var frames = new List<AnimationFrame>();
            int active = 0;
            int selectedFrame = 0, fps = 8;
            if (version == 1)
            {
                if (!root.TryGetProperty("pixels", out var rows) || !TryParseRows(rows, width, height, out var grid))
                { error = "像素数据不合法。"; return null; }
                layers.Add(new Layer("图层 1", grid!));
            }
            else if (version == 2)
            {
                if (!root.TryGetProperty("layers", out var entries) || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() is < 1 or > 32)
                { error = "图层数量必须为 1–32。"; return null; }
                foreach (var entry in entries.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(name.GetString()) || name.GetString()!.Length > 100
                        || !entry.TryGetProperty("visible", out var visible) || visible.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                        || !entry.TryGetProperty("pixels", out var rows) || !TryParseRows(rows, width, height, out var grid))
                    { error = "图层数据不合法。"; return null; }
                    layers.Add(new Layer(name.GetString()!, grid!, visible.GetBoolean()));
                }
                if (!root.TryGetProperty("activeLayer", out var selected) || selected.ValueKind != JsonValueKind.Number || !selected.TryGetInt32(out active) || active < 0 || active >= layers.Count)
                { error = "当前图层索引不合法。"; return null; }
            }
            else
            {
                if (!root.TryGetProperty("frames", out var frameEntries) || frameEntries.ValueKind != JsonValueKind.Array || frameEntries.GetArrayLength() is < 1 or > MaxFrames ||
                    !root.TryGetProperty("activeFrame", out var frameIndex) || frameIndex.ValueKind != JsonValueKind.Number || !frameIndex.TryGetInt32(out selectedFrame) || selectedFrame < 0 || selectedFrame >= frameEntries.GetArrayLength() ||
                    !root.TryGetProperty("fps", out var rate) || rate.ValueKind != JsonValueKind.Number || !rate.TryGetInt32(out fps) || fps is < 1 or > MaxFps)
                { error = "动画帧或帧率不合法。"; return null; }
                long pixelBudget = 0;
                foreach (var frame in frameEntries.EnumerateArray())
                {
                    if (frame.ValueKind != JsonValueKind.Object || !frame.TryGetProperty("layers", out var entries) || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() is < 1 or > 32 ||
                        !frame.TryGetProperty("activeLayer", out var selected) || selected.ValueKind != JsonValueKind.Number || !selected.TryGetInt32(out int frameActive) || frameActive < 0 || frameActive >= entries.GetArrayLength())
                    { error = "动画帧图层数据不合法。"; return null; }
                    pixelBudget += (long)entries.GetArrayLength() * width * height;
                    if (frameEntries.GetArrayLength() > 1 && pixelBudget > MaxAnimationPixels) { error = "动画帧总像素超过上限。"; return null; }
                    var frameLayers = new List<Layer>();
                    foreach (var entry in entries.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()) || name.GetString()!.Length > 100 ||
                            !entry.TryGetProperty("visible", out var visible) || visible.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                            !entry.TryGetProperty("pixels", out var rows) || !TryParseRows(rows, width, height, out var grid))
                        { error = "动画帧图层数据不合法。"; return null; }
                        frameLayers.Add(new Layer(name.GetString()!, grid!, visible.GetBoolean()));
                    }
                    frames.Add(new AnimationFrame(frameLayers, frameActive));
                }
                layers = frames[selectedFrame].Layers;
                active = frames[selectedFrame].ActiveLayer;
            }
            if (frames.Count == 0) frames.Add(new AnimationFrame(layers, active));
            return new LoadResult { Grid = layers[active].Grid, Palette = palette, Layers = layers, ActiveLayer = active, Frames = frames, ActiveFrame = selectedFrame, FramesPerSecond = fps };
        }
    }

    private static bool TryParseRows(JsonElement rows, int width, int height, out PixelGrid? grid)
    {
        grid = null;
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() != height) return false;
        var result = new PixelGrid(width, height);
        int y = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.String || !ParseRow(row.GetString()!, width, result, y++)) return false;
        }
        grid = result;
        return true;
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
