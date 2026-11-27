using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace PixelCanvas;

/// <summary>更新检查失败时用于向界面提供可读原因。</summary>
internal sealed class UpdateException : Exception
{
    internal UpdateException(string message, Exception? inner = null) : base(message, inner) { }
}

internal sealed record UpdateAsset(string Name, Uri DownloadUrl, Uri ChecksumUrl, long Size);

internal sealed record UpdateInfo(
    string Version,
    string TagName,
    Uri ReleasePage,
    string Notes,
    UpdateAsset Installer);

/// <summary>版本号解析与“是否更新”的单一判定。</summary>
internal static class UpdateVersion
{
    /// <summary>把 v1.0.47 / 1.0.47 / 1.0.47.0 归一化为四段版本号；无法解析返回 null。</summary>
    internal static string? Normalize(string? value)
    {
        string? clean = Clean(value);
        if (clean is null || !Version.TryParse(clean, out var version)) return null;
        return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}.{Math.Max(0, version.Revision)}";
    }

    /// <summary>用于展示与资产文件名的版本号，保留原始段数（1.0.47 不会变成 1.0.47.0）。</summary>
    internal static string? Display(string? value)
    {
        string? clean = Clean(value);
        if (clean is null) return null;
        return Version.TryParse(clean, out var version) ? version.ToString() : null;
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        int plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];
        int dash = text.IndexOf('-');
        if (dash >= 0) text = text[..dash];
        return Version.TryParse(text, out _) ? text : null;
    }

    internal static bool IsNewer(string? candidate, string? current)
    {
        string? left = Normalize(candidate);
        string? right = Normalize(current);
        if (left is null || right is null) return false;
        return Version.Parse(left) > Version.Parse(right);
    }

    internal static string ArchitectureName(Architecture architecture) => architecture switch
    {
        Architecture.Arm64 => "arm64",
        _ => "x64",
    };

    internal static string InstallerName(string version, string architecture) =>
        $"PixelCanvas-{Display(version) ?? version}-win-{architecture}-setup.exe";
}

