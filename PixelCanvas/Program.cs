using System;
using System.Windows.Forms;

namespace PixelCanvas;

internal static class Program
{
    /// <summary>程序入口：启用 PerMonitorV2 高 DPI 后启动主窗口。</summary>
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
