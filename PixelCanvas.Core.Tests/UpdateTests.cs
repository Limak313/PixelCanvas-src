using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace PixelCanvas.Tests;

public class UpdateVersionTests
{
    [Theory]
    [InlineData("v1.0.47", "1.0.47", "1.0.47.0")]
    [InlineData("1.0.47", "1.0.47", "1.0.47.0")]
    [InlineData("1.0.47.0", "1.0.47.0", "1.0.47.0")]
    [InlineData("V1.0.47+build.9", "1.0.47", "1.0.47.0")]
    [InlineData("v1.0.47-rc1", "1.0.47", "1.0.47.0")]
    public void Normalize_HandlesTagPrefixesAndSuffixes(string input, string display, string normalized)
    {
        Assert.Equal(display, UpdateVersion.Display(input));
        Assert.Equal(normalized, UpdateVersion.Normalize(input));
    }

    [Theory]
    [InlineData("v1.0.47", "1.0.46", true)]
    [InlineData("1.0.46", "1.0.46", false)]
    [InlineData("1.0.45", "1.0.46", false)]
    [InlineData("1.0.47.1", "1.0.47", true)]
    [InlineData("not-a-version", "1.0.46", false)]
    public void IsNewer_ComparesNumericComponents(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, UpdateVersion.IsNewer(candidate, current));
    }

    [Fact]
    public void InstallerName_PreservesThreeSegmentVersion()
    {
        Assert.Equal("PixelCanvas-1.0.47-win-x64-setup.exe",
            UpdateVersion.InstallerName("v1.0.47", "x64"));
        Assert.Equal("PixelCanvas-1.0.47-win-arm64-setup.exe",
            UpdateVersion.InstallerName("1.0.47", "arm64"));
    }
}

public class UpdateReleaseParserTests
{
    private const string Page = "https://github.com/Limak313/PixelCanvas-src/releases/tag/v1.0.47";

