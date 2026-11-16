using System.Drawing;

namespace PixelCanvas;

/// <summary>动画中的一帧；每帧拥有独立图层与撤销历史。</summary>
public sealed class AnimationFrame
{
    public List<Layer> Layers { get; set; }
    public int ActiveLayer { get; set; }
    public AnimationFrame(List<Layer> layers, int activeLayer = 0) { Layers = layers; ActiveLayer = activeLayer; }

    public static AnimationFrame FromImage(Image source, int width, int height)
    {
        if (source.Width != width || source.Height != height) throw new ArgumentException($"图片尺寸须与画布一致（{width}×{height}）。");
        using var bitmap = new Bitmap(source);
        var grid = new PixelGrid(width, height);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            Color color = bitmap.GetPixel(x, y);
            grid.Set(x, y, color.A < 128 ? Argb.Transparent : Argb.FromRgb(color.ToArgb()));
        }
        return new AnimationFrame(new List<Layer> { new("图层 1", grid) });
    }
}
