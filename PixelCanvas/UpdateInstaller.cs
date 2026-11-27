using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PixelCanvas;

/// <summary>安装包签名者固定（可选）与静默安装启动。</summary>
internal static class UpdateInstaller
{
    internal const string SignerThumbprintEnv = "PIXELCANVAS_UPDATE_SIGNER_THUMBPRINT";

    /// <summary>
    /// 若配置了 PIXELCANVAS_UPDATE_SIGNER_THUMBPRINT，则安装包必须带签名且证书指纹一致。
    /// 当前发布证书是自签名测试证书，不能依赖系统信任链；指纹固定可以阻止“别的签名包”混入。
    /// </summary>
    internal static bool VerifySigner(string installerPath)
    {
        string? expected = Environment.GetEnvironmentVariable(SignerThumbprintEnv);
        if (string.IsNullOrWhiteSpace(expected)) return true;
        expected = NormalizeThumbprint(expected);
        if (expected.Length != 40) throw new UpdateException("更新签名者指纹配置格式不正确。");
        string actual;
        try { actual = GetSignerThumbprint(installerPath); }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new UpdateException("安装包没有可读取的 Authenticode 签名，已停止更新。", ex);
        }
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("安装包签名者与固定指纹不一致，已停止更新。");
        return true;
    }

    internal static string GetSignerThumbprint(string installerPath)
    {
        // CreateFromSignedFile 是读取 Authenticode 内嵌证书的唯一内置入口；这里只读取签名者，不导入证书。
#pragma warning disable SYSLIB0057
        using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(installerPath));
#pragma warning restore SYSLIB0057
        return NormalizeThumbprint(certificate.Thumbprint);
    }

    private static string NormalizeThumbprint(string value) =>
        new string(value.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    internal static void LaunchInstaller(string installerPath)
    {
        var start = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Path.GetTempPath(),
        };
        if (Process.Start(start) is null) throw new UpdateException("无法启动更新安装器。");
    }
}
