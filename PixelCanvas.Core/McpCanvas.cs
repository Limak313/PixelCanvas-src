using System.Text.Json.Nodes;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PixelCanvas;

/// <summary>本地 MCP 服务与桌面程序共用的画布操作。</summary>
internal static class McpCanvas
{
    private const int MaxPixels = 4096;

    internal static int[] Batch(JsonObject args, IReadOnlyList<Layer> layers, int active)
    {
        if (args["operations"] is not JsonArray operations || operations.Count is < 1 or > 64)
            throw new ArgumentException("operations 必须包含 1–64 个操作。");
        var copies = new List<Layer>(layers.Count);
        foreach (var layer in layers)
        {
            var grid = new PixelGrid(layer.Grid.Width, layer.Grid.Height);
            grid.Restore(layer.Grid.Snapshot());
            copies.Add(new Layer(layer.Name, grid, layer.Visible));
        }
        int pixelCount = 0;
        for (int i = 0; i < operations.Count; i++)
        {
            try
            {
                var operation = operations[i] as JsonObject ?? throw new ArgumentException("操作必须是对象。");
                string type = Text(operation, "type");
                if (type == "paint" && operation["pixels"] is JsonArray pixels)
                {
                    pixelCount += pixels.Count;
                    if (pixelCount > 16384) throw new ArgumentException("单次批量操作最多包含 16384 个像素。");
                }
                _ = type switch
                {
                    "paint" => Paint(operation, copies, active),
                    "shape" => Shape(operation, copies, active),
                    "fill" => Fill(operation, copies, active, false),
                    "gradient" => Fill(operation, copies, active, true),
                    _ => throw new ArgumentException("type 必须为 paint、shape、fill 或 gradient。")
                };
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            { throw new ArgumentException($"第 {i + 1} 个操作失败：{ex.Message}", ex); }
        }
        var changed = new List<int>();
        for (int i = 0; i < layers.Count; i++)
        {
            if (layers[i].Grid.RawPixels.AsSpan().SequenceEqual(copies[i].Grid.RawPixels)) continue;
            layers[i].History.Commit(layers[i].Grid.Snapshot());
            layers[i].Grid.Restore(copies[i].Grid.Snapshot());
            changed.Add(i);
        }
        return changed.ToArray();
    }

    internal static int Number(JsonObject o, string name, int fallback = -1)
    {
        if (o[name] is null) return fallback;
        if (o[name] is JsonValue v && v.TryGetValue<int>(out int result)) return result;
        throw new ArgumentException($"{name} 必须是整数。");
    }

    internal static string Text(JsonObject o, string name)
    {
        if (o[name] is JsonValue v && v.TryGetValue<string>(out var result) && !string.IsNullOrWhiteSpace(result)) return result;
        throw new ArgumentException($"{name} 必须是非空字符串。");
    }

    internal static bool Flag(JsonObject args, string name)
    {
        if (args[name] is null) return false;
        if (args[name] is JsonValue value && value.TryGetValue<bool>(out bool result)) return result;
        throw new ArgumentException($"{name} 必须是布尔值。");
    }

    internal static object ReadComposite(JsonObject args, IReadOnlyList<Layer> layers)
    {
        int x = Number(args, "x"), y = Number(args, "y");
        if (!layers[0].Grid.InBounds(x, y)) throw new ArgumentException("坐标超出画布。");
        int color = Argb.Transparent;
        foreach (var layer in layers) if (layer.Visible)
        {
            int pixel = layer.Grid.Get(x, y);
            if (pixel != Argb.Transparent) color = pixel;
        }
        return new { x, y, color = color == Argb.Transparent ? "transparent" : "#" + Argb.ToRgbHex(color) };
    }

    internal static int LayerIndex(JsonObject args, IReadOnlyList<Layer> layers, int active)
    {
        int index = Number(args, "layer", active);
        if (index < 0 || index >= layers.Count) throw new ArgumentException("图层索引超出范围。");
        return index;
    }

    internal static object Info(IReadOnlyList<Layer> layers, int active, int frameCount = 1, int activeFrame = 0, int fps = 8, bool playing = false)
    {
        var grid = layers[active].Grid;
        return new
        {
            width = grid.Width, height = grid.Height, activeLayer = active, frameCount, activeFrame, fps, playing,
            layers = layers.Select((l, i) => new { index = i, name = l.Name, visible = l.Visible }).ToArray()
        };
    }

    internal static object Read(JsonObject args, IReadOnlyList<Layer> layers, int active)
    {
        int layer = LayerIndex(args, layers, active);
        var grid = layers[layer].Grid;
        int x = Number(args, "x", 0), y = Number(args, "y", 0);
        int w = Number(args, "width", 1), h = Number(args, "height", 1);
        if (x < 0 || y < 0 || w < 1 || h < 1 || x >= grid.Width || y >= grid.Height || w > grid.Width - x || h > grid.Height - y || (long)w * h > MaxPixels)
            throw new ArgumentException("读取区域超出画布或超过 4096 像素。");
        var rows = new string[h][];
        for (int dy = 0; dy < h; dy++)
        {
            rows[dy] = new string[w];
            for (int dx = 0; dx < w; dx++)
            {
                int color = grid.Get(x + dx, y + dy);
                rows[dy][dx] = color == Argb.Transparent ? "transparent" : "#" + Argb.ToRgbHex(color);
            }
        }
        return new { layer, x, y, width = w, height = h, pixels = rows };
    }

    internal static bool Paint(JsonObject args, IReadOnlyList<Layer> layers, int active, Action<int[]>? commit = null)
    {
        int layerIndex = LayerIndex(args, layers, active);
        var layer = layers[layerIndex];
        if (!layer.Visible) throw new ArgumentException("不能修改隐藏图层，请先显示该图层。");
        if (args["pixels"] is not JsonArray pixels || pixels.Count is < 1 or > MaxPixels)
            throw new ArgumentException("pixels 必须包含 1–4096 个像素。");
        var validated = new List<(int X, int Y, int Color)>(pixels.Count);
        foreach (var item in pixels)
        {
            if (item is not JsonObject pixel) throw new ArgumentException("像素必须是对象。");
            int x = Number(pixel, "x"), y = Number(pixel, "y");
            if (!layer.Grid.InBounds(x, y)) throw new ArgumentException("像素坐标超出画布。");
            string color = Text(pixel, "color");
            int argb = ParseColor(color);
            validated.Add((x, y, argb));
        }
        bool mirrorH = Flag(args, "mirror_h"), mirrorV = Flag(args, "mirror_v");
        int[] before = layer.Grid.Snapshot();
        foreach (var p in validated)
        {
            layer.Grid.Set(p.X, p.Y, p.Color);
            if (mirrorH) layer.Grid.Set(layer.Grid.Width - 1 - p.X, p.Y, p.Color);
            if (mirrorV) layer.Grid.Set(p.X, layer.Grid.Height - 1 - p.Y, p.Color);
            if (mirrorH && mirrorV) layer.Grid.Set(layer.Grid.Width - 1 - p.X, layer.Grid.Height - 1 - p.Y, p.Color);
        }
        bool changed = !layer.Grid.RawPixels.AsSpan().SequenceEqual(before);
        if (changed) commit?.Invoke(before);
        return changed;
    }

    internal static int ParseColor(string color)
    {
        if (color.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return Argb.Transparent;
        if (color.StartsWith('#')) color = color[1..];
        if (color.Length != 6 || !int.TryParse(color, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int rgb))
            throw new ArgumentException("颜色必须为 #RRGGBB 或 transparent。");
        return Argb.FromRgb(rgb);
    }

    internal static (int Layer, int Count) ReplaceColor(JsonObject args, IReadOnlyList<Layer> layers, int active)
    {
        int index = LayerIndex(args, layers, active);
        var layer = layers[index];
        if (!layer.Visible) throw new ArgumentException("不能修改隐藏图层。");
        int from = ParseColor(Text(args, "from_color")), to = ParseColor(Text(args, "to_color"));
        if (from == to) return (index, 0);
        int count = layer.Grid.RawPixels.Count(pixel => pixel == from);
        if (count == 0) return (index, 0);
        int[] before = layer.Grid.Snapshot();
        for (int i = 0; i < layer.Grid.RawPixels.Length; i++) if (layer.Grid.RawPixels[i] == from) layer.Grid.RawPixels[i] = to;
        layer.History.Commit(before);
        return (index, count);
    }

    internal static bool Shape(JsonObject args, IReadOnlyList<Layer> layers, int active, Action<int[]>? commit = null)
    {
        int layerIndex = LayerIndex(args, layers, active);
        var layer = layers[layerIndex];
        if (!layer.Visible) throw new ArgumentException("不能修改隐藏图层。");
        var tool = Text(args, "shape") switch
        {
            "line" => ToolKind.Line,
            "rectangle" => ToolKind.Rectangle,
            "ellipse" => ToolKind.Ellipse,
            _ => throw new ArgumentException("shape 必须为 line、rectangle 或 ellipse。"),
        };
        int x1 = Number(args, "x1"), y1 = Number(args, "y1"), x2 = Number(args, "x2"), y2 = Number(args, "y2");
        int size = Number(args, "size", 1);
        if (!layer.Grid.InBounds(x1, y1) || !layer.Grid.InBounds(x2, y2) || size is < 1 or > 4)
            throw new ArgumentException("坐标超出画布或画笔大小不在 1–4 之间。");
        int color = ParseColor(Text(args, "color"));
        bool mirrorH = Flag(args, "mirror_h"), mirrorV = Flag(args, "mirror_v");
        int[] before = layer.Grid.Snapshot();
        PaintOps.DrawShape(layer.Grid, tool, x1, y1, x2, y2, size, color, mirrorH, mirrorV);
        bool changed = !layer.Grid.RawPixels.AsSpan().SequenceEqual(before);
        if (changed) commit?.Invoke(before);
        return changed;
    }

    internal static bool Fill(JsonObject args, IReadOnlyList<Layer> layers, int active, bool gradient, Action<int[]>? commit = null)
    {
        int index = LayerIndex(args, layers, active);
        var layer = layers[index];
        if (!layer.Visible) throw new ArgumentException("不能修改隐藏图层。");
        int x = Number(args, "x"), y = Number(args, "y");
        int endX = gradient ? Number(args, "end_x") : x, endY = gradient ? Number(args, "end_y") : y;
        if (!layer.Grid.InBounds(x, y) || !layer.Grid.InBounds(endX, endY)) throw new ArgumentException("坐标超出画布。");
        int start = ParseColor(Text(args, gradient ? "start_color" : "color"));
        int end = gradient ? ParseColor(Text(args, "end_color")) : start;
        if (gradient && (start == Argb.Transparent || end == Argb.Transparent)) throw new ArgumentException("渐变颜色必须是 #RRGGBB，不支持透明色。");
        bool mirrorH = Flag(args, "mirror_h"), mirrorV = Flag(args, "mirror_v");
        int[] before = layer.Grid.Snapshot();
        bool changed = gradient ? PaintOps.GradientFill(layer.Grid, x, y, endX, endY, start, end, mirrorH, mirrorV)
            : PaintOps.FloodFill(layer.Grid, x, y, start, mirrorH, mirrorV);
        if (changed) commit?.Invoke(before);
        return changed;
    }

    internal static bool TransferRegion(JsonObject args, IReadOnlyList<Layer> layers, int active, bool move, Action<int[]>? commit = null)
    {
        var layer = layers[LayerIndex(args, layers, active)];
        if (!layer.Visible) throw new ArgumentException("不能修改隐藏图层。");
        var grid = layer.Grid;
        int x = Number(args, "x"), y = Number(args, "y"), width = Number(args, "width"), height = Number(args, "height");
        int destX = Number(args, "dest_x"), destY = Number(args, "dest_y");
        if (width < 1 || height < 1 || x < 0 || y < 0 || destX < 0 || destY < 0 ||
            width > grid.Width || height > grid.Height || x > grid.Width - width || destX > grid.Width - width ||
            y > grid.Height - height || destY > grid.Height - height)
            throw new ArgumentException("源区域或目标区域超出画布。");
        if (move && x == destX && y == destY) return false;
        int[] before = grid.Snapshot();
        if (move)
            for (int dy = 0; dy < height; dy++) for (int dx = 0; dx < width; dx++) grid.Set(x + dx, y + dy, Argb.Transparent);
        for (int dy = 0; dy < height; dy++) for (int dx = 0; dx < width; dx++)
        {
            int color = before[(y + dy) * grid.Width + x + dx];
            if (color != Argb.Transparent) grid.Set(destX + dx, destY + dy, color);
        }
        bool changed = !grid.RawPixels.AsSpan().SequenceEqual(before);
        if (changed) commit?.Invoke(before);
        return changed;
    }

    internal static (int Active, bool Changed) ManageLayer(JsonObject args, List<Layer> layers, int active, string action)
    {
        int index = LayerIndex(args, layers, active);
        if (action == "delete")
        {
            if (args["confirm"]?.GetValue<bool>() != true) throw new ArgumentException("删除图层会丢掉该层像素（可用 canvas_undo 撤销），必须传入 confirm=true。");
            if (layers.Count == 1) throw new ArgumentException("至少保留一个图层。");
            layers.RemoveAt(index);
            if (index < active) active--;
            else if (index == active) active = Math.Min(active, layers.Count - 1);
            return (active, true);
        }
        if (action == "visibility")
        {
            if (args["visible"] is not JsonValue v || !v.TryGetValue<bool>(out bool visible)) throw new ArgumentException("visible 必须是布尔值。");
            bool changed = layers[index].Visible != visible;
            layers[index].Visible = visible;
            return (active, changed);
        }
        int target = Number(args, "target_index");
        if (target < 0 || target >= layers.Count) throw new ArgumentException("目标图层索引超出范围。");
        if (target == index) return (active, false);
        var selected = layers[active];
        var layer = layers[index];
        layers.RemoveAt(index);
        layers.Insert(target, layer);
        return (layers.IndexOf(selected), true);
    }

    internal static string NewPath(JsonObject args, string key, string extension, string? source = null, bool animation = false)
    {
        string path = args[key] is null ? OutputPaths.NewFile(extension, animation) : Text(args, key);
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{key} 必须是 {extension} 文件的绝对路径。");
        if (source is not null && string.Equals(Path.GetFullPath(source), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("输出路径不能与源文件相同。");
        if (File.Exists(path)) throw new IOException("输出文件已存在，请选择新路径。");
        string? parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(parent)) throw new ArgumentException($"{key} 的父目录无效。");
        Directory.CreateDirectory(parent);
        return path;
    }

    internal static void ExportPng(string path, IReadOnlyList<Layer> layers, int scale, bool overwrite = false)
    {
        var grid = layers[0].Grid;
        PngExport.Save(path, FrameComposite.Flatten(layers, grid.Width, grid.Height), grid.Width, grid.Height, scale, overwrite);
    }

    internal static int ExportSvg(string path, IReadOnlyList<Layer> layers, bool overwrite = false)
    {
        var grid = layers[0].Grid;
        return SvgExport.Save(path, FrameComposite.Flatten(layers, grid.Width, grid.Height), grid.Width, grid.Height, overwrite);
    }
}
