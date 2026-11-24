namespace PixelCanvas;

internal static class SpriteSheetExport
{
    internal sealed record Layout(int Columns, int Rows, int FrameWidth, int FrameHeight, int Width, int Height, int FrameCount, int Scale)
    {
        internal object ToData() => new { columns = Columns, rows = Rows, frameWidth = FrameWidth, frameHeight = FrameHeight, width = Width, height = Height, frameCount = FrameCount, scale = Scale };
    }

    internal static Layout Save(string path, IReadOnlyList<AnimationFrame> frames, int columns, int scale, bool overwrite = false)
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

        var sheet = new int[width * height];   // 默认 0 = 透明
        var framePixels = new int[grid.Width * grid.Height];
        for (int i = 0; i < frames.Count; i++)
        {
            Array.Clear(framePixels);
            foreach (var layer in frames[i].Layers) if (layer.Visible)
                for (int j = 0; j < framePixels.Length; j++) if (layer.Grid.RawPixels[j] != Argb.Transparent) framePixels[j] = layer.Grid.RawPixels[j];
            int left = i % columns * frameWidth, top = i / columns * frameHeight;
            for (int y = 0; y < frameHeight; y++)
            {
                int sourceRow = y / scale * grid.Width;
                int targetRow = (top + y) * width + left;
                for (int x = 0; x < frameWidth; x++) sheet[targetRow + x] = framePixels[sourceRow + x / scale];
            }
        }
        PngExport.Save(path, sheet, width, height, 1, overwrite);
        return new Layout(columns, rows, frameWidth, frameHeight, width, height, frames.Count, scale);
    }
}
