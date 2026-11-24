namespace PixelCanvas;

/// <summary>
/// 数据层：像素网格。每个格子一个 int，0 表示透明，非零表示 0xAARRGGBB。
/// 按行存储，索引 = y * Width + x。
/// </summary>
public sealed class PixelGrid
{
    private readonly int[] _pixels;

    public int Width { get; }
    public int Height { get; }

    public PixelGrid(int width, int height)
    {
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException($"画布尺寸必须为正整数：{width}×{height}");
        Width = width;
        Height = height;
        _pixels = new int[width * height];
    }

    /// <summary>底层像素数组（供渲染整块拷贝与测试比对；索引 = y * Width + x）。</summary>
    public int[] RawPixels => _pixels;

    public bool InBounds(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;

    /// <summary>读取格子颜色；越界返回 -1。</summary>
    public int Get(int x, int y) => InBounds(x, y) ? _pixels[y * Width + x] : -1;

    /// <summary>写入格子颜色（越界忽略）。argb 为 0 表示透明。</summary>
    public void Set(int x, int y, int argb)
    {
        if (InBounds(x, y)) _pixels[y * Width + x] = argb;
    }

    /// <summary>全部置为透明。</summary>
    public void Clear() => Array.Clear(_pixels);

    /// <summary>当前内容的快照（供撤销历史使用）。</summary>
    public int[] Snapshot() => (int[])_pixels.Clone();

    /// <summary>用快照恢复内容（长度必须与画布一致）。</summary>
    public void Restore(int[] snapshot)
    {
        if (snapshot.Length != _pixels.Length)
            throw new ArgumentException("快照长度与画布尺寸不一致。");
        Array.Copy(snapshot, _pixels, _pixels.Length);
    }
}
