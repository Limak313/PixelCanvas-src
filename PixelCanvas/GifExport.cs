using System.Text;

namespace PixelCanvas;

/// <summary>将完整帧写为循环 GIF；透明像素使用索引 0。</summary>
internal static class GifExport
{
    internal static void Save(string path, IReadOnlyList<AnimationFrame> frames, int fps, int scale)
    {
        if (frames.Count is < 1 or > ProjectFile.MaxFrames || fps is < 1 or > ProjectFile.MaxFps || scale is < 1 or > 16)
            throw new ArgumentException("帧数、帧率或缩放倍数不合法。");
        var grid = frames[0].Layers[0].Grid;
        int width = grid.Width * scale, height = grid.Height * scale;
        if ((long)width * height * frames.Count > 16_777_216) throw new ArgumentException("GIF 总输出像素超过 16777216，请降低缩放倍数或帧数。");
        var colors = new Dictionary<int, byte>();
        bool quantize = false;
        foreach (var frame in frames)
        {
            if (frame.Layers.Count == 0 || frame.Layers.Any(l => l.Grid.Width != grid.Width || l.Grid.Height != grid.Height)) throw new ArgumentException("动画帧尺寸不一致。");
            foreach (int color in Composite(frame, grid.Width, grid.Height))
            {
                if (color == Argb.Transparent || colors.ContainsKey(color)) continue;
                if (colors.Count == 255) { quantize = true; break; }
                colors.Add(color, (byte)(colors.Count + 1));
            }
            if (quantize) break;
        }
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
            writer.Write(Encoding.ASCII.GetBytes("GIF89a")); writer.Write((ushort)width); writer.Write((ushort)height);
            writer.Write((byte)0xF7); writer.Write((byte)0); writer.Write((byte)0);
            var palette = new byte[768];
            if (quantize)
            {
                for (int r = 0; r < 6; r++) for (int g = 0; g < 7; g++) for (int b = 0; b < 6; b++)
                { int i = (1 + (r * 7 + g) * 6 + b) * 3; palette[i] = (byte)(r * 51); palette[i + 1] = (byte)((g * 255 + 3) / 6); palette[i + 2] = (byte)(b * 51); }
            }
            else foreach (var entry in colors)
            { int i = entry.Value * 3; palette[i] = (byte)(entry.Key >> 16); palette[i + 1] = (byte)(entry.Key >> 8); palette[i + 2] = (byte)entry.Key; }
            writer.Write(palette);
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
                    int index = color == Argb.Transparent ? 0 : quantize
                        ? 1 + ((((color >> 16 & 255) * 5 + 127) / 255 * 7 + ((color >> 8 & 255) * 6 + 127) / 255) * 6 + ((color & 255) * 5 + 127) / 255)
                        : colors[color];
                    Code(index);
                    if (++count == 200) { Code(256); count = 0; }
                }
                Code(257); if (bitCount > 0) compressed.WriteByte((byte)bitBuffer);
                byte[] bytes = compressed.ToArray();
                for (int offset = 0; offset < bytes.Length;) { int length = Math.Min(255, bytes.Length - offset); writer.Write((byte)length); writer.Write(bytes, offset, length); offset += length; }
                writer.Write((byte)0);
            }
            writer.Write((byte)0x3B);
        }
        catch { stream.Dispose(); try { File.Delete(path); } catch { } throw; }
    }

    private static int[] Composite(AnimationFrame frame, int width, int height)
    {
        var pixels = new int[width * height];
        foreach (var layer in frame.Layers) if (layer.Visible)
            for (int i = 0; i < pixels.Length; i++) if (layer.Grid.RawPixels[i] != Argb.Transparent) pixels[i] = layer.Grid.RawPixels[i];
        return pixels;
    }
}
