using System.Net.Http;
using System.Threading;
using System.Windows.Forms;

namespace PixelCanvas;

/// <summary>
/// 全局崩溃捕获：落盘本地报告，并在显式配置端点时尝试上报。
/// UI 线程异常会提示用户并退出，避免在损坏状态继续编辑；后台任务异常只记录，不打断编辑。
/// </summary>
internal static class CrashHandler
{
    private static int _handling;
    private static readonly HttpClient UploadClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    internal static void Install()
    {
        Application.ThreadException += (_, e) => Handle(e.Exception, "Application.ThreadException", terminate: true);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
                Handle(exception, "AppDomain.UnhandledException", terminate: e.IsTerminating);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Handle(e.Exception, "TaskScheduler.UnobservedTaskException", terminate: false, showDialog: false);
            e.SetObserved();
        };
    }

    /// <summary>处理异常并返回本次生成的报告路径；重复进入时返回 null。</summary>
    internal static CrashReportPaths? Handle(Exception exception, string source, bool terminate, bool showDialog = true)
    {
        if (Interlocked.Exchange(ref _handling, 1) != 0) return null;

        CrashReport report = CrashReport.Create(exception, source, terminate);
        CrashReportPaths? paths = null;
        string failure = "";
        try
        {
            paths = CrashReportStore.Save(AppPaths.CrashReportRoot, report);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            failure = ex.Message;
        }

        TryUpload(report);

        if (showDialog)
        {
            string message = BuildMessage(exception, paths, failure);
            try
            {
                var result = MessageBox.Show(message, "PixelCanvas 遇到错误", MessageBoxButtons.YesNo, MessageBoxIcon.Error);
                if (result == DialogResult.Yes) OpenCrashReportFolder();
            }
            catch { /* 崩溃处理里不再制造新的崩溃 */ }
        }

        if (terminate) Environment.Exit(1);
        return paths;
    }

    private static string BuildMessage(Exception exception, CrashReportPaths? paths, string failure)
    {
        string head = $"PixelCanvas 遇到错误：{exception.GetType().Name}\n{exception.Message}";
        if (paths is not null)
            return head + $"\n\n崩溃报告已保存：\n{paths.TextPath}\n\n是否打开报告所在目录？";
        return head + $"\n\n无法写入崩溃报告：{failure}\n\n是否打开崩溃报告目录？";
    }

    private static void TryUpload(CrashReport report)
    {
        Uri? endpoint = CrashReportUpload.EndpointFromEnvironment();
        if (endpoint is null) return;
        try
        {
            string? token = Environment.GetEnvironmentVariable(CrashReportUpload.TokenEnv);
            var task = Task.Run(() => CrashReportUpload.UploadAsync(UploadClient, endpoint, report, token));
            task.Wait(TimeSpan.FromSeconds(4));
        }
        catch { /* 上报失败不影响本地报告与退出流程 */ }
    }

    internal static void OpenCrashReportFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.CrashReportRoot);
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.CrashReportRoot,
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(start);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show("无法打开崩溃报告目录：" + ex.Message, "PixelCanvas",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
