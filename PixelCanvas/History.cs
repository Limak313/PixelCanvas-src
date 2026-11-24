namespace PixelCanvas;

/// <summary>各图层共享的历史内存预算，按快照进入历史的先后顺序淘汰。</summary>
public sealed class HistoryBudget
{
    public const long DefaultMaxBytes = 64L * 1024 * 1024;
    private readonly long _maxBytes;
    private long _bytes;
    private readonly LinkedList<History.Entry> _entries = new();

    public HistoryBudget(long maxBytes = DefaultMaxBytes)
    {
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _maxBytes = maxBytes;
    }

    internal void Add(History.Entry entry)
    {
        entry.Node = _entries.AddLast(entry);
        _bytes += (long)entry.Pixels.Length * sizeof(int);
        while (_bytes > _maxBytes && _entries.First is { } first)
            first.Value.Owner.Evict(first.Value);
    }

    internal void Remove(History.Entry entry)
    {
        if (entry.Node is null) return;
        _entries.Remove(entry.Node);
        entry.Node = null;
        _bytes -= (long)entry.Pixels.Length * sizeof(int);
    }
}

/// <summary>每层最多 100 步，所有图层的撤销和重做快照共享内存预算。</summary>
public sealed class History
{
    internal sealed class Entry
    {
        internal readonly History Owner;
        internal readonly int[] Pixels;
        internal readonly long Seq;
        internal LinkedListNode<Entry>? Node;
        internal Entry(History owner, int[] pixels, long seq) { Owner = owner; Pixels = pixels; Seq = seq; }
    }

    private static long _seqCounter;

    /// <summary>全局动作序号：图层像素提交与帧结构改动共用同一计数器，用于决定撤销/重做的先后。</summary>
    public static long NextSeq() => Interlocked.Increment(ref _seqCounter);

    private readonly List<Entry> _undo = new();
    private readonly List<Entry> _redo = new();
    private readonly HistoryBudget _budget;
    private const int MaxSteps = 100;

    public History(HistoryBudget? budget = null) => _budget = budget ?? new HistoryBudget();
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>栈顶待撤销动作的序号；为空时返回 <see cref="long.MinValue"/>。</summary>
    public long UndoSeq => _undo.Count > 0 ? _undo[^1].Seq : long.MinValue;

    /// <summary>栈顶待重做动作的序号；为空时返回 <see cref="long.MaxValue"/>。</summary>
    public long RedoSeq => _redo.Count > 0 ? _redo[^1].Seq : long.MaxValue;

    private void Push(List<Entry> stack, int[] pixels, long seq)
    {
        var entry = new Entry(this, pixels, seq);
        stack.Add(entry);
        _budget.Add(entry);
    }

    private Entry Pop(List<Entry> stack)
    {
        var entry = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        _budget.Remove(entry);
        return entry;
    }

    internal void Evict(Entry entry)
    {
        _undo.Remove(entry);
        _redo.Remove(entry);
        _budget.Remove(entry);
    }

    private void ClearStack(List<Entry> stack)
    {
        foreach (var entry in stack) _budget.Remove(entry);
        stack.Clear();
    }

    public void Commit(int[] beforeSnapshot)
    {
        ClearStack(_redo);
        Push(_undo, beforeSnapshot, NextSeq());
        if (_undo.Count > MaxSteps) Evict(_undo[0]);
    }

    public int[] Undo(int[] currentSnapshot)
    {
        if (!CanUndo) throw new InvalidOperationException("没有可撤销的历史。");
        var entry = Pop(_undo);
        Push(_redo, currentSnapshot, entry.Seq);   // 重做条目沿用被撤销动作的序号
        return entry.Pixels;
    }

    public int[] Redo(int[] currentSnapshot)
    {
        if (!CanRedo) throw new InvalidOperationException("没有可重做的历史。");
        var entry = Pop(_redo);
        Push(_undo, currentSnapshot, entry.Seq);
        return entry.Pixels;
    }

    /// <summary>清空重做栈：结构改动等新动作发生后，重做不再可用。</summary>
    public void ClearRedo() => ClearStack(_redo);

    public void Clear() { ClearStack(_undo); ClearStack(_redo); }
}
