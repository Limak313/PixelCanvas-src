namespace PixelCanvas;

/// <summary>
/// 播放时钟：按累计耗时直接算出应显示的帧号，而不是每次 Tick 累加一帧。
/// WinForms 定时器实际精度约 15 ms，逐帧累加会让 30/60 fps 越播越慢；
/// 用已过时间反推帧号可让播放速率与设定帧率一致，仅受定时器单次精度限制。
/// </summary>
internal static class AnimationClock
{
    /// <summary>
    /// 从 <paramref name="startFrame"/> 起，播放 <paramref name="elapsedMs"/> 毫秒后应显示的帧索引（循环取模）。
    /// </summary>
    internal static int FrameIndex(long elapsedMs, int fps, int frameCount, int startFrame)
    {
        if (frameCount <= 1 || fps <= 0 || elapsedMs <= 0) return Math.Clamp(startFrame, 0, Math.Max(0, frameCount - 1));
        long advanced = (long)((double)elapsedMs * fps / 1000.0);
        long index = (startFrame + advanced) % frameCount;
        return (int)(index < 0 ? index + frameCount : index);
    }

    /// <summary>
    /// 定时器轮询间隔：取半个帧周期，让帧号计算有机会落在正确时刻；
    /// 下限 5 ms（不空转 CPU），上限 60 ms（低帧率时也保持响应）。
    /// </summary>
    internal static int PollIntervalMs(int fps) => Math.Clamp(500 / Math.Max(1, fps), 5, 60);
}
