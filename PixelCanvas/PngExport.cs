using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PixelCanvas;

/// <summary>把 32 位 ARGB 像素缓冲放大并写为 PNG（最近邻、保留透明），各导出路径共用。</summary>
internal static class PngExport
{
    internal const long MaxPixels = 16_777_216;

    internal static void Save(string path, int[] pixels, int width, int height, int scale, bool overwrite = false)
    {
        if (scale is < 1 or > 16) throw new ArgumentException("放大倍数必须为 1–16。");
        if (pixels.Length != width * height) throw new ArgumentException("像素数量与画布尺寸不一致。");
        int outWidth = width * scale, outHeight = height * scale;
        if ((long)outWidth * outHeight > MaxPixels) throw new ArgumentException("导出图片超过 16777216 像素，请降低放大倍数。");
        using var native = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var bits = native.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(pixels, 0, bits.Scan0, pixels.Length); } finally { native.UnlockBits(bits); }
        using var image = new Bitmap(outWidth, outHeight, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(native, new Rectangle(0, 0, outWidth, outHeight), new Rectangle(0, 0, width, height), GraphicsUnit.Pixel);
        }
        AtomicFile.Write(path, stream => image.Save(stream, ImageFormat.Png), overwrite);
    }
}
