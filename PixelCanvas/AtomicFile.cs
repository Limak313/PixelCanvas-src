using System.Text;

namespace PixelCanvas;

/// <summary>
/// 原子写文件：先写同目录临时文件，落盘后再整体替换目标。
/// 任何一步失败都只留下可清理的临时文件，目标文件保持原样（要么旧内容，要么新内容，不会出现半截）。
/// </summary>
internal static class AtomicFile
{
    /// <param name="path">目标路径；临时文件写在同目录，保证替换在同卷内完成。</param>
    /// <param name="write">写内容回调（负责把数据写入流）。</param>
    /// <param name="overwrite">目标已存在时是否替换；否则抛 <see cref="IOException"/> 且不改动目标。</param>
    /// <param name="flushToDisk">是否把数据刷到物理磁盘（工程等不可再生数据应开启）。</param>
    internal static void Write(string path, Action<Stream> write, bool overwrite = true, bool flushToDisk = true)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("目标路径不能为空。", nameof(path));
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                if (flushToDisk) stream.Flush(true);
            }
            File.Move(tmp, path, overwrite);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    internal static void WriteText(string path, string text, bool overwrite = true) =>
        Write(path, stream =>
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: true);
            writer.Write(text);
        }, overwrite);
}
