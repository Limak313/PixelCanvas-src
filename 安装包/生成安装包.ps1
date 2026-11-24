param(
    # 已生成的部署包目录（默认先调用 打包本地MCP.ps1 生成临时 Bundle 包）
    [string]$PackageRoot = '',
    [string]$OutputPath = '',
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Rid = 'win-x64',
    [switch]$KeepPackage
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
DefaultDirName={localappdata}\Programs\PixelCanvas
DefaultGroupName=PixelCanvas
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=$outDir
OutputBaseFilename=$outBase
SetupIconFile=$projectRoot\PixelCanvas\Assets\pixelcanvas.ico
UninstallDisplayIcon={app}\app\PixelCanvas.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=$targetArch
ArchitecturesInstallIn64BitMode=$targetArch
LicenseFile=$projectRoot\LICENSE
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "$PackageRoot\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\PixelCanvas"; Filename: "{app}\app\PixelCanvas.exe"; WorkingDir: "{app}\app"
Name: "{group}\MCP 说明"; Filename: "{app}\MCP.md"
Name: "{autodesktop}\PixelCanvas"; Filename: "{app}\app\PixelCanvas.exe"; WorkingDir: "{app}\app"; Tasks: desktopicon

[Run]
Filename: "{app}\app\PixelCanvas.exe"; Description: "{cm:LaunchProgram,PixelCanvas}"; Flags: nowait postinstall skipifsilent
"@
Set-Content -LiteralPath $issPath -Value $iss -Encoding utf8

$iscc = Find-Iscc
& $iscc $issPath
if ($LASTEXITCODE -ne 0) { throw '生成安装包失败。' }

if (-not (Test-Path -LiteralPath $OutputPath)) { throw "未生成预期文件：$OutputPath" }
$setup = Get-Item -LiteralPath $OutputPath
Write-Host ("已生成安装包：{0}（{1:N1} MB，自包含，无需预装 .NET）" -f $setup.FullName, ($setup.Length / 1MB))
foreach ($path in $cleanup) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force } }
