using System.Text.Json;

namespace PixelCanvas;

internal sealed record ExportPreset(string Name, string Format, int Scale, int Fps, int Columns, string Directory);

internal static class ExportPresets
{
    private static string Folder()
    {
        string path = AppPaths.PresetRoot;
        System.IO.Directory.CreateDirectory(path); return path;
    }

    private static FileStream Lock(string folder) => new(Path.Combine(folder, "export-presets.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private static List<ExportPreset> Read(string folder)
    {
        string path = Path.Combine(folder, "export-presets.json");
        if (!File.Exists(path)) return new();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 262144) throw new ArgumentException("导出预设文件超过 256 KiB。");
        var result = JsonSerializer.Deserialize<List<ExportPreset>>(file) ?? throw new ArgumentException("导出预设格式不正确。");
        if (result.Count > 64) throw new ArgumentException("导出预设超过 64 项。");
        foreach (var item in result) Validate(item, false);
        if (result.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count) throw new ArgumentException("导出预设名称重复。");
        return result;
    }

    internal static void Validate(ExportPreset item, bool checkDirectory = true)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 60 || item.Name != item.Name.Trim()) throw new ArgumentException("预设名称须为 1–60 个字符且首尾不能有空格。");
        if (item.Format is not ("png" or "gif" or "sheet")) throw new ArgumentException("格式须为 png、gif 或 sheet。");
        if (item.Scale is < 1 or > 16 || item.Fps is < 1 or > ProjectFile.MaxFps || item.Columns is < 1 or > 64) throw new ArgumentException($"缩放须为 1–16、帧率 1–{ProjectFile.MaxFps}、列数 1–64。");
        if (item.Directory is null || (item.Directory.Length > 0 && (!Path.IsPathFullyQualified(item.Directory) || (checkDirectory && !System.IO.Directory.Exists(item.Directory))))) throw new ArgumentException("输出目录必须是现有文件夹的绝对路径，或留空使用默认目录。");
    }

    internal static List<ExportPreset> List()
    {
        string folder = Folder(); using var fileLock = Lock(folder); return Read(folder);
    }

    internal static void Put(ExportPreset item, bool overwrite)
    {
        Validate(item); string folder = Folder(); using var fileLock = Lock(folder); var items = Read(folder);
        int index = items.FindIndex(p => string.Equals(p.Name, item.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && !overwrite) throw new IOException("同名预设已存在。");
        if (index < 0) { if (items.Count >= 64) throw new ArgumentException("最多保存 64 个导出预设。"); items.Add(item); } else items[index] = item;
        Write(folder, items);
    }

    internal static void Delete(string name)
    {
        string folder = Folder(); using var fileLock = Lock(folder); var items = Read(folder);
        int index = items.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new ArgumentException("未找到该导出预设。");
        items.RemoveAt(index); Write(folder, items);
    }

    private static void Write(string folder, List<ExportPreset> items)
    {
        string target = Path.Combine(folder, "export-presets.json"), temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) JsonSerializer.Serialize(file, items); File.Move(temp, target, true); }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
    }

    internal static string NewOutputPath(ExportPreset preset)
    {
        Validate(preset);
        string extension = preset.Format == "gif" ? ".gif" : ".png";
        string folder = preset.Directory.Length == 0 ? OutputPaths.DirectoryPath(extension, preset.Format is "gif" or "sheet") : preset.Directory;
        return Path.Combine(folder, $"PixelCanvas-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}{extension}");
    }
}
