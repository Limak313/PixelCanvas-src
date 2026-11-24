using System.IO.Compression;
using System.Text;

namespace PixelCanvas;

/// <summary>把 32 位 ARGB 像素缓冲放大并写为 PNG（最近邻、保留透明）；不依赖 System.Drawing，可跨平台运行。</summary>
internal static class PngExport
{
    internal const long MaxPixels = 16_777_216;
    private static readonly uint[] CrcTable = BuildCrcTable();

    internal static void Save(string path, int[] pixels, int width, int height, int scale, bool overwrite = false)
    {
        if (scale is < 1 or > 16) throw new ArgumentException("放大倍数必须为 1–16。");
        if (width is < 1 or > 16384 || height is < 1 or > 16384) throw new ArgumentException("图片边长超出 PNG 上限 16384。");
        if (pixels.Length != width * height) throw new ArgumentException("像素数量与画布尺寸不一致。");
        int outWidth = width * scale, outHeight = height * scale;
        if ((long)outWidth * outHeight > MaxPixels) throw new ArgumentException("导出图片超过 16777216 像素，请降低放大倍数。");
        if (scale == 1)
        {
            AtomicFile.Write(path, stream => Write(stream, pixels, width, height), overwrite);
            return;
        }
        var scaled = new int[outWidth * outHeight];
        for (int y = 0; y < outHeight; y++)
        {
            int sourceRow = y / scale * width;
            for (int x = 0; x < outWidth; x++) scaled[y * outWidth + x] = pixels[sourceRow + x / scale];
        }
        AtomicFile.Write(path, stream => Write(stream, scaled, outWidth, outHeight), overwrite);
    }

    /// <summary>写入 RGBA8 PNG；像素为 0xAARRGGBB，alpha 为 0 的像素按透明输出。</summary>
    internal static void Write(Stream stream, int[] pixels, int width, int height)
    {
        stream.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var header = new byte[13];
        WriteInt32(header, 0, width); WriteInt32(header, 4, height);
        header[8] = 8;    // bit depth
        header[9] = 6;    // RGBA
        header[10] = 0; header[11] = 0; header[12] = 0;
        WriteChunk(stream, "IHDR", header);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, true))
        {
            for (int y = 0; y < height; y++)
            {
                zlib.WriteByte(0);   // filter: none
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int color = pixels[row + x];
                    if ((color & 0xFF000000) == 0) { zlib.WriteByte(0); zlib.WriteByte(0); zlib.WriteByte(0); zlib.WriteByte(0); }
                    else
                    {
                        zlib.WriteByte((byte)(color >> 16));
                        zlib.WriteByte((byte)(color >> 8));
                        zlib.WriteByte((byte)color);
                        zlib.WriteByte((byte)(color >> 24));
                    }
                }
            }
        }
        WriteChunk(stream, "IDAT", compressed.ToArray());
        WriteChunk(stream, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        var buffer = new byte[4 + data.Length];
        Buffer.BlockCopy(typeBytes, 0, buffer, 0, 4);
        Buffer.BlockCopy(data, 0, buffer, 4, data.Length);
        var length = new byte[4]; WriteInt32(length, 0, data.Length);
        stream.Write(length);
        stream.Write(buffer);
        var crc = new byte[4]; WriteUInt32(crc, 0, Crc32(buffer));
        stream.Write(crc);
    }

    private static void WriteInt32(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24); target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8); target[offset + 3] = (byte)value;
    }

    private static void WriteUInt32(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24); target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8); target[offset + 3] = (byte)value;
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in data) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++) value = (value & 1) == 1 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }
}
