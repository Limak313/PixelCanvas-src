using System;
using System.Windows.Forms;

namespace PixelCanvas;

internal static class Program
{
    /// <summary>程序入口：启用 PerMonitorV2 高 DPI 后启动主窗口。</summary>
    [STAThread]
    private static void Main(string[] args)
    {
        int width = 32, height = 32;
        if (args.Length != 0 && (args.Length != 2 || !int.TryParse(args[0], out width) || !int.TryParse(args[1], out height) || width is < 4 or > 512 || height is < 4 or > 512))
        {
            MessageBox.Show("画布宽高必须是 4 至 512 之间的整数。", "PixelCanvas", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(width, height));
    }
}
