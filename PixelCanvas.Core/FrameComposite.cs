namespace PixelCanvas;

/// <summary>可见图层合成：自底向上覆盖，透明像素不遮挡下层。导出与预览共用同一规则。</summary>
internal static class FrameComposite
{
    internal static int[] Flatten(IReadOnlyList<Layer> layers, int width, int height)
    {
        var pixels = new int[width * height];
        foreach (var layer in layers)
        {
            if (!layer.Visible) continue;
            var source = layer.Grid.RawPixels;
            if (source.Length != pixels.Length) throw new ArgumentException("图层尺寸与画布不一致。");
            for (int i = 0; i < pixels.Length; i++) if (source[i] != Argb.Transparent) pixels[i] = source[i];
        }
        return pixels;
    }

    internal static int[] Flatten(AnimationFrame frame, int width, int height) => Flatten(frame.Layers, width, height);
}
