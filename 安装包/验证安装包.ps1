param(
    [string]$SetupPath = '',
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Rid = 'win-x64',
    [switch]$SkipInstall,
    [switch]$KeepInstall,
    [switch]$RequireSignature
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$version = [string]([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props'))).Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($SetupPath)) { $SetupPath = Join-Path $projectRoot "PixelCanvas-$version-$Rid-setup.exe" }
$SetupPath = (Resolve-Path -LiteralPath $SetupPath).Path

function Assert($condition, $message) {
    if (-not $condition) { throw $message }
}

$info = (Get-Item -LiteralPath $SetupPath).VersionInfo
Write-Host ("安装包：{0}（{1:N1} MB）" -f $SetupPath, ((Get-Item $SetupPath).Length / 1MB))
$productName = ([string]$info.ProductName).Trim()
$companyName = ([string]$info.CompanyName).Trim()
$productVersion = ([string]$info.ProductVersion).Trim()
$fileDescription = ([string]$info.FileDescription).Trim()
Write-Host ("版本信息：File={0} Product={1} Company={2} Desc={3}" -f $info.FileVersion, $productVersion, $companyName, $fileDescription)
Assert ($productName -eq 'PixelCanvas') "ProductName 不正确：$productName"
Assert ($companyName -eq 'Limak313') "CompanyName 不正确：$companyName"
Assert ($productVersion -eq $version) "ProductVersion 不正确：$productVersion"
Assert ($fileDescription -eq "PixelCanvas $version Setup") "FileDescription 不正确：$fileDescription"

$signature = Get-AuthenticodeSignature -LiteralPath $SetupPath
Write-Host "Authenticode：$($signature.Status)"
$signerThumbprint = $null
if ($null -ne $signature.SignerCertificate) { $signerThumbprint = $signature.SignerCertificate.Thumbprint }
if ($RequireSignature) {
    Assert ($signature.Status -eq 'Valid') "签名校验失败：$($signature.Status)"
    Assert ($null -ne $signerThumbprint) '安装包没有签名者证书。'
}

$checksumPath = "$SetupPath.sha256"
if (Test-Path -LiteralPath $checksumPath) {
    $expected = (Get-Content -LiteralPath $checksumPath -Raw).Trim().Split(' ')[0]
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $SetupPath).Hash.ToLowerInvariant()
    Assert ($expected -eq $actual) "SHA-256 校验和不匹配。"
    Write-Host "SHA-256：$actual"
} else {
    Write-Warning '找不到 .sha256 侧车文件，跳过校验和检查。'
}

if ($SkipInstall) {
    Write-Host '已跳过安装/卸载生命周期验证。'
    return
}

$hostArch = $env:PROCESSOR_ARCHITECTURE
if ($Rid -eq 'win-arm64' -and $hostArch -ne 'ARM64') {
    Write-Warning "当前主机是 $hostArch，无法执行 arm64 安装包；已跳过敏捷安装/卸载。"
    return
}

$installDir = Join-Path $env:TEMP ("pc-installer-verify-" + [guid]::NewGuid().ToString('N'))
$logPath = Join-Path $env:TEMP ("pc-installer-verify-" + [guid]::NewGuid().ToString('N') + '.log')
$installed = $false
try {
    Write-Host "静默安装到临时目录：$installDir"
    $installArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=$installDir", "/LOG=$logPath")
    $process = Start-Process -FilePath $SetupPath -ArgumentList $installArgs -Wait -PassThru
    Assert ($process.ExitCode -eq 0) "安装器退出码：$($process.ExitCode)；日志：$logPath"
    $installed = $true

    foreach ($relative in @('app\PixelCanvas.exe', 'server\PixelCanvas.Mcp.exe', 'README.md', 'MCP.md', 'unins000.exe')) {
        Assert (Test-Path -LiteralPath (Join-Path $installDir $relative)) "安装后缺少文件：$relative"
    }

    $appSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $installDir 'app\PixelCanvas.exe')
    $serverSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $installDir 'server\PixelCanvas.Mcp.exe')
    Write-Host "已安装主程序签名：$($appSignature.Status)；MCP 签名：$($serverSignature.Status)"
    if ($null -ne $signerThumbprint) {
        Assert ($null -ne $appSignature.SignerCertificate) '已安装主程序没有签名。'
        Assert ($null -ne $serverSignature.SignerCertificate) '已安装 MCP 入口没有签名。'
        Assert ($appSignature.SignerCertificate.Thumbprint -eq $signerThumbprint) '主程序签名者与安装包不一致。'
        Assert ($serverSignature.SignerCertificate.Thumbprint -eq $signerThumbprint) 'MCP 签名者与安装包不一致。'
    }
    if ($RequireSignature) {
        Assert ($appSignature.Status -eq 'Valid') "主程序签名无效：$($appSignature.Status)"
        Assert ($serverSignature.Status -eq 'Valid') "MCP 签名无效：$($serverSignature.Status)"
    }

    $appPathKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\PixelCanvas.exe'
    $appPath = (Get-ItemProperty -LiteralPath $appPathKey -ErrorAction SilentlyContinue).'(default)'
    Assert ($appPath -eq (Join-Path $installDir 'app\PixelCanvas.exe')) "App Paths 未指向安装目录：$appPath"

    $uninstallKey = Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
        ForEach-Object { Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue } |
        Where-Object { $_.DisplayName -eq 'PixelCanvas' -and $_.UninstallString -like "*$installDir*" } |
        Select-Object -First 1
    Assert ($null -ne $uninstallKey) '卸载注册项未创建或路径不匹配。'

    $uninstaller = Join-Path $installDir 'unins000.exe'
    Write-Host '静默卸载……'
    $uninstallArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    $uninstallProcess = Start-Process -FilePath $uninstaller -ArgumentList $uninstallArgs -Wait -PassThru
    Assert ($uninstallProcess.ExitCode -eq 0) "卸载器退出码：$($uninstallProcess.ExitCode)"

    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and (Test-Path -LiteralPath (Join-Path $installDir 'app\PixelCanvas.exe'))) {
        Start-Sleep -Milliseconds 500
    }
    Assert (-not (Test-Path -LiteralPath (Join-Path $installDir 'app\PixelCanvas.exe'))) '卸载后 app\PixelCanvas.exe 仍存在。'
    Assert (-not (Test-Path -LiteralPath $appPathKey)) '卸载后 App Paths 注册项仍存在。'
    Write-Host '安装、App Paths、卸载与清理均通过。'
} finally {
    if ($installed -and -not $KeepInstall -and (Test-Path -LiteralPath (Join-Path $installDir 'unins000.exe'))) {
        try { Start-Process -FilePath (Join-Path $installDir 'unins000.exe') -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait | Out-Null } catch { }
    }
    if (-not $KeepInstall -and (Test-Path -LiteralPath $installDir)) {
        Remove-Item -LiteralPath $installDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $logPath) { Write-Host "安装日志：$logPath" }
}
