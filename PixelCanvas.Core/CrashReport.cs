using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PixelCanvas;

/// <summary>崩溃报告的数据形状；只包含诊断所需字段，不采集工程内容、用户名或文件路径列表。</summary>
internal sealed record CrashReport(
    string Id,
    DateTimeOffset TimestampUtc,
    string AppVersion,
    string RuntimeVersion,
    string OsVersion,
    string Architecture,
    string Source,
    bool Terminating,
    string ExceptionText,
    long WorkingSetBytes,
    long UptimeMilliseconds)
{
    internal const int MaxExceptionChars = 96 * 1024;

    internal static CrashReport Create(Exception exception, string source, bool terminating, string? appVersion = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string id = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}"[..30];
        string text = exception.ToString();
        if (text.Length > MaxExceptionChars) text = text[..MaxExceptionChars] + "\n…（异常文本已截断）";
        return new CrashReport(
            id,
            DateTimeOffset.UtcNow,
            appVersion ?? ProductInfo.CurrentVersion,
            RuntimeInformation.FrameworkDescription,
            Environment.OSVersion.VersionString,
            RuntimeInformation.ProcessArchitecture.ToString(),
            source,
            terminating,
            text,
            SafeWorkingSet(),
            Environment.TickCount64);
    }

    private static long SafeWorkingSet()
    {
        try { return Environment.WorkingSet; }
        catch { return 0; }
    }

    internal string ToJson()
    {
        return JsonSerializer.Serialize(new
        {
            format = "pixelcanvas-crash",
            version = 1,
            id = Id,
            timestampUtc = TimestampUtc,
            appVersion = AppVersion,
            runtimeVersion = RuntimeVersion,
            osVersion = OsVersion,
            architecture = Architecture,
            source = Source,
            terminating = Terminating,
            workingSetBytes = WorkingSetBytes,
            uptimeMilliseconds = UptimeMilliseconds,
            exception = ExceptionText,
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    internal string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine("PixelCanvas 崩溃报告");
        text.AppendLine("==================");
        text.AppendLine($"时间（UTC）：{TimestampUtc:O}");
        text.AppendLine($"报告 ID：{Id}");
        text.AppendLine($"程序版本：{AppVersion}");
        text.AppendLine($"运行时：{RuntimeVersion}");
        text.AppendLine($"操作系统：{OsVersion}");
        text.AppendLine($"进程架构：{Architecture}");
        text.AppendLine($"捕获来源：{Source}");
        text.AppendLine($"是否终止：{(Terminating ? "是" : "否")}");
        text.AppendLine($"运行时长：{TimeSpan.FromMilliseconds(UptimeMilliseconds):c}");
        text.AppendLine($"工作集：{WorkingSetBytes / 1024 / 1024} MiB");
        text.AppendLine();
        text.AppendLine("异常详情");
        text.AppendLine("----------");
        text.AppendLine(ExceptionText);
        return text.ToString();
    }
}

internal sealed record CrashReportPaths(string JsonPath, string TextPath);

/// <summary>崩溃报告落盘：同目录写 JSON（供上报）与 TXT（供用户直接查看）。</summary>
internal static class CrashReportStore
{
    internal static CrashReportPaths Save(string root, CrashReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("崩溃报告目录不能为空。", nameof(root));
        Directory.CreateDirectory(root);
        string name = $"crash-{report.TimestampUtc:yyyyMMdd-HHmmss}-{report.Id}";
        string jsonPath = Path.Combine(root, name + ".json");
        string textPath = Path.Combine(root, name + ".txt");
        AtomicFile.WriteText(jsonPath, report.ToJson());
        AtomicFile.WriteText(textPath, report.ToText());
        return new CrashReportPaths(jsonPath, textPath);
    }
}

/// <summary>
/// 可选联网上报。默认关闭；只有显式设置 PIXELCANVAS_CRASH_ENDPOINT 才启用。
/// 允许 HTTPS，或本机回环地址的 HTTP（便于本地接收端调试）。
/// </summary>
internal static class CrashReportUpload
{
    internal const string EndpointEnv = "PIXELCANVAS_CRASH_ENDPOINT";
    internal const string TokenEnv = "PIXELCANVAS_CRASH_TOKEN";

    internal static Uri? EndpointFromEnvironment()
    {
        string? text = Environment.GetEnvironmentVariable(EndpointEnv);
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme == Uri.UriSchemeHttps) return uri;
        if (uri.Scheme == Uri.UriSchemeHttp && IsLoopback(uri)) return uri;
        return null;
    }

    private static bool IsLoopback(Uri uri) =>
        string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal)
        || string.Equals(uri.Host, "::1", StringComparison.Ordinal);

    internal static async Task<bool> UploadAsync(
        HttpClient http,
        Uri endpoint,
        CrashReport report,
        string? token = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(report);
        if (!(endpoint.Scheme == Uri.UriSchemeHttps || (endpoint.Scheme == Uri.UriSchemeHttp && IsLoopback(endpoint))))
            throw new ArgumentException("崩溃上报只允许 HTTPS 或本机回环 HTTP。", nameof(endpoint));

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd($"PixelCanvas-CrashReporter/{ProductInfo.CurrentVersion}");
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(report.ToJson(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }
}
