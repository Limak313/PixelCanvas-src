namespace PixelCanvas;

/// <summary>
/// 把全部帧按顺序导出为 PNG 序列（frame-001.png、frame-002.png…），供游戏引擎或逐帧编辑使用。
/// 只写入一个空目录或新目录，不覆盖已有文件。
/// </summary>
internal static class FrameSequenceExport
{
    internal sealed record Result(string Directory, int FrameCount, int Width, int Height, int Scale, string[] Files)
    {
        internal object ToData() => new { directory = Directory, frameCount = FrameCount, width = Width, height = Height, scale = Scale, files = Files };
    }

    internal static Result Save(string directory, string baseName, IReadOnlyList<AnimationFrame> frames, int scale = 1)
    {
        if (frames.Count is < 1 or > ProjectFile.MaxFrames) throw new ArgumentException($"帧数必须为 1–{ProjectFile.MaxFrames}。");
        if (scale is < 1 or > 16) throw new ArgumentException("放大倍数必须为 1–16。");
        if (string.IsNullOrWhiteSpace(baseName) || baseName.Length > 64 || baseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("文件基名不能为空、不能超过 64 字符，也不能包含路径分隔符或非法字符。");
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("输出目录必须是绝对路径。");

        var grid = frames[0].Layers[0].Grid;
        if (frames.Any(frame => frame.Layers.Count == 0 || frame.Layers.Any(layer => layer.Grid.Width != grid.Width || layer.Grid.Height != grid.Height)))
            throw new ArgumentException("动画帧尺寸不一致。");
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException("输出目录已存在且不为空，请换一个新目录。");

        Directory.CreateDirectory(directory);
        var files = new string[frames.Count];
        int digits = Math.Max(3, frames.Count.ToString().Length);
        for (int i = 0; i < frames.Count; i++)
        {
            string path = Path.Combine(directory, $"{baseName}-{(i + 1).ToString().PadLeft(digits, '0')}.png");
            if (File.Exists(path)) throw new IOException($"目标文件已存在：{Path.GetFileName(path)}");
            PngExport.Save(path, FrameComposite.Flatten(frames[i], grid.Width, grid.Height), grid.Width, grid.Height, scale);
            files[i] = path;
        }
        return new Result(directory, frames.Count, grid.Width, grid.Height, scale, files);
    }
}
