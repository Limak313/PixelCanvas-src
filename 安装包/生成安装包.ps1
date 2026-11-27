param(
    # 已生成的部署包目录（默认先调用 打包本地MCP.ps1 生成临时 Bundle 包）
    [string]$PackageRoot = '',
    [string]$OutputPath = '',
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Rid = 'win-x64',
    [switch]$KeepPackage,
    # 可选 Authenticode 签名；不传时读取 PIXELCANVAS_SIGN_PFX / PIXELCANVAS_SIGN_PFX_PASSWORD
    [string]$SignPfxPath = '',
    [string]$SignPfxPassword = '',
    [string]$TimestampUrl = '',
    [switch]$RequireSignature
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$buildDir = Join-Path $PSScriptRoot '_build'
$tempBase = 'pc-installer-payload'

[xml]$props = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props')
$version = [string]$props.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw 'Directory.Build.props 未设置 Version。' }
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $projectRoot "PixelCanvas-$version-$Rid-setup.exe" }
$targetArch = if ($Rid -eq 'win-arm64') { 'arm64' } else { 'x64compatible' }
$versionInfoVersion = if (($version.Split('.').Count) -eq 3) { "$version.0" } else { $version }
if ([string]::IsNullOrWhiteSpace($TimestampUrl)) { $TimestampUrl = 'http://timestamp.digicert.com' }
if ([string]::IsNullOrWhiteSpace($SignPfxPath)) { $SignPfxPath = $env:PIXELCANVAS_SIGN_PFX }
if ([string]::IsNullOrWhiteSpace($SignPfxPassword)) { $SignPfxPassword = $env:PIXELCANVAS_SIGN_PFX_PASSWORD }

$cleanup = @()
if ([string]::IsNullOrWhiteSpace($PackageRoot)) {
    foreach ($path in @((Join-Path $projectRoot $tempBase), (Join-Path $projectRoot "$tempBase.zip"), (Join-Path $projectRoot "$tempBase-build"))) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
    & (Join-Path $projectRoot '打包本地MCP.ps1') -Mode Bundle -PackageName $tempBase -Rid $Rid -NoPlugin
    if ($LASTEXITCODE -ne 0) { throw '生成自包含部署包失败。' }
    $PackageRoot = Join-Path $projectRoot $tempBase
    if (-not $KeepPackage) { $cleanup = @($PackageRoot, (Join-Path $projectRoot "$tempBase.zip"), (Join-Path $projectRoot "$tempBase-build")) }
}
if (-not [System.IO.Path]::IsPathRooted($PackageRoot)) { $PackageRoot = Join-Path $projectRoot $PackageRoot }
if (-not (Test-Path -LiteralPath $PackageRoot)) { throw "找不到部署包目录：$PackageRoot" }
$PackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path

$requiredFiles = @('app\PixelCanvas.exe', 'server\PixelCanvas.Mcp.exe', 'README.md', 'MCP.md')
foreach ($relative in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $PackageRoot $relative))) { throw "部署包缺少必需文件：$relative" }
}

New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
$issPath = Join-Path $buildDir 'PixelCanvas.iss'
$outDir = Split-Path -Parent $OutputPath
$outBase = [System.IO.Path]::GetFileNameWithoutExtension($OutputPath)

function Find-Iscc {
    foreach ($path in @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'))) {
        if (Test-Path -LiteralPath $path) { return $path }
    }
    $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    # CI 兜底：从 GitHub API 取官方安装器（仅当本机没有 Inno Setup 时）
    $installer = Join-Path $env:TEMP 'innosetup-6.7.4.exe'
    if (-not (Test-Path -LiteralPath $installer)) {
        & gh api -H 'Accept: application/octet-stream' repos/jrsoftware/issrc/releases/assets/686086859 > $installer
        if ($LASTEXITCODE -ne 0) { throw '下载 Inno Setup 失败；请先安装 Inno Setup 6。' }
    }
    & $installer /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER | Out-Null
    $fallback = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
    if (-not (Test-Path -LiteralPath $fallback)) { throw '安装 Inno Setup 后仍找不到 ISCC.exe。' }
    return $fallback
}

function Find-SignTool {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kits) {
        $candidate = Get-ChildItem -LiteralPath $kits -Directory |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
            Where-Object { Test-Path -LiteralPath $_ } |
            Select-Object -First 1
        if ($candidate) { return $candidate }
    }
    return $null
}

$iscc = Find-Iscc
$chineseIsl = Join-Path (Split-Path -Parent $iscc) 'Languages\ChineseSimplified.isl'
$hasChinese = Test-Path -LiteralPath $chineseIsl
if (-not $hasChinese) { Write-Warning 'Inno Setup 缺少 ChineseSimplified.isl，安装界面将只提供英文。' }

$languageBlock = if ($hasChinese) {
@'
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
'@
} else {
'Name: "english"; MessagesFile: "compiler:Default.isl"'
}

$customLines = @(
    '[CustomMessages]',
    'english.McpDocs=Open MCP guide',
    'english.Readme=Open README',
    'english.AppPath=PixelCanvas application'
)
if ($hasChinese) {
    $customLines += @(
        'chinesesimplified.McpDocs=打开 MCP 说明',
        'chinesesimplified.Readme=打开说明文档',
        'chinesesimplified.AppPath=PixelCanvas 程序'
    )
}
$customMessages = $customLines -join "`r`n"

