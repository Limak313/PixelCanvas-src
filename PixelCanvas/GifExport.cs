using System.Text;

namespace PixelCanvas;

/// <summary>将完整帧写为循环 GIF；透明像素固定使用索引 0。</summary>
internal static class GifExport
{
    /// <param name="palette">
    /// 可选的固定索引色（ARGB，最多 255 色，不含透明）；按给定顺序写入全局色表，
    /// 画面外的颜色映射到最近色。传 null 时保持原有行为：≤255 色精确输出，超出则用内置色板量化。
    /// </param>
    internal static void Save(string path, IReadOnlyList<AnimationFrame> frames, int fps, int scale, bool overwrite = false, IReadOnlyList<int>? palette = null)
    {
        if (frames.Count is < 1 or > ProjectFile.MaxFrames || fps is < 1 or > ProjectFile.MaxFps || scale is < 1 or > 16)
            throw new ArgumentException("帧数、帧率或缩放倍数不合法。");
        var grid = frames[0].Layers[0].Grid;
        int width = grid.Width * scale, height = grid.Height * scale;
        if ((long)width * height * frames.Count > 16_777_216) throw new ArgumentException("GIF 总输出像素超过 16777216，请降低缩放倍数或帧数。");
        foreach (var frame in frames)
            if (frame.Layers.Count == 0 || frame.Layers.Any(l => l.Grid.Width != grid.Width || l.Grid.Height != grid.Height))
                throw new ArgumentException("动画帧尺寸不一致。");

        int[]? fixedPalette = null;
        Dictionary<int, int>? fixedIndex = null;
        if (palette is not null)
        {
            fixedPalette = NormalizePalette(palette, out fixedIndex);
        }

        var colors = new Dictionary<int, byte>();
        bool quantize = false;
        if (fixedPalette is null)
        {
            foreach (var frame in frames)
            {
                foreach (int color in Composite(frame, grid.Width, grid.Height))
                {
                    if (color == Argb.Transparent || colors.ContainsKey(color)) continue;
                    if (colors.Count == 255) { quantize = true; break; }
                    colors.Add(color, (byte)(colors.Count + 1));
                }
                if (quantize) break;
            }
        }

        AtomicFile.Write(path, stream =>
        {
            using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
            writer.Write(Encoding.ASCII.GetBytes("GIF89a")); writer.Write((ushort)width); writer.Write((ushort)height);
            writer.Write((byte)0xF7); writer.Write((byte)0); writer.Write((byte)0);
            var table = new byte[768];
            if (fixedPalette is not null)
            {
                for (int i = 0; i < fixedPalette.Length; i++)
                {
                    int offset = (i + 1) * 3;
                    table[offset] = (byte)(fixedPalette[i] >> 16);
                    table[offset + 1] = (byte)(fixedPalette[i] >> 8);
                    table[offset + 2] = (byte)fixedPalette[i];
                }
            }
            else if (quantize)
            {
                for (int r = 0; r < 6; r++) for (int g = 0; g < 7; g++) for (int b = 0; b < 6; b++)
                { int i = (1 + (r * 7 + g) * 6 + b) * 3; table[i] = (byte)(r * 51); table[i + 1] = (byte)((g * 255 + 3) / 6); table[i + 2] = (byte)(b * 51); }
            }
            else foreach (var entry in colors)
            { int i = entry.Value * 3; table[i] = (byte)(entry.Key >> 16); table[i + 1] = (byte)(entry.Key >> 8); table[i + 2] = (byte)entry.Key; }
            writer.Write(table);

            var nearestCache = new Dictionary<int, int>();
            int IndexOf(int color)
            {
                if (color == Argb.Transparent) return 0;
                if (fixedPalette is not null)
                {
                    if (fixedIndex!.TryGetValue(color, out int exact)) return exact;
                    if (nearestCache.TryGetValue(color, out int cached)) return cached;
                    int best = 1, bestDistance = int.MaxValue;
                    int r = color >> 16 & 255, g = color >> 8 & 255, b = color & 255;
                    for (int i = 0; i < fixedPalette.Length; i++)
                    {
                        int dr = r - (fixedPalette[i] >> 16), dg = g - (fixedPalette[i] >> 8 & 255), db = b - (fixedPalette[i] & 255);
                        int distance = dr * dr + dg * dg + db * db;
                        if (distance < bestDistance) { bestDistance = distance; best = i + 1; }
                        if (distance == 0) break;
                    }
                    nearestCache[color] = best;
                    return best;
                }
                return quantize
                    ? 1 + ((((color >> 16 & 255) * 5 + 127) / 255 * 7 + ((color >> 8 & 255) * 6 + 127) / 255) * 6 + ((color & 255) * 5 + 127) / 255)
                    : colors[color];
            }

            writer.Write(new byte[] { 0x21, 0xFF, 0x0B }); writer.Write(Encoding.ASCII.GetBytes("NETSCAPE2.0")); writer.Write(new byte[] { 3, 1, 0, 0, 0 });
            for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
            {
                int delay = (int)Math.Round((frameIndex + 1) * 100.0 / fps) - (int)Math.Round(frameIndex * 100.0 / fps);
                writer.Write(new byte[] { 0x21, 0xF9, 4, 9 }); writer.Write((ushort)delay); writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((byte)0x2C); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)width); writer.Write((ushort)height); writer.Write((byte)0);
                writer.Write((byte)8);
                int[] pixels = Composite(frames[frameIndex], grid.Width, grid.Height);
                using var compressed = new MemoryStream();
                int bitBuffer = 0, bitCount = 0, count = 0;
                void Code(int value) { bitBuffer |= value << bitCount; bitCount += 9; while (bitCount >= 8) { compressed.WriteByte((byte)bitBuffer); bitBuffer >>= 8; bitCount -= 8; } }
                Code(256);
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                {
                    int color = pixels[(y / scale) * grid.Width + x / scale];
                    Code(IndexOf(color));
                    if (++count == 200) { Code(256); count = 0; }
                }
                Code(257); if (bitCount > 0) compressed.WriteByte((byte)bitBuffer);
                byte[] bytes = compressed.ToArray();
                for (int offset = 0; offset < bytes.Length;) { int length = Math.Min(255, bytes.Length - offset); writer.Write((byte)length); writer.Write(bytes, offset, length); offset += length; }
                writer.Write((byte)0);
            }
            writer.Write((byte)0x3B);
        }, overwrite);
    }

    /// <summary>校验固定色板：1–255 个不透明 RGB 颜色，重复色按首次出现保留。</summary>
    private static int[] NormalizePalette(IReadOnlyList<int> palette, out Dictionary<int, int> index)
    {
        if (palette.Count is < 1 or > 255) throw new ArgumentException("GIF 索引色必须为 1–255 种。");
        var colors = new List<int>(palette.Count);
        index = new Dictionary<int, int>(palette.Count);
        foreach (int argb in palette)
        {
            if ((uint)argb >> 24 != 0xFF) throw new ArgumentException("GIF 索引色必须为不透明颜色。");
            int rgb = argb & 0xFFFFFF;
            if (index.ContainsKey(rgb)) continue;
            colors.Add(rgb);
            index[rgb] = colors.Count;   // 索引 0 留给透明像素
        }
        if (colors.Count == 0) throw new ArgumentException("GIF 索引色不能为空。");
        return colors.ToArray();
    }

    private static int[] Composite(AnimationFrame frame, int width, int height)
    {
        var pixels = new int[width * height];
        foreach (var layer in frame.Layers) if (layer.Visible)
            for (int i = 0; i < pixels.Length; i++) if (layer.Grid.RawPixels[i] != Argb.Transparent) pixels[i] = layer.Grid.RawPixels[i];
        return pixels;
    }
}
