namespace PixelCanvas;

/// <summary>
/// 帧结构改动（新增、复制、导入、删除、移动、批量加帧）的撤销与重做。
/// 快照只保存帧对象引用与当前帧索引，不复制像素；动作序号与图层像素历史共用
/// <see cref="History.NextSeq"/>，撤销/重做时按序号决定先执行哪一类。
/// </summary>
public sealed class FrameUndo
{
    /// <summary>最多保留的帧结构快照数。</summary>
    public const int MaxSteps = 32;

    /// <summary>快照间接持有的像素预算（按网格字节估算），超出后淘汰最旧快照。</summary>
    public const long MaxPixelBytes = 64L * 1024 * 1024;

    public sealed class Snapshot
    {
        internal readonly AnimationFrame[] Frames;
        internal readonly int ActiveFrame;
        internal readonly long Seq;
        internal readonly long Bytes;

        internal Snapshot(AnimationFrame[] frames, int activeFrame, long seq, long bytes)
        {
            Frames = frames;
            ActiveFrame = activeFrame;
            Seq = seq;
            Bytes = bytes;
        }

        public int FrameCount => Frames.Length;
    }

    private readonly List<Snapshot> _undo = new();
    private readonly List<Snapshot> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>栈顶待撤销结构改动的序号；为空时返回 <see cref="long.MinValue"/>。</summary>
    public long UndoSeq => _undo.Count > 0 ? _undo[^1].Seq : long.MinValue;

    /// <summary>栈顶待重做结构改动的序号；为空时返回 <see cref="long.MaxValue"/>。</summary>
    public long RedoSeq => _redo.Count > 0 ? _redo[^1].Seq : long.MaxValue;

    /// <summary>当前结构的快照，供撤销/重做把现状压入对侧栈。</summary>
    public Snapshot Capture(IReadOnlyList<AnimationFrame> frames, int activeFrame) =>
        new(frames.ToArray(), activeFrame, History.NextSeq(), EstimateBytes(frames));

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
        if (!CanUndo) throw new InvalidOperationException("没有可撤销的帧操作。");
        var entry = Pop(_undo);
        _redo.Add(new Snapshot(current.Frames, current.ActiveFrame, entry.Seq, current.Bytes));
        return entry;
    }

    public Snapshot Redo(Snapshot current)
    {
        if (!CanRedo) throw new InvalidOperationException("没有可重做的帧操作。");
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
