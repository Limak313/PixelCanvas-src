using System.IO.Compression;
using System.Text;

namespace PixelCanvas;

/// <summary>
/// 撤销历史的磁盘副本：跟随自动恢复副本一起写入，进程异常退出后恢复作品时把像素撤销栈一并接回。
/// 只持久化图层像素历史；帧 / 图层结构快照不落盘 —— 它们可能引用已删除的图层，体积也不可控。
/// 文件为 gzip 压缩的 RLE 行编码；恢复时若与工程对不上（尺寸、帧数、图层数）就整份丢弃，不影响载入。
/// </summary>
internal static class UndoJournal
{
    /// <summary>每层最多保留的撤销步数（比内存上限更保守，避免恢复文件过大）。</summary>
    internal const int MaxUndoEntries = 20;

    /// <summary>每层最多保留的重做步数。</summary>
    internal const int MaxRedoEntries = 20;

    internal const long MaxFileBytes = 128L * 1024 * 1024;
    private const string Magic = "PCUJ";
    private const byte FormatVersion = 1;

    internal static void Save(string path, IReadOnlyList<AnimationFrame> frames)
    {
        var grid = frames[0].Layers[0].Grid;
        AtomicFile.Write(path, stream =>
        {
            using var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true);
            using var writer = new BinaryWriter(gzip, Encoding.UTF8, leaveOpen: true);
            writer.Write(Encoding.ASCII.GetBytes(Magic));
            writer.Write(FormatVersion);
            writer.Write(grid.Width);
            writer.Write(grid.Height);
            writer.Write(frames.Count);
            foreach (var frame in frames)
            {
                writer.Write(frame.Layers.Count);
                foreach (var layer in frame.Layers)
                {
                    WriteStack(writer, layer.History.UndoEntries(), MaxUndoEntries, grid.Width, grid.Height);
                    WriteStack(writer, layer.History.RedoEntries(), MaxRedoEntries, grid.Width, grid.Height);
                }
            }
        }, overwrite: true, flushToDisk: false);
    }

    /// <summary>
    /// 尝试把撤销栈套用到给定文档；结构与文档不匹配、文件损坏或读取失败时返回 false 且不修改任何图层。
    /// </summary>
    internal static bool TryLoad(string path, IReadOnlyList<AnimationFrame> frames, out int restoredSteps)
    {
        restoredSteps = 0;
        try
        {
            if (!File.Exists(path)) return false;
            if (frames.Count == 0 || frames[0].Layers.Count == 0) return false;
            var grid = frames[0].Layers[0].Grid;

            var plan = new List<(Layer Layer, List<(int[], long)> Undo, List<(int[], long)> Redo)>();
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length > MaxFileBytes) return false;
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                using var reader = new BinaryReader(gzip, Encoding.UTF8);
                if (Encoding.ASCII.GetString(reader.ReadBytes(Magic.Length)) != Magic || reader.ReadByte() != FormatVersion) return false;
                int width = reader.ReadInt32(), height = reader.ReadInt32(), frameCount = reader.ReadInt32();
                if (width != grid.Width || height != grid.Height || frameCount != frames.Count) return false;
                foreach (var frame in frames)
                {
                    int layerCount = reader.ReadInt32();
                    if (layerCount != frame.Layers.Count) return false;
                    foreach (var layer in frame.Layers)
                    {
                        var undo = ReadStack(reader, MaxUndoEntries, width, height);
                        if (undo is null) return false;
                        var redo = ReadStack(reader, MaxRedoEntries, width, height);
                        if (redo is null) return false;
                        plan.Add((layer, undo, redo));
                    }
                }
            }

            foreach (var item in plan)
            {
                item.Layer.History.Restore(item.Undo, item.Redo);
                restoredSteps += item.Undo.Count + item.Redo.Count;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException
            or ArgumentException or OutOfMemoryException or FormatException)
        {
            restoredSteps = 0;
            return false;
        }
    }

    private static void WriteStack(BinaryWriter writer, (int[] Pixels, long Seq)[] entries, int max, int width, int height)
    {
        int count = Math.Min(entries.Length, max);
        writer.Write(count);
        for (int i = entries.Length - count; i < entries.Length; i++)
        {
            writer.Write(entries[i].Seq);
            foreach (string row in ProjectFile.EncodeRows(entries[i].Pixels, width, height)) writer.Write(row);
        }
    }

    private static List<(int[], long)>? ReadStack(BinaryReader reader, int max, int width, int height)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > max) return null;
        var entries = new List<(int[], long)>(count);
        for (int i = 0; i < count; i++)
        {
            long seq = reader.ReadInt64();
            var rows = new string[height];
            for (int y = 0; y < height; y++) rows[y] = reader.ReadString();
            var pixels = DecodeRows(rows, width, height);
            if (pixels is null) return null;
            entries.Add((pixels, seq));
        }
        return entries;
    }

    private static int[]? DecodeRows(string[] rows, int width, int height)
    {
        if (rows.Length != height) return null;
        var grid = new PixelGrid(width, height);
        for (int y = 0; y < height; y++) if (!ProjectFile.ParseRow(rows[y], width, grid, y)) return null;
        return grid.Snapshot();
    }
}
