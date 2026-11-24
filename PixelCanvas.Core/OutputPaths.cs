namespace PixelCanvas;

internal static class OutputPaths
{
    internal static string DirectoryPath(string extension, bool animation = false)
    {
        string path = AppPaths.OutputRoot;
        string subfolder = extension.Equals(".pxproj", StringComparison.OrdinalIgnoreCase) ? "工程"
            : animation ? "动画" : extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ? "PNG" : "工程";
        string directory = Path.Combine(path, subfolder);
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static string NewFile(string extension, bool animation = false) => Path.Combine(DirectoryPath(extension, animation),
        $"PixelCanvas-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}" + extension);
}
