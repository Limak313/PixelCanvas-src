using PixelCanvas;
using Xunit;

namespace PixelCanvas.Tests;

/// <summary>
/// 撤销历史落盘：自动恢复副本旁的 .undo 文件保存像素撤销/重做栈，
/// 载入时按尺寸、帧数、图层数校验，全部通过才套用，否则整份丢弃。
/// </summary>
public class UndoJournalTests
{
    private static readonly int Red = Argb.FromRgbHex("FF0000");
    private static readonly int Blue = Argb.FromRgbHex("0000FF");

    private static List<AnimationFrame> Frames(int width = 4, int height = 4, int frameCount = 1, int layerCount = 1)
    {
        var frames = new List<AnimationFrame>();
        for (int f = 0; f < frameCount; f++)
        {
            var frameLayers = new List<Layer>();
            for (int l = 0; l < layerCount; l++) frameLayers.Add(new Layer($"L{l}", new PixelGrid(width, height)));
            frames.Add(new AnimationFrame(frameLayers));
        }
        return frames;
    }

    private static void Stroke(Layer layer, int x, int y, int color)
    {
        var before = layer.Grid.Snapshot();
        layer.Grid.Set(x, y, color);
        layer.History.Commit(before);
    }

    [Fact]
    public void SaveThenLoad_RestoresUndoStackInOriginalOrder()
    {
        using var temp = new TempDir();
        var frames = Frames();
        var layer = frames[0].Layers[0];
        Stroke(layer, 1, 0, Red);
        Stroke(layer, 2, 0, Blue);
        string path = temp.File("doc.undo");

        UndoJournal.Save(path, frames);

        var restoredFrames = Frames();
        var restored = restoredFrames[0].Layers[0];
        Assert.True(UndoJournal.TryLoad(path, restoredFrames, out int steps));
        Assert.Equal(2, steps);
        Assert.True(restored.History.CanUndo);
        Assert.False(restored.History.CanRedo);

        var current = restored.Grid.Snapshot();
        var first = restored.History.Undo(current);
        Assert.Equal(Red, first[0 * 4 + 1]);
        Assert.Equal(Argb.Transparent, first[0 * 4 + 2]);

        var second = restored.History.Undo(first);
        Assert.Equal(Argb.Transparent, second[0 * 4 + 1]);
    }

    [Fact]
    public void SaveThenLoad_RestoresRedoStack()
    {
        using var temp = new TempDir();
        var frames = Frames();
        var layer = frames[0].Layers[0];
        Stroke(layer, 1, 0, Red);
        var afterRed = layer.Grid.Snapshot();
        Stroke(layer, 2, 0, Blue);
        var afterBlue = layer.Grid.Snapshot();
        layer.History.Undo(afterBlue);   // 撤销一次，产生一个重做条目
        string path = temp.File("doc.undo");

        UndoJournal.Save(path, frames);

        var restoredFrames = Frames();
        var restored = restoredFrames[0].Layers[0];
        Assert.True(UndoJournal.TryLoad(path, restoredFrames, out int steps));
        Assert.Equal(2, steps);
        Assert.True(restored.History.CanRedo);
        Assert.Equal(Blue, restored.History.Redo(afterRed)[0 * 4 + 2]);
    }

    [Fact]
    public void Load_WithDifferentSize_IsRejectedWithoutTouchingHistory()
    {
        using var temp = new TempDir();
        var frames = Frames();
        Stroke(frames[0].Layers[0], 1, 0, Red);
        string path = temp.File("doc.undo");
        UndoJournal.Save(path, frames);

        var target = Frames(width: 6);
        Assert.False(UndoJournal.TryLoad(path, target, out int steps));
        Assert.Equal(0, steps);
        Assert.False(target[0].Layers[0].History.CanUndo);
    }

    [Fact]
    public void Load_WithDifferentFrameOrLayerCount_IsRejected()
    {
        using var temp = new TempDir();
        var frames = Frames(frameCount: 2, layerCount: 2);
        Stroke(frames[0].Layers[0], 1, 0, Red);
        string path = temp.File("doc.undo");
        UndoJournal.Save(path, frames);

        Assert.False(UndoJournal.TryLoad(path, Frames(frameCount: 1, layerCount: 2), out _));
        Assert.False(UndoJournal.TryLoad(path, Frames(frameCount: 2, layerCount: 1), out _));
        Assert.True(UndoJournal.TryLoad(path, Frames(frameCount: 2, layerCount: 2), out int steps));
        Assert.Equal(1, steps);
    }

    [Fact]
    public void Load_CorruptOrMissingFile_IsRejected()
    {
        using var temp = new TempDir();
        string corrupt = temp.File("bad.undo");
        File.WriteAllText(corrupt, "not a journal");

        Assert.False(UndoJournal.TryLoad(corrupt, Frames(), out _));
        Assert.False(UndoJournal.TryLoad(temp.File("missing.undo"), Frames(), out _));
    }

    [Fact]
    public void Save_KeepsOnlyNewestEntries()
    {
        using var temp = new TempDir();
        var frames = Frames(4, 8);
        var layer = frames[0].Layers[0];
        for (int i = 0; i < 30; i++) Stroke(layer, i % 4, i / 4, i + 1);
        string path = temp.File("doc.undo");

        UndoJournal.Save(path, frames);

        var restoredFrames = Frames(4, 8);
        Assert.True(UndoJournal.TryLoad(path, restoredFrames, out int steps));
        Assert.Equal(UndoJournal.MaxUndoEntries, steps);
        Assert.Equal(UndoJournal.MaxUndoEntries, restoredFrames[0].Layers[0].History.UndoEntries().Length);
    }

    [Fact]
    public void LoadedHistory_KeepsSeqOrderingAgainstNewActions()
    {
        using var temp = new TempDir();
        var frames = Frames();
        var layer = frames[0].Layers[0];
        Stroke(layer, 1, 0, Red);
        Stroke(layer, 2, 0, Blue);
        string path = temp.File("doc.undo");
        UndoJournal.Save(path, frames);

        var restoredFrames = Frames();
        var restored = restoredFrames[0].Layers[0];
        Assert.True(UndoJournal.TryLoad(path, restoredFrames, out _));
        long loadedTop = restored.History.UndoSeq;

        // 恢复后新画的笔画序号必须大于磁盘里带回的序号，否则撤销会跳步。
        Stroke(restored, 3, 0, Red);
        Assert.True(restored.History.UndoSeq > loadedTop);

        var current = restored.Grid.Snapshot();
        var undone = restored.History.Undo(current);
        // 撤销刚画的那一笔：回到落盘历史之上的空白画布（磁盘里带回的两步仍在后面）
        Assert.All(undone, pixel => Assert.Equal(Argb.Transparent, pixel));
        Assert.True(restored.History.CanUndo);
    }
}
