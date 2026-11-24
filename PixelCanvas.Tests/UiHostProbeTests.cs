using System.Drawing;
using System.Windows.Forms;
using Xunit;

namespace PixelCanvas.Tests;

/// <summary>
/// UI 宿主探针：只验证「当前会话能否创建 WinForms 窗体句柄」，不覆盖任何业务界面路径。
/// 故意不加 Category=InteractiveUI 标签，因此默认进入 CI 门禁——用来判断 windows runner 能否承载界面用例。
/// 本地单跑：--filter "Category=UiHost"
/// </summary>
[Trait("Category", "UiHost")]
public class UiHostProbeTests
{
    [Fact]
    public void StaThread_CanCreateMainFormHandle()
    {
        UiTestSupport.RunInSta(() =>
        {
            using var form = new MainForm(4, 4);
            form.CreateControl();
            var diag =
                $"UserInteractive={Environment.UserInteractive}, " +
                $"IsHandleCreated={form.IsHandleCreated}, " +
                $"Handle=0x{form.Handle.ToInt64():X}, " +
                $"ScreenCount={Screen.AllScreens.Length}, " +
                $"DeviceDpi={form.DeviceDpi}, " +
                $"SessionId={System.Diagnostics.Process.GetCurrentProcess().SessionId}";
            Assert.True(form.IsHandleCreated, $"窗体句柄创建失败。诊断：{diag}");
            Assert.NotEqual(IntPtr.Zero, form.Handle);
            // 屏幕数量、DPI、会话号只作诊断记录，不断言具体值：CI 与 150% 缩放桌面本就不需一致。
        });
    }

    [Fact]
    public void StaThread_CanPaintCanvasToBitmap()
    {
        UiTestSupport.RunInSta(() =>
        {
            using var control = new CanvasControl(new PixelGrid(4, 4));
            control.Size = new Size(32, 32);
            control.CreateControl();
            var diag =
                $"IsHandleCreated={control.IsHandleCreated}, " +
                $"Size={control.Width}x{control.Height}, " +
                $"DeviceDpi={control.DeviceDpi}";
            Assert.True(control.IsHandleCreated, $"画布控件句柄创建失败。诊断：{diag}");
            using var bitmap = new Bitmap(16, 16);
            control.DrawToBitmap(bitmap, new Rectangle(0, 0, 16, 16));
        });
    }
}
