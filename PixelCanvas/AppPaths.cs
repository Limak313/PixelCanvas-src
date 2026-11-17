namespace PixelCanvas;

/// <summary>应用目录与环境变量的单一来源：默认输出目录、预设目录、自动恢复目录。</summary>
internal static class AppPaths
{
    public const string OutputDirEnv = "PIXELCANVAS_OUTPUT_DIR";
    public const string PresetDirEnv = "PIXELCANVAS_PRESET_DIR";
    public const string ProductName = "PixelCanvas";

    /// <summary>本地应用数据根目录（LocalAppData\PixelCanvas）。</summary>
    public static string LocalAppDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName);

    /// <summary>工程/图片默认输出根目录：环境变量 &gt; 工作区 output &gt; 文档 output。</summary>
    public static string OutputRoot
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable(OutputDirEnv);
            if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured)) return configured;
            if (Directory.Exists(@"D:\像素画布")) return @"D:\像素画布\output";
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), ProductName, "output");
        }
    }

    /// <summary>导出预设保存目录：环境变量 &gt; 本地应用数据目录。</summary>
    public static string PresetRoot
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable(PresetDirEnv);
            return !string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured)
                ? configured : LocalAppDataRoot;
        }
    }

    /// <summary>自动恢复副本目录。</summary>
    public static string RecoveryRoot => Path.Combine(LocalAppDataRoot, "Recovery");
}
