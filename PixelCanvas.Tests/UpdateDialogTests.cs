using System.Windows.Forms;
using Xunit;

namespace PixelCanvas.Tests;

public class UpdateDialogTests
{
    private static UpdateInfo Info() => new UpdateInfo(
        "1.0.47",
        "v1.0.47",
        new Uri("https://github.com/Limak313/PixelCanvas-src/releases/tag/v1.0.47"),
        "更新说明",
        new UpdateAsset(
            "PixelCanvas-1.0.47-win-x64-setup.exe",
            new Uri("https://example.test/PixelCanvas-1.0.47-win-x64-setup.exe"),
            new Uri("https://example.test/PixelCanvas-1.0.47-win-x64-setup.exe.sha256"),
            59538481));

    [Fact]
    public void Dialog_ReflectsAutoCheckAndOffersInstall()
    {
        using var dialog = new UpdateDialog(Info(), "1.0.46", autoCheck: false);
        dialog.CreateControl();
        Assert.False(dialog.AutoCheck);
        Assert.NotNull(FindButton(dialog, "下载并安装"));
        Assert.NotNull(FindButton(dialog, "稍后"));
    }

    [Fact]
    public void Dialog_CanPreserveAutoCheckEnabled()
    {
        using var dialog = new UpdateDialog(Info(), "1.0.46", autoCheck: true);
        dialog.CreateControl();
        Assert.True(dialog.AutoCheck);
    }

    private static Button? FindButton(Control parent, string text)
    {
        foreach (Control control in parent.Controls)
        {
            if (control is Button button && button.Text == text) return button;
            var nested = FindButton(control, text);
            if (nested is not null) return nested;
        }
        return null;
    }
}
