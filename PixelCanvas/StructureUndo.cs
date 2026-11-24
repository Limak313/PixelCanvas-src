namespace PixelCanvas;

/// <summary>
/// 文档结构（帧与图层）改动的撤销与重做：新增 / 复制 / 导入 / 删除 / 移动帧，增删图层、调整图层顺序、切换图层可见性。
/// 快照保存帧对象引用、每帧图层列表的结构副本以及各层可见性与名称，不复制像素；
/// 动作序号与图层像素历史共用 <see cref="History.NextSeq"/>，撤销 / 重做时按序号决定先执行哪一类。
/// </summary>
public sealed class StructureUndo
{
    /// <summary>最多保留的结构快照数。</summary>
    public const int MaxSteps = 32;

    /// <summary>快照间接持有的像素预算（按网格字节估算），超出后淘汰最旧快照。</summary>
    public const long MaxPixelBytes = 64L * 1024 * 1024;

    /// <summary>单帧结构：图层顺序副本 + 当前图层 + 各层可见性与名称（像素仍指向原图层对象）。</summary>
    internal sealed class FrameState
    {
        internal readonly AnimationFrame Frame;
        internal readonly List<Layer> Layers;
        internal readonly int ActiveLayer;
        internal readonly bool[] Visible;
        internal readonly string[] Names;

        internal FrameState(AnimationFrame frame)
        {
            Frame = frame;
            Layers = new List<Layer>(frame.Layers);
            ActiveLayer = frame.ActiveLayer;
            Visible = Layers.Select(layer => layer.Visible).ToArray();
            Names = Layers.Select(layer => layer.Name).ToArray();
        }
    }

    public sealed class Snapshot
    {
        internal readonly FrameState[] Frames;
        internal readonly int ActiveFrame;
        internal readonly long Seq;
        internal readonly long Bytes;

        internal Snapshot(FrameState[] frames, int activeFrame, long seq, long bytes)
        {
            Frames = frames;
            ActiveFrame = activeFrame;
            Seq = seq;
            Bytes = bytes;
        }

        public int FrameCount => Frames.Length;

        /// <summary>快照中当前帧的图层数。</summary>
        public int LayerCount => Frames.Length == 0 ? 0 : Frames[Math.Clamp(ActiveFrame, 0, Frames.Length - 1)].Layers.Count;
    }

    private readonly List<Snapshot> _undo = new();
    private readonly List<Snapshot> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>栈顶待撤销结构改动的序号；为空时返回 <see cref="long.MinValue"/>。</summary>
    public long UndoSeq => _undo.Count > 0 ? _undo[^1].Seq : long.MinValue;

    /// <summary>栈顶待重做结构改动的序号；为空时返回 <see cref="long.MaxValue"/>。</summary>
    public long RedoSeq => _redo.Count > 0 ? _redo[^1].Seq : long.MaxValue;

    /// <summary>当前结构的快照，供撤销 / 重做把现状压入对侧栈。</summary>
    public Snapshot Capture(IReadOnlyList<AnimationFrame> frames, int activeFrame) =>
        new(frames.Select(frame => new FrameState(frame)).ToArray(), activeFrame, History.NextSeq(), EstimateBytes(frames));

    /// <summary>把快照结构写回帧对象（图层列表、当前图层、可见性、名称），返回该快照的帧序列。</summary>
    public IReadOnlyList<AnimationFrame> Restore(Snapshot snapshot)
    {
        foreach (var state in snapshot.Frames)
        {
            for (int i = 0; i < state.Layers.Count; i++)
            {
                state.Layers[i].Visible = state.Visible[i];
                state.Layers[i].Name = state.Names[i];
            }
            state.Frame.Layers = new List<Layer>(state.Layers);
            state.Frame.ActiveLayer = Math.Clamp(state.ActiveLayer, 0, Math.Max(0, state.Layers.Count - 1));
        }
        return snapshot.Frames.Select(state => state.Frame).ToArray();
    }

    /// <summary>记录一次结构改动之前的快照；新动作同时使重做失效。</summary>
    public void Commit(Snapshot before)
    {
        _redo.Clear();
        _undo.Add(before);
        while (_undo.Count > MaxSteps) _undo.RemoveAt(0);
        while (_undo.Count > 1 && RetainedBytes() > MaxPixelBytes) _undo.RemoveAt(0);
    }

    public Snapshot Undo(Snapshot current)
    {
        if (!CanUndo) throw new InvalidOperationException("没有可撤销的结构操作。");
        var entry = Pop(_undo);
        _redo.Add(new Snapshot(current.Frames, current.ActiveFrame, entry.Seq, current.Bytes));
        return entry;
    }

    public Snapshot Redo(Snapshot current)
    {
        if (!CanRedo) throw new InvalidOperationException("没有可重做的结构操作。");
        var entry = Pop(_redo);
        _undo.Add(new Snapshot(current.Frames, current.ActiveFrame, entry.Seq, current.Bytes));
        return entry;
    }

    public void ClearRedo() => _redo.Clear();

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    private Snapshot Pop(List<Snapshot> stack)
    {
        var entry = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return entry;
    }

    private long RetainedBytes()
    {
        long total = 0;
        foreach (var entry in _undo) total += entry.Bytes;
        return total;
    }

    private static long EstimateBytes(IReadOnlyList<AnimationFrame> frames)
    {
        long total = 0;
        foreach (var frame in frames)
            foreach (var layer in frame.Layers)
                total += (long)layer.Grid.Width * layer.Grid.Height * sizeof(int);
        return total;
    }
}