/// <summary>从 GitHub Releases latest 响应中挑选当前架构的安装包。</summary>
internal static class UpdateReleaseParser
{
    internal static UpdateInfo? ParseLatest(string json, string currentVersion, string architecture)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new UpdateException("更新信息格式不正确。", ex); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new UpdateException("更新信息格式不正确。");
            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True) return null;

            string tag = root.TryGetProperty("tag_name", out var tagNode) ? tagNode.GetString() ?? "" : "";
            string? version = UpdateVersion.Display(tag);
            if (version is null) throw new UpdateException("发布版本号无法识别。");
            if (!UpdateVersion.IsNewer(version, currentVersion)) return null;

            string page = root.TryGetProperty("html_url", out var pageNode) ? pageNode.GetString() ?? "" : "";
            if (!Uri.TryCreate(page, UriKind.Absolute, out var pageUri) || pageUri.Scheme != Uri.UriSchemeHttps)
                throw new UpdateException("发布页面地址不正确。");
            string notes = root.TryGetProperty("body", out var bodyNode) ? bodyNode.GetString() ?? "" : "";

            string expected = UpdateVersion.InstallerName(version, architecture);
            string checksumName = expected + ".sha256";
            JsonElement? installer = null, checksum = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.ValueKind != JsonValueKind.Object) continue;
                    string name = asset.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? "" : "";
                    if (string.Equals(name, expected, StringComparison.OrdinalIgnoreCase)) installer = asset;
                    else if (string.Equals(name, checksumName, StringComparison.OrdinalIgnoreCase)) checksum = asset;
                }
            }
            if (installer is null) throw new UpdateException($"该版本没有适用于 win-{architecture} 的安装包。");
            if (checksum is null) throw new UpdateException("该版本缺少 SHA-256 校验文件，已拒绝自动更新。");

            var file = ReadAsset(installer.Value);
            var check = ReadAsset(checksum.Value);
            if (!string.Equals(file.Name, expected, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("安装包名称不正确。");
            if (!string.Equals(check.Name, checksumName, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("校验文件名称不正确。");
            return new UpdateInfo(version, tag, pageUri, notes, new UpdateAsset(file.Name, file.Url, check.Url, file.Size));
        }
    }

    private static (string Name, Uri Url, long Size) ReadAsset(JsonElement asset)
    {
        string name = asset.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? "" : "";
        string url = asset.TryGetProperty("browser_download_url", out var urlNode) ? urlNode.GetString() ?? "" : "";
        long size = asset.TryGetProperty("size", out var sizeNode) && sizeNode.TryGetInt64(out long value) ? value : 0;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new UpdateException($"更新资产 {name} 的下载地址不正确。");
        if (string.IsNullOrWhiteSpace(name)) throw new UpdateException("更新资产缺少文件名。");
        return (name, uri, size);
    }
}

/// <summary>检查 GitHub 最新正式 Release，并把安装包下载到本地更新目录。</summary>
internal sealed class UpdateService
{
    internal const string DefaultRepository = "Limak313/PixelCanvas-src";
    private readonly HttpClient _http;
    private readonly string _repository;
    private readonly string _currentVersion;
    private readonly string _architecture;

    internal UpdateService(
        HttpClient? http = null,
        string? repository = null,
        string? currentVersion = null,
        string? architecture = null)
    {
        _http = http ?? CreateHttpClient();
        _repository = repository ?? DefaultRepository;
        _currentVersion = currentVersion ?? ProductInfo.CurrentVersion;
        _architecture = architecture ?? UpdateVersion.ArchitectureName(RuntimeInformation.OSArchitecture);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(12),
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"PixelCanvas-Updater/{ProductInfo.CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    internal string CurrentVersion => _currentVersion;
    internal string Architecture => _architecture;

    /// <summary>返回可用更新；没有更新（或 latest 不存在）返回 null。</summary>
    internal async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        string url = $"https://api.github.com/repos/{_repository}/releases/latest";
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new UpdateException("GitHub 暂时限制了更新查询，请稍后再试。");
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"更新服务返回 {(int)response.StatusCode}。");
            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return UpdateReleaseParser.ParseLatest(json, _currentVersion, _architecture);
        }
        catch (UpdateException) { throw; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException("连接更新服务超时，请检查网络后重试。");
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException("无法连接更新服务，请检查网络后重试。", ex);
        }
    }

    /// <summary>下载安装包并校验 SHA-256；校验失败不会留下可执行文件。</summary>
    internal async Task<string> DownloadAsync(
        UpdateAsset asset,
        string directory,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("更新目录不能为空。", nameof(directory));
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, asset.Name);
        string checksumPath = target + ".sha256";
        string partial = target + ".part";
        try
        {
            string checksum = await DownloadChecksumAsync(asset, cancellationToken).ConfigureAwait(false);
            if (File.Exists(target) && FileLengthEquals(target, asset.Size) && ChecksumMatches(target, checksum))
            {
                progress?.Report(100);
                return target;
            }

            using var response = await _http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new UpdateException($"下载安装包失败：HTTP {(int)response.StatusCode}。");
            long? total = response.Content.Headers.ContentLength ?? (asset.Size > 0 ? asset.Size : null);
            if (File.Exists(partial)) File.Delete(partial);
            using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[128 * 1024];
                long readTotal = 0;
                int lastPercent = -1;
                while (true)
                {
                    int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    readTotal += read;
                    int percent = total is > 0 ? (int)Math.Clamp(readTotal * 100 / total.Value, 0, 100) : 0;
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Report(percent);
                    }
                }
                output.Flush(true);
                string actual = Convert.ToHexString(hash.GetHashAndReset());
                if (!string.Equals(actual, checksum, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateException("安装包 SHA-256 校验失败，已停止更新。");
                if (asset.Size > 0 && readTotal != asset.Size)
                    throw new UpdateException("安装包大小与发布信息不一致，已停止更新。");
            }
            if (File.Exists(target)) File.Delete(target);
            File.Move(partial, target);
            AtomicFile.WriteText(checksumPath, checksum + "  " + asset.Name + "\n");
            progress?.Report(100);
            return target;
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private async Task<string> DownloadChecksumAsync(UpdateAsset asset, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(asset.ChecksumUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new UpdateException($"下载校验文件失败：HTTP {(int)response.StatusCode}。");
        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string? value = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (value is null || value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new UpdateException("SHA-256 校验文件格式不正确。");
        return value;
    }

    private static bool FileLengthEquals(string path, long size)
    {
        if (size <= 0) return false;
        try { return new FileInfo(path).Length == size; }
        catch (IOException) { return false; }
    }

    private static bool ChecksumMatches(string path, string expected)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            string actual = Convert.ToHexString(SHA256.HashData(stream));
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

/// <summary>更新偏好的轻量持久化：只记录“启动时自动检查”和上次成功检查时间。</summary>
internal sealed record UpdatePreferences(bool AutoCheck = true, DateTimeOffset? LastCheckUtc = null);

internal static class UpdatePreferencesFile
{
    internal static string FilePath(string root) => Path.Combine(root, "update-settings.json");

    internal static UpdatePreferences Load(string root)
    {
        string path = FilePath(root);
        try
        {
            if (!File.Exists(path)) return new UpdatePreferences();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 64 * 1024) return new UpdatePreferences();
            using var document = JsonDocument.Parse(stream);
            var rootNode = document.RootElement;
            bool auto = !rootNode.TryGetProperty("autoCheck", out var autoNode) || autoNode.ValueKind != JsonValueKind.False;
            DateTimeOffset? last = rootNode.TryGetProperty("lastCheckUtc", out var lastNode)
                && lastNode.TryGetDateTimeOffset(out var value) ? value : null;
            return new UpdatePreferences(auto, last);
        }
        catch (IOException) { return new UpdatePreferences(); }
        catch (JsonException) { return new UpdatePreferences(); }
        catch (UnauthorizedAccessException) { return new UpdatePreferences(); }
    }

    internal static void Save(string root, UpdatePreferences preferences)
    {
        Directory.CreateDirectory(root);
        AtomicFile.WriteText(FilePath(root), JsonSerializer.Serialize(new
        {
            format = "pixelcanvas-update-settings",
            version = 1,
            autoCheck = preferences.AutoCheck,
            lastCheckUtc = preferences.LastCheckUtc,
        }));
    }
}

/// <summary>当前程序版本：优先取 InformationalVersion，取不到时退回 ProductVersion。</summary>
internal static class ProductInfo
{
    internal static string CurrentVersion
    {
        get
        {
            try
            {
                var assembly = typeof(ProductInfo).Assembly;
                string? informational = assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                string? display = UpdateVersion.Display(informational);
                if (display is not null) return display;
                return UpdateVersion.Display(assembly.GetName().Version?.ToString()) ?? "0.0.0.0";
            }
            catch { return "0.0.0.0"; }
        }
    }
}