$iss = @"
; 由 安装包\生成安装包.ps1 生成，请勿手改；版本与部署包来自 Directory.Build.props
[Setup]
AppId={{8F3A1C2E-4B7E-4C1D-9B2F-6D5A7E8C9F01}
AppName=PixelCanvas
AppVerName=PixelCanvas $version
AppVersion=$version
AppPublisher=Limak313
AppPublisherURL=https://github.com/Limak313/PixelCanvas-src
AppSupportURL=https://github.com/Limak313/PixelCanvas-src
AppComments=PixelCanvas 像素画布与本地 MCP 服务
AppReadmeFile={app}\README.md
VersionInfoVersion=$versionInfoVersion
VersionInfoCompany=Limak313
VersionInfoDescription=PixelCanvas $version Setup
VersionInfoProductName=PixelCanvas
VersionInfoProductVersion=$version
VersionInfoCopyright=Copyright (C) 2026 Limak313
MinVersion=10.0
DefaultDirName={localappdata}\Programs\PixelCanvas
DefaultGroupName=PixelCanvas
DisableProgramGroupPage=yes
AllowNoIcons=yes
PrivilegesRequired=lowest
UsePreviousAppDir=yes
UsePreviousLanguage=yes
UsePreviousTasks=yes
OutputDir=$outDir
OutputBaseFilename=$outBase
SetupIconFile=$projectRoot\PixelCanvas\Assets\pixelcanvas.ico
UninstallDisplayIcon={app}\app\PixelCanvas.exe
UninstallDisplayName=PixelCanvas
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=$targetArch
ArchitecturesInstallIn64BitMode=$targetArch
LicenseFile=$projectRoot\LICENSE
SetupMutex=PixelCanvasSetup
AppMutex=PixelCanvas.Running
CloseApplications=yes
CloseApplicationsFilter=PixelCanvas.exe,PixelCanvas.Mcp.exe
RestartApplications=no
SetupLogging=yes

[Languages]
$languageBlock

$customMessages

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "$PackageRoot\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\PixelCanvas"; Filename: "{app}\app\PixelCanvas.exe"; WorkingDir: "{app}\app"
Name: "{group}\{cm:McpDocs}"; Filename: "{app}\MCP.md"
Name: "{group}\{cm:Readme}"; Filename: "{app}\README.md"
Name: "{group}\{cm:UninstallProgram,PixelCanvas}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\PixelCanvas"; Filename: "{app}\app\PixelCanvas.exe"; WorkingDir: "{app}\app"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\PixelCanvas.exe"; ValueType: string; ValueName: ""; ValueData: "{app}\app\PixelCanvas.exe"; Flags: uninsdeletekey

[Run]
Filename: "{app}\app\PixelCanvas.exe"; Description: "{cm:LaunchProgram,PixelCanvas}"; Flags: nowait postinstall skipifsilent
"@
Set-Content -LiteralPath $issPath -Value $iss -Encoding utf8

& $iscc $issPath
if ($LASTEXITCODE -ne 0) { throw '生成安装包失败。' }

if (-not (Test-Path -LiteralPath $OutputPath)) { throw "未生成预期文件：$OutputPath" }

$signatureStatus = 'NotSigned'
if (-not [string]::IsNullOrWhiteSpace($SignPfxPath)) {
    if (-not (Test-Path -LiteralPath $SignPfxPath)) { throw "找不到签名证书：$SignPfxPath" }
    $signTool = Find-SignTool
    if (-not $signTool) {
        if ($RequireSignature) { throw '找不到 signtool.exe，无法签名安装包。' }
        Write-Warning '找不到 signtool.exe，跳过安装包签名。'
    } else {
        $signArgs = @('sign', '/fd', 'SHA256', '/f', (Resolve-Path -LiteralPath $SignPfxPath).Path)
        if (-not [string]::IsNullOrWhiteSpace($SignPfxPassword)) { $signArgs += @('/p', $SignPfxPassword) }
        $signArgs += @('/tr', $TimestampUrl, '/td', 'SHA256', $OutputPath)
        & $signTool @signArgs
        if ($LASTEXITCODE -ne 0) { throw '安装包签名失败。' }
    }
}
$signature = Get-AuthenticodeSignature -LiteralPath $OutputPath
$signatureStatus = [string]$signature.Status
if ($RequireSignature -and $signatureStatus -ne 'Valid') {
    throw "安装包签名校验失败：$signatureStatus"
}
if ($signatureStatus -eq 'Valid') {
    Write-Host "安装包签名：$($signature.SignerCertificate.Subject)"
} elseif ($RequireSignature) {
    throw "安装包未签名：$signatureStatus"
} else {
    Write-Warning "安装包未签名（$signatureStatus）；正式发布建议设置 PIXELCANVAS_SIGN_PFX。"
}

$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $OutputPath).Hash.ToLowerInvariant()
$checksumPath = "$OutputPath.sha256"
Set-Content -LiteralPath $checksumPath -Value "$hash  $([System.IO.Path]::GetFileName($OutputPath))" -Encoding ascii

$setup = Get-Item -LiteralPath $OutputPath
Write-Host ("已生成安装包：{0}（{1:N1} MB，自包含，无需预装 .NET）" -f $setup.FullName, ($setup.Length / 1MB))
Write-Host "SHA-256：$hash"
foreach ($path in $cleanup) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force } }
