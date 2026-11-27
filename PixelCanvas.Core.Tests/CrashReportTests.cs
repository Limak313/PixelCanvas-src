using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace PixelCanvas.Tests;

public class CrashReportTests
{
    [Fact]
    public void Create_IncludesDiagnosticsButNotMachineIdentity()
    {
        var exception = new InvalidOperationException("外层失败", new ArgumentException("内层失败"));
        var report = CrashReport.Create(exception, "Application.ThreadException", terminating: true, appVersion: "1.0.47");
        Assert.Equal("1.0.47", report.AppVersion);
        Assert.Equal("Application.ThreadException", report.Source);
        Assert.True(report.Terminating);
        Assert.Contains("InvalidOperationException", report.ExceptionText);
        Assert.Contains("ArgumentException", report.ExceptionText);
        Assert.DoesNotContain(Environment.UserName, report.ExceptionText);
        Assert.DoesNotContain(Environment.MachineName, report.ExceptionText);
    }

    [Fact]
    public void Json_IsStructuredAndParseable()
    {
        var report = CrashReport.Create(new Exception("boom"), "AppDomain.UnhandledException", terminating: false);
        using var document = JsonDocument.Parse(report.ToJson());
        var root = document.RootElement;
        Assert.Equal("pixelcanvas-crash", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(report.Id, root.GetProperty("id").GetString());
        Assert.False(root.GetProperty("terminating").GetBoolean());
        Assert.Contains("boom", root.GetProperty("exception").GetString());
    }

    [Fact]
    public void Text_ContainsHeaderAndException()
    {
        var report = CrashReport.Create(new Exception("boom"), "TaskScheduler.UnobservedTaskException", terminating: false);
        string text = report.ToText();
        Assert.Contains("PixelCanvas 崩溃报告", text);
        Assert.Contains("TaskScheduler.UnobservedTaskException", text);
        Assert.Contains("boom", text);
        Assert.Contains(report.Id, text);
    }

    [Fact]
    public void Store_WritesJsonAndTextAtomically()
    {
        using var dir = new TempDir();
        var report = CrashReport.Create(new Exception("boom"), "Program.Main", terminating: true);
        var paths = CrashReportStore.Save(dir.Path, report);
        Assert.True(File.Exists(paths.JsonPath));
        Assert.True(File.Exists(paths.TextPath));
        Assert.Equal("pixelcanvas-crash", JsonDocument.Parse(File.ReadAllText(paths.JsonPath))
            .RootElement.GetProperty("format").GetString());
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    [Fact]
    public void Store_CreatesDirectory()
    {
        using var dir = new TempDir();
        var nested = Path.Combine(dir.Path, "CrashReports");
        var paths = CrashReportStore.Save(nested, CrashReport.Create(new Exception("x"), "test", false));
        Assert.True(Directory.Exists(nested));
        Assert.True(File.Exists(paths.JsonPath));
    }
}

public class CrashReportUploadTests
{
    [Fact]
    public void EndpointFromEnvironment_RejectsUnconfiguredAndNonLoopbackHttp()
    {
        string? oldEndpoint = Environment.GetEnvironmentVariable(CrashReportUpload.EndpointEnv);
        try
        {
            Environment.SetEnvironmentVariable(CrashReportUpload.EndpointEnv, "");
            Assert.Null(CrashReportUpload.EndpointFromEnvironment());
            Environment.SetEnvironmentVariable(CrashReportUpload.EndpointEnv, "http://example.com/crash");
            Assert.Null(CrashReportUpload.EndpointFromEnvironment());
            Environment.SetEnvironmentVariable(CrashReportUpload.EndpointEnv, "https://example.com/crash");
            Assert.Equal("https://example.com/crash", CrashReportUpload.EndpointFromEnvironment()?.ToString());
            Environment.SetEnvironmentVariable(CrashReportUpload.EndpointEnv, "http://127.0.0.1:8787/crash");
            Assert.NotNull(CrashReportUpload.EndpointFromEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable(CrashReportUpload.EndpointEnv, oldEndpoint);
        }
    }

    [Fact]
    public async Task UploadAsync_PostsJsonWithBearerToken()
    {
        using var dir = new TempDir();
        var report = CrashReport.Create(new Exception("boom"), "Application.ThreadException", true);
        var handler = new FakeHttpHandler();
        var endpoint = new Uri("https://crash.example.test/api/reports");
        handler.Map(endpoint.ToString(), _ => new HttpResponseMessage(HttpStatusCode.Accepted));
        using var client = new HttpClient(handler);
        bool ok = await CrashReportUpload.UploadAsync(client, endpoint, report, "secret-token");
        Assert.True(ok);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("Bearer secret-token", request.Authorization);
        using var document = JsonDocument.Parse(request.Body);
        Assert.Equal(report.Id, document.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task UploadAsync_ReturnsFalseOnServerError()
    {
        var report = CrashReport.Create(new Exception("boom"), "test", true);
        var endpoint = new Uri("https://crash.example.test/api/reports");
        var handler = new FakeHttpHandler();
        handler.Map(endpoint.ToString(), _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var client = new HttpClient(handler);
        Assert.False(await CrashReportUpload.UploadAsync(client, endpoint, report));
    }

    [Fact]
    public async Task UploadAsync_RejectsNonLoopbackHttp()
    {
        var report = CrashReport.Create(new Exception("boom"), "test", true);
        using var client = new HttpClient(new FakeHttpHandler());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            CrashReportUpload.UploadAsync(client, new Uri("http://example.test/crash"), report));
    }
}
