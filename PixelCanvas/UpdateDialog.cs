using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace PixelCanvas;

/// <summary>发现新版本时的轻量提示：展示版本、说明与安装入口，不在此处执行下载。</summary>
internal sealed class UpdateDialog : Form
{
    private readonly CheckBox _autoCheck;

    internal bool AutoCheck => _autoCheck.Checked;

    internal UpdateDialog(UpdateInfo info, string currentVersion, bool autoCheck)
    {
        Text = "软件更新";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(640, 480);
        BackColor = Color.FromArgb(31, 35, 43);
        ForeColor = Color.FromArgb(228, 234, 241);
        Font = new Font("Microsoft YaHei UI", 9F);

        var header = new Panel { Dock = DockStyle.Top, Height = 96, Padding = new Padding(24, 16, 24, 0) };
        var title = new Label
        {
            Text = $"发现新版本 {info.Version}",
            Dock = DockStyle.Top, Height = 34,
            Font = new Font(Font.FontFamily, 15F, FontStyle.Bold),
            ForeColor = Color.FromArgb(136, 207, 245),
        };
        var subtitle = new Label
        {
            Text = $"当前版本 {currentVersion}  ·  {info.Installer.Name}  ·  {FormatSize(info.Installer.Size)}",
            Dock = DockStyle.Top, Height = 24,
            ForeColor = Color.FromArgb(171, 185, 202),
        };
        header.Controls.Add(subtitle);
        header.Controls.Add(title);

        var notesLabel = new Label
        {
            Text = "更新说明", Dock = DockStyle.Top, Height = 26,
            Padding = new Padding(24, 4, 0, 0),
            ForeColor = Color.FromArgb(205, 215, 228),
        };
        var notes = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            BackColor = Color.FromArgb(25, 28, 34),
            ForeColor = Color.FromArgb(224, 229, 237),
            BorderStyle = BorderStyle.FixedSingle,
            Text = string.IsNullOrWhiteSpace(info.Notes) ? "（该版本没有发布说明）" : info.Notes.Trim(),
            Margin = new Padding(24, 0, 24, 0),
        };
        var notesPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 0, 24, 8) };
        notesPanel.Controls.Add(notes);
        notesPanel.Controls.Add(notesLabel);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 112, Padding = new Padding(24, 8, 24, 16) };
        _autoCheck = new CheckBox
        {
            Text = "启动时自动检查更新",
            Checked = autoCheck,
            AutoSize = true,
            Location = new Point(24, 10),
            ForeColor = Color.FromArgb(205, 215, 228),
        };
        var releaseLink = new LinkLabel
        {
            Text = "查看 Release 页面",
            AutoSize = true,
            Location = new Point(24, 40),
            LinkColor = Color.FromArgb(129, 201, 239),
            ActiveLinkColor = Color.FromArgb(255, 255, 255),
            VisitedLinkColor = Color.FromArgb(129, 201, 239),
        };
        releaseLink.LinkClicked += (_, _) => OpenReleasePage(info.ReleasePage);
        var crashLink = new LinkLabel
        {
            Text = "打开崩溃报告目录",
            AutoSize = true,
            Location = new Point(190, 40),
            LinkColor = Color.FromArgb(171, 185, 202),
            ActiveLinkColor = Color.FromArgb(255, 255, 255),
            VisitedLinkColor = Color.FromArgb(171, 185, 202),
        };
        crashLink.LinkClicked += (_, _) => CrashHandler.OpenCrashReportFolder();

        var install = MakeButton("下载并安装", 116);
        install.Location = new Point(ClientSize.Width - 24 - install.Width, 74);
        install.DialogResult = DialogResult.OK;
        var later = MakeButton("稍后", 80);
        later.Location = new Point(install.Left - 10 - later.Width, 74);
        later.DialogResult = DialogResult.Cancel;
        footer.Controls.AddRange(new Control[] { _autoCheck, releaseLink, crashLink, later, install });

        Controls.Add(notesPanel);
        Controls.Add(footer);
        Controls.Add(header);
        AcceptButton = install;
        CancelButton = later;
    }

    private Button MakeButton(string text, int width)
    {
        var button = new Button
        {
            Text = text, Width = width, Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(49, 55, 66),
            ForeColor = Color.FromArgb(235, 239, 245),
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(72, 82, 98);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(58, 66, 80);
        return button;
    }

    private static void OpenReleasePage(Uri uri)
    {
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show("无法打开浏览器：" + ex.Message, "PixelCanvas", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "大小未知";
        if (bytes >= 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MiB";
        return $"{Math.Max(1, bytes / 1024)} KiB";
    }
}
