namespace PixelCanvas;

/// <summary>撤销/重做历史：保存每步操作之前的画布快照，最多 100 步，超出丢弃最旧。</summary>
public sealed class History
{
    private readonly List<int[]> _undo = new();
    private readonly List<int[]> _redo = new();
    private const int MaxSteps = 100;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>提交一步（传入操作前的快照），并清空重做栈。</summary>
    public void Commit(int[] beforeSnapshot)
    {
        _undo.Add(beforeSnapshot);
        if (_undo.Count > MaxSteps) _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>撤销：返回操作前的快照，同时把当前状态压入重做栈。</summary>
    public int[] Undo(int[] currentSnapshot)
    {
        if (!CanUndo) throw new InvalidOperationException("没有可撤销的历史。");
        int[] before = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(currentSnapshot);
        return before;
    }

    /// <summary>重做：返回重做快照，同时把当前状态压回撤销栈。</summary>
    public int[] Redo(int[] currentSnapshot)
    {
        if (!CanRedo) throw new InvalidOperationException("没有可重做的历史。");
        int[] next = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(currentSnapshot);
        return next;
    }

    /// <summary>清空全部历史（切换尺寸 / 载入工程时调用）。</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