    private static string ReleaseJson(string tag = "v1.0.47", bool draft = false, bool prerelease = false,
        string assetVersion = "1.0.47", bool includeChecksum = true, string checksumUrl = "https://example.test/setup.sha256")
    {
        string setup = $"https://example.test/PixelCanvas-{assetVersion}-win-x64-setup.exe";
        string checksum = includeChecksum
            ? $"{{\"name\":\"PixelCanvas-{assetVersion}-win-x64-setup.exe.sha256\",\"browser_download_url\":\"{checksumUrl}\",\"size\":66}},"
            : "";
        return $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "{{Page}}",
          "body": "本次更新说明",
          "draft": {{draft.ToString().ToLowerInvariant()}},
          "prerelease": {{prerelease.ToString().ToLowerInvariant()}},
          "assets": [
            {"name":"PixelCanvas-{{assetVersion}}-win-x64-setup.exe","browser_download_url":"{{setup}}","size":123},
            {{checksum}}
            {"name":"PixelCanvas-{{assetVersion}}-win-arm64-setup.exe","browser_download_url":"https://example.test/arm64.exe","size":99},
            {"name":"PixelCanvas-{{assetVersion}}-win-arm64-setup.exe.sha256","browser_download_url":"https://example.test/arm64.sha256","size":66}
          ]
        }
        """;
    }

    [Fact]
    public void ParseLatest_SelectsCurrentArchitectureAndChecksum()
    {
        var info = UpdateReleaseParser.ParseLatest(ReleaseJson(), "1.0.46", "arm64");
        Assert.NotNull(info);
        Assert.Equal("1.0.47", info!.Version);
        Assert.Equal("v1.0.47", info.TagName);
        Assert.Equal("PixelCanvas-1.0.47-win-arm64-setup.exe", info.Installer.Name);
        Assert.Equal("https://example.test/arm64.sha256", info.Installer.ChecksumUrl.ToString());
        Assert.Equal(99, info.Installer.Size);
        Assert.Equal(Page, info.ReleasePage.ToString());
    }

    [Fact]
    public void ParseLatest_ReturnsNullWhenCurrentIsSameOrNewer()
    {
        Assert.Null(UpdateReleaseParser.ParseLatest(ReleaseJson(), "1.0.47", "x64"));
        Assert.Null(UpdateReleaseParser.ParseLatest(ReleaseJson(), "1.0.48", "x64"));
    }

    [Fact]
    public void ParseLatest_IgnoresDraftAndPrerelease()
    {
        Assert.Null(UpdateReleaseParser.ParseLatest(ReleaseJson(draft: true), "1.0.46", "x64"));
        Assert.Null(UpdateReleaseParser.ParseLatest(ReleaseJson(prerelease: true), "1.0.46", "x64"));
    }

    [Fact]
    public void ParseLatest_RejectsReleaseWithoutChecksum()
    {
        var error = Assert.Throws<UpdateException>(() =>
            UpdateReleaseParser.ParseLatest(ReleaseJson(includeChecksum: false), "1.0.46", "x64"));
        Assert.Contains("SHA-256", error.Message);
    }

    [Fact]
    public void ParseLatest_RejectsNonHttpsAssetUrl()
    {
        Assert.Throws<UpdateException>(() =>
            UpdateReleaseParser.ParseLatest(ReleaseJson(checksumUrl: "http://example.test/setup.sha256"), "1.0.46", "x64"));
    }

    [Fact]
    public void ParseLatest_RejectsAssetVersionMismatch()
    {
        // tag 是 1.0.47，但资产名仍是 1.0.46：不按旧包更新。
        Assert.Throws<UpdateException>(() =>
            UpdateReleaseParser.ParseLatest(ReleaseJson(assetVersion: "1.0.46"), "1.0.46", "x64"));
    }

    [Fact]
    public void ParseLatest_RejectsMalformedJsonAndMissingInstaller()
    {
        Assert.Throws<UpdateException>(() => UpdateReleaseParser.ParseLatest("{", "1.0.46", "x64"));
        Assert.Throws<UpdateException>(() =>
            UpdateReleaseParser.ParseLatest(ReleaseJson(assetVersion: "1.0.47").Replace("win-x64", "win-riscv"), "1.0.46", "x64"));
    }
}

public class UpdateServiceTests
{
    [Fact]
    public async Task CheckAsync_ParsesLatestRelease()
    {
        var handler = new FakeHttpHandler();
        string api = "https://api.github.com/repos/Limak313/PixelCanvas-src/releases/latest";
        handler.MapText(api, ReleaseForService());
        var service = new UpdateService(new HttpClient(handler), currentVersion: "1.0.46", architecture: "x64");
        var info = await service.CheckAsync();
        Assert.NotNull(info);
        Assert.Equal("1.0.47", info!.Version);
        Assert.Contains(handler.Requests, request => request.Url == api && request.Method == "GET");
    }

    [Fact]
    public async Task CheckAsync_ReturnsNullOn404()
    {
        var service = new UpdateService(new HttpClient(new FakeHttpHandler()), currentVersion: "1.0.46", architecture: "x64");
        Assert.Null(await service.CheckAsync());
    }

    [Fact]
    public async Task CheckAsync_ReportsRateLimit()
    {
        var handler = new FakeHttpHandler();
        string api = "https://api.github.com/repos/Limak313/PixelCanvas-src/releases/latest";
        handler.Map(api, _ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var service = new UpdateService(new HttpClient(handler), currentVersion: "1.0.46", architecture: "x64");
        var error = await Assert.ThrowsAsync<UpdateException>(() => service.CheckAsync());
        Assert.Contains("限制", error.Message);
    }

    [Fact]
    public async Task DownloadAsync_DownloadsAndVerifiesChecksum()
    {
        using var dir = new TempDir();
        byte[] installer = new byte[4096];
        RandomNumberGenerator.Fill(installer);
        string sha = Convert.ToHexString(SHA256.HashData(installer));
        string fileUrl = "https://example.test/PixelCanvas-1.0.47-win-x64-setup.exe";
        string shaUrl = fileUrl + ".sha256";
        var handler = new FakeHttpHandler().MapBytes(fileUrl, installer)
            .MapText(shaUrl, sha + "  PixelCanvas-1.0.47-win-x64-setup.exe\n");
        var service = new UpdateService(new HttpClient(handler), currentVersion: "1.0.46", architecture: "x64");
        var asset = new UpdateAsset("PixelCanvas-1.0.47-win-x64-setup.exe", new Uri(fileUrl), new Uri(shaUrl), installer.Length);
        var path = await service.DownloadAsync(asset, dir.Path);
        Assert.Equal(installer, File.ReadAllBytes(path));
        Assert.True(File.Exists(path + ".sha256"));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.part"));
    }

    [Fact]
    public async Task DownloadAsync_RejectsChecksumMismatchAndLeavesNoExe()
    {
        using var dir = new TempDir();
        byte[] installer = { 1, 2, 3, 4 };
        string fileUrl = "https://example.test/PixelCanvas-1.0.47-win-x64-setup.exe";
        var handler = new FakeHttpHandler().MapBytes(fileUrl, installer)
            .MapText(fileUrl + ".sha256", new string('0', 64));
        var service = new UpdateService(new HttpClient(handler), currentVersion: "1.0.46", architecture: "x64");
        var asset = new UpdateAsset("PixelCanvas-1.0.47-win-x64-setup.exe", new Uri(fileUrl), new Uri(fileUrl + ".sha256"), installer.Length);
        await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(asset, dir.Path));
        Assert.False(File.Exists(Path.Combine(dir.Path, asset.Name)));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.part"));
    }

    [Fact]
    public async Task DownloadAsync_RejectsBadChecksumFileFormat()
    {
        using var dir = new TempDir();
        string fileUrl = "https://example.test/PixelCanvas-1.0.47-win-x64-setup.exe";
        var handler = new FakeHttpHandler().MapText(fileUrl + ".sha256", "not-a-hash");
        var service = new UpdateService(new HttpClient(handler), currentVersion: "1.0.46", architecture: "x64");
        var asset = new UpdateAsset("PixelCanvas-1.0.47-win-x64-setup.exe", new Uri(fileUrl), new Uri(fileUrl + ".sha256"), 4);
        await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAsync(asset, dir.Path));
    }

    private static string ReleaseForService() => $$"""
    {
      "tag_name": "v1.0.47",
      "html_url": "https://github.com/Limak313/PixelCanvas-src/releases/tag/v1.0.47",
      "body": "notes",
      "draft": false,
      "prerelease": false,
      "assets": [
        {"name":"PixelCanvas-1.0.47-win-x64-setup.exe","browser_download_url":"https://example.test/PixelCanvas-1.0.47-win-x64-setup.exe","size":123},
        {"name":"PixelCanvas-1.0.47-win-x64-setup.exe.sha256","browser_download_url":"https://example.test/PixelCanvas-1.0.47-win-x64-setup.exe.sha256","size":66}
      ]
    }
    """;
}

public class UpdatePreferencesTests
{
    [Fact]
    public void Load_MissingFileReturnsAutoCheckEnabled()
    {
        using var dir = new TempDir();
        Assert.True(UpdatePreferencesFile.Load(dir.Path).AutoCheck);
        Assert.Null(UpdatePreferencesFile.Load(dir.Path).LastCheckUtc);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        using var dir = new TempDir();
        var expected = new UpdatePreferences(false, new DateTimeOffset(2026, 11, 27, 8, 0, 0, TimeSpan.FromHours(8)));
        UpdatePreferencesFile.Save(dir.Path, expected);
        var actual = UpdatePreferencesFile.Load(dir.Path);
        Assert.Equal(expected.AutoCheck, actual.AutoCheck);
        Assert.Equal(expected.LastCheckUtc, actual.LastCheckUtc);
        Assert.Single(Directory.GetFiles(dir.Path, "update-settings.json"));
    }

    [Fact]
    public void Load_CorruptFileFallsBackToDefaults()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "update-settings.json"), "{");
        Assert.True(UpdatePreferencesFile.Load(dir.Path).AutoCheck);
    }
}
