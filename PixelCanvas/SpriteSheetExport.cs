using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PixelCanvas;

internal static class SpriteSheetExport
{
    internal sealed record Layout(int Columns, int Rows, int FrameWidth, int FrameHeight, int Width, int Height, int FrameCount, int Scale)
    {
        internal object ToData() => new { columns = Columns, rows = Rows, frameWidth = FrameWidth, frameHeight = FrameHeight, width = Width, height = Height, frameCount = FrameCount, scale = Scale };
    }

    internal static Layout Save(string path, IReadOnlyList<AnimationFrame> frames, int columns, int scale)
    {
        if (frames.Count is < 1 or > ProjectFile.MaxFrames || columns is < 1 or > ProjectFile.MaxFrames || scale is < 1 or > 16)
            throw new ArgumentException("帧数、列数或放大倍数不合法。");
        var grid = frames[0].Layers[0].Grid;
        int rows = (frames.Count + columns - 1) / columns;
        int frameWidth = checked(grid.Width * scale), frameHeight = checked(grid.Height * scale);
        int width = checked(frameWidth * columns), height = checked(frameHeight * rows);
        if (width > 16384 || height > 16384 || (long)width * height > 16_777_216)
            throw new ArgumentException("精灵表尺寸超过上限（边长 16384、总像素 16777216），请减少列数或放大倍数。");
        if (frames.Any(frame => frame.Layers.Count == 0 || frame.Layers.Any(layer => layer.Grid.Width != grid.Width || layer.Grid.Height != grid.Height)))
            throw new ArgumentException("动画帧尺寸不一致。");
        using var sheet = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(sheet))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.Clear(Color.Transparent);
            using var image = new Bitmap(grid.Width, grid.Height, PixelFormat.Format32bppArgb);
            var pixels = new int[grid.Width * grid.Height];
            for (int i = 0; i < frames.Count; i++)
            {
                Array.Clear(pixels);
                foreach (var layer in frames[i].Layers) if (layer.Visible)
                    for (int j = 0; j < pixels.Length; j++) if (layer.Grid.RawPixels[j] != Argb.Transparent) pixels[j] = layer.Grid.RawPixels[j];
                var data = image.LockBits(new Rectangle(0, 0, grid.Width, grid.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); } finally { image.UnlockBits(data); }
                graphics.DrawImage(image, new Rectangle(i % columns * frameWidth, i / columns * frameHeight, frameWidth, frameHeight),
                    new Rectangle(0, 0, grid.Width, grid.Height), GraphicsUnit.Pixel);
            }
        }
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try { sheet.Save(stream, ImageFormat.Png); }
        catch { stream.Dispose(); try { File.Delete(path); } catch { } throw; }
        return new Layout(columns, rows, frameWidth, frameHeight, width, height, frames.Count, scale);
    }
}
