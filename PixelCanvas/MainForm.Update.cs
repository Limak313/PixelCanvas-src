using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Forms;

namespace PixelCanvas;

/// <summary>主窗口的自动更新交互：后台检查、更新提示、下载校验与安装切换。</summary>
public sealed partial class MainForm
{
    private readonly UpdateService _updateService = new();
    private UpdatePreferences _updatePreferences = new();
    private bool _updateCheckRunning;
    private bool _closingForUpdate;
    private LinkLabel _updateLink = null!;

    private const string StatusHintDefault = "滚轮缩放  /  中键平移";

    private async Task CheckForUpdatesOnStartupAsync()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("PIXELCANVAS_DISABLE_UPDATE_CHECK"), "1", StringComparison.Ordinal))
            return;
        if (!_updatePreferences.AutoCheck) return;
        if (_updatePreferences.LastCheckUtc is { } last)
        {
            var elapsed = DateTimeOffset.UtcNow - last;
            if (elapsed < TimeSpan.FromHours(24)) return;
        }
        await CheckForUpdatesAsync(manual: false);
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateCheckRunning)
        {
            if (manual) MessageBox.Show(this, "正在检查更新，请稍候。", "软件更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _updateCheckRunning = true;
        SetUpdateLink("检查中…");
        try
        {
            UpdateInfo? info = await _updateService.CheckAsync();
            MarkUpdateChecked();
            if (IsDisposed || Disposing) return;
            if (info is null)
            {
                if (manual)
                    MessageBox.Show(this, $"当前已是最新版本（{_updateService.CurrentVersion}）。", "软件更新",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var dialog = new UpdateDialog(info, _updateService.CurrentVersion, _updatePreferences.AutoCheck);
            var choice = dialog.ShowDialog(this);
            if (dialog.AutoCheck != _updatePreferences.AutoCheck)
            {
                _updatePreferences = _updatePreferences with { AutoCheck = dialog.AutoCheck };
                SaveUpdatePreferences();
            }
            if (choice == DialogResult.OK) await DownloadAndInstallAsync(info);
        }
        catch (UpdateException ex)
        {
            if (!IsDisposed && !Disposing && manual) MessageBox.Show(this, ex.Message, "软件更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException)
        {
            if (!IsDisposed && !Disposing && manual) MessageBox.Show(this, "检查更新失败：" + ex.Message, "软件更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _updateCheckRunning = false;
            SetUpdateLink("检查更新");
        }
    }

    private async Task DownloadAndInstallAsync(UpdateInfo info)
    {
        string installerPath;
        try
        {
            var progress = new Progress<int>(percent =>
            {
                if (!IsDisposed && !Disposing && _statusHint is not null)
                    _statusHint.Text = percent >= 100 ? "更新下载完成，正在校验…" : $"正在下载更新 {percent}%";
            });
            installerPath = await _updateService.DownloadAsync(info.Installer, AppPaths.UpdateRoot, progress);
            if (IsDisposed || Disposing) return;
            UpdateInstaller.VerifySigner(installerPath);
        }
        catch (UpdateException ex)
        {
            if (!IsDisposed && !Disposing)
                MessageBox.Show(this, ex.Message, "软件更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException)
        {
            if (!IsDisposed && !Disposing)
                MessageBox.Show(this, "下载更新失败：" + ex.Message, "软件更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        finally
        {
            if (!IsDisposed && !Disposing && _statusHint is not null) _statusHint.Text = StatusHintDefault;
        }

        if (IsDisposed || Disposing) return;
        var confirm = MessageBox.Show(this,
            $"安装包已下载并通过 SHA-256 校验：\n{installerPath}\n\n安装会关闭 PixelCanvas，随后静默运行安装器。\n是否立即安装？",
            "安装更新", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;
        if (!PrepareForUpdateInstall()) return;

        try
        {
            UpdateInstaller.LaunchInstaller(installerPath);
            _closingForUpdate = true;
            Close();
        }
        catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, "无法启动更新安装器：" + ex.Message + "\n\n可手动运行：\n" + installerPath,
                "软件更新", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>安装前保存工程；用户选择不保存时至少留下恢复副本。失败则取消安装。</summary>
    private bool PrepareForUpdateInstall()
    {
        if (_playing) ToggleAnimation();
        if (_dirty)
        {
            var choice = MessageBox.Show(this,
                "当前工程有未保存的修改。安装更新会关闭 PixelCanvas，是否先保存？",
                "安装更新", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (choice == DialogResult.Cancel) return false;
            if (choice == DialogResult.Yes)
            {
                if (!EnsureProjectPath()) return false;
                if (!TryWriteProject(_projectPath!)) return false;
                _dirty = false;
            }
        }
        if (!SaveRecovery())
        {
            MessageBox.Show(this, "无法写入自动恢复副本，已取消安装更新。", "安装更新",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        return true;
    }

    private void MarkUpdateChecked()
    {
        _updatePreferences = _updatePreferences with { LastCheckUtc = DateTimeOffset.UtcNow };
        SaveUpdatePreferences();
    }

    private void SaveUpdatePreferences()
    {
        try { UpdatePreferencesFile.Save(AppPaths.LocalAppDataRoot, _updatePreferences); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private void SetUpdateLink(string text)
    {
        if (_updateLink is not null && !_updateLink.IsDisposed) _updateLink.Text = text;
    }

    /// <summary>更新安装退出：保留恢复副本，只释放锁/timer，避免 FormClosing 再次询问保存。</summary>
    private void ReleaseRecoveryLockOnly()
    {
        _autosaveTimer.Stop();
        _animationTimer.Stop();
        _recoveryLock?.Dispose();
        _recoveryLock = null;
        try { File.Delete(Path.ChangeExtension(RecoveryPath, ".lock")); } catch { }
    }
}
