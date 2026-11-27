# 生成开发/测试用自签名代码签名证书。
# 这只用于验证签名链路：证书不在公共信任链中，不会消除 SmartScreen 的“未知发布者”。
# 正式发布必须使用受信任 CA 签发的代码签名证书（OV/EV）。
param(
    [string]$Subject = 'CN=PixelCanvas Development, O=Limak313, C=CN',
    [string]$Password = '',
    [int]$ValidYears = 3,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$signingDir = Join-Path $projectRoot '.signing'
$pfxPath = Join-Path $signingDir 'PixelCanvas-Dev.pfx'
$cerPath = Join-Path $signingDir 'PixelCanvas-Dev.cer'
$passwordPath = Join-Path $signingDir 'password.txt'

if ((Test-Path -LiteralPath $pfxPath) -and -not $Force) {
    throw "证书已存在：$pfxPath；如需重建请加 -Force。"
}
New-Item -ItemType Directory -Path $signingDir -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($Password)) {
    $Password = ([guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N').Substring(0, 8))
}

# 用 .NET CertificateRequest 生成自签名证书，避免依赖本机 CertEnroll 提供程序。
$rsa = [System.Security.Cryptography.RSA]::Create(3072)
try {
    $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
        $Subject,
        $rsa,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)

    $request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
    $request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
    $ekuOids = New-Object System.Security.Cryptography.OidCollection
    $ekuOids.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3')) | Out-Null
    $request.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($ekuOids, $false))

    $cert = $request.CreateSelfSigned((Get-Date).AddDays(-1), (Get-Date).AddYears($ValidYears))
    $pfxBytes = $cert.Export(
        [System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $Password)
    [System.IO.File]::WriteAllBytes($pfxPath, $pfxBytes)
    $cerBytes = $cert.Export(
        [System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
    [System.IO.File]::WriteAllBytes($cerPath, $cerBytes)
} finally {
    $rsa.Dispose()
}

Set-Content -LiteralPath $passwordPath -Value $Password -Encoding ascii
# 收紧私钥口令文件权限，仅当前用户可读。
$acl = Get-Acl -LiteralPath $passwordPath
$acl.SetAccessRuleProtection($true, $false)
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    [System.Security.Principal.WindowsIdentity]::GetCurrent().Name,
    'FullControl', 'Allow')
$acl.SetAccessRule($rule)
Set-Acl -LiteralPath $passwordPath -AclObject $acl

Write-Host "自签名代码签名证书已生成。"
Write-Host "  主题：$($cert.Subject)"
Write-Host "  指纹：$($cert.Thumbprint)"
Write-Host "  有效期至：$($cert.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host "  PFX：$pfxPath"
Write-Host "  证书：$cerPath"
Write-Host "  口令：$passwordPath（仅本机，已被 .gitignore 排除）"
Write-Host ""
Write-Host "签名示例："
Write-Host "  .\安装包\生成安装包.ps1 -PackageRoot PixelCanvas-Agent-1.0.46-win-x64 -Rid win-x64 ``"
Write-Host "    -SignPfxPath '$pfxPath' -SignPfxPassword (Get-Content '$passwordPath' -Raw).Trim() -NoTimestamp"
Write-Host ""
Write-Host "注意：自签名证书只验证签名链路，公开分发前应购买受信任 CA 的代码签名证书。"
