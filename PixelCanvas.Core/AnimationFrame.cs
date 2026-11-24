namespace PixelCanvas;

/// <summary>动画中的一帧；每帧拥有独立图层与撤销历史。</summary>
public sealed class AnimationFrame
{
    public List<Layer> Layers { get; set; }
    public int ActiveLayer { get; set; }
    public AnimationFrame(List<Layer> layers, int activeLayer = 0) { Layers = layers; ActiveLayer = activeLayer; }
}
