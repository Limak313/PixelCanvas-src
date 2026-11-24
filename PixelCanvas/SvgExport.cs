using System.Globalization;
using System.Text;

namespace PixelCanvas;

/// <summary>
/// 把一帧合成后的像素导出为 SVG 矢量色块：相邻同色像素合并成矩形（先横向连续、再纵向同宽同色），
/// 透明像素不输出。放大倍数由查看端任意缩放，因此不做 scale 参数。
/// </summary>
internal static class SvgExport
{
    private sealed record Rect(int X, int Y, int Width, int Height, int Color);

    internal static int Save(string path, int[] pixels, int width, int height, bool overwrite = false)
    {
        if (pixels.Length != width * height) throw new ArgumentException("像素数量与画布尺寸不一致。");
        var rects = BuildRects(pixels, width, height);
        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" shape-rendering=\"crispEdges\">\n");
        foreach (var rect in rects)
            svg.Append(CultureInfo.InvariantCulture, $"<rect x=\"{rect.X}\" y=\"{rect.Y}\" width=\"{rect.Width}\" height=\"{rect.Height}\" fill=\"#{Argb.ToRgbHex(rect.Color)}\"/>\n");
        svg.Append("</svg>\n");
        AtomicFile.WriteText(path, svg.ToString(), overwrite);
        return rects.Count;
    }

    private static List<Rect> BuildRects(int[] pixels, int width, int height)
    {
        var rects = new List<Rect>();
        var open = new Dictionary<(int X, int Length, int Color), int>();   // 上一行仍在延伸的矩形
        for (int y = 0; y < height; y++)
        {
            var next = new Dictionary<(int X, int Length, int Color), int>();
            int x = 0;
            while (x < width)
            {
                int color = pixels[y * width + x];
                int length = 1;
                while (x + length < width && pixels[y * width + x + length] == color) length++;
                if (color != Argb.Transparent)
                {
                    var key = (x, length, color);
                    if (open.TryGetValue(key, out int index)) rects[index] = rects[index] with { Height = rects[index].Height + 1 };
                    else { index = rects.Count; rects.Add(new Rect(x, y, length, 1, color)); }
                    next[key] = index;
                }
                x += length;
            }
            open = next;
        }
        return rects;
    }
}
