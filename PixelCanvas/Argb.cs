namespace PixelCanvas;

/// <summary>ARGB 整数颜色工具：0 = 透明，非零 = 0xAARRGGBB（本程序只使用 FF / 00 两种 alpha）。</summary>
public static class Argb
{
    public const int Transparent = 0;

    /// <summary>由 24 位 RGB 生成不透明颜色。</summary>
    public static int FromRgb(int rgb) => unchecked((int)(0xFF000000u | ((uint)rgb & 0xFFFFFFu)));

    /// <summary>由 6 位十六进制 RGB 字符串生成不透明颜色。</summary>
    public static int FromRgbHex(string hex6) => FromRgb(Convert.ToInt32(hex6, 16));

    /// <summary>解析 8 位 ARGB 十六进制字符串。</summary>
    public static int FromArgbHex(string hex8) => unchecked((int)Convert.ToUInt32(hex8, 16));

    /// <summary>输出 6 位大写 RGB 十六进制（不含 #）。</summary>
    public static string ToRgbHex(int argb) => ((uint)argb & 0xFFFFFFu).ToString("X6");

    /// <summary>输出 8 位大写 ARGB 十六进制。</summary>
    public static string ToArgbHex(int argb) => ((uint)argb).ToString("X8");

    /// <summary>alpha 是否为 0（透明）。</summary>
    public static bool IsTransparent(int argb) => ((uint)argb >> 24) == 0;

    /// <summary>取 alpha 分量（0–255）。</summary>
    public static int AlphaOf(int argb) => (int)(((uint)argb >> 24) & 0xFF);
}
