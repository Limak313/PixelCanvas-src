param(
    [string]$PackageName = '',
    [switch]$NoPlugin,
    [switch]$NoPackage
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot

# 统一产品版本来自 Directory.Build.props（桌面程序、MCP 服务、插件清单对齐此版本）
[xml]$props = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props')
$version = [string]$props.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw 'Directory.Build.props 未设置 Version。' }
if ([string]::IsNullOrWhiteSpace($PackageName)) { $PackageName = "PixelCanvas-Agent-$version-win-x64" }
if ($PackageName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { throw 'PackageName 必须是简单文件名。' }

$buildRoot = Join-Path $projectRoot "$PackageName-build"
$packageRoot = Join-Path $projectRoot $PackageName
$zipPath = Join-Path $projectRoot "$PackageName.zip"
foreach ($path in @($packageRoot, $zipPath, $buildRoot)) { if (Test-Path -LiteralPath $path) { throw "目标已存在，请选择其他 PackageName：$path" } }

dotnet build (Join-Path $projectRoot 'PixelCanvas.Mcp\PixelCanvas.Mcp.csproj') -c Release --artifacts-path $buildRoot --no-incremental
if ($LASTEXITCODE -ne 0) { throw '构建失败。' }

$appOut = Join-Path $buildRoot 'bin\PixelCanvas\release'
$serverOut = Join-Path $buildRoot 'bin\PixelCanvas.Mcp\release'
$appFiles = @('PixelCanvas.exe', 'PixelCanvas.dll', 'PixelCanvas.deps.json', 'PixelCanvas.runtimeconfig.json')
$serverFiles = @('PixelCanvas.Mcp.exe', 'PixelCanvas.Mcp.dll', 'PixelCanvas.Mcp.deps.json', 'PixelCanvas.Mcp.runtimeconfig.json', 'PixelCanvas.exe', 'PixelCanvas.dll', 'PixelCanvas.deps.json', 'PixelCanvas.runtimeconfig.json')

# 刷新随插件入库的桌面程序与 MCP 服务二进制（排除 pdb）
if (-not $NoPlugin) {
    $short = 'v' + ($version -replace '\.', '')
    $pluginDir = Join-Path $projectRoot "pixelcanvas-plugin\plugins\pixelcanvas-desktop-$short"
    if (-not (Test-Path -LiteralPath $pluginDir)) { throw "找不到插件目录：$pluginDir" }
    $pluginApp = Join-Path $pluginDir 'app'
    $pluginServer = Join-Path $pluginDir 'server'
    New-Item -ItemType Directory -Path $pluginApp, $pluginServer -Force | Out-Null
    foreach ($name in $appFiles) { Copy-Item -LiteralPath (Join-Path $appOut $name) -Destination $pluginApp -Force }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'PixelCanvas\Assets\pixelcanvas.ico') -Destination $pluginApp -Force
    foreach ($name in $serverFiles) {
        $src = Join-Path $serverOut $name
        if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination $pluginServer -Force }
    }
    Write-Host "插件二进制已刷新：$pluginDir（$version）"
}

if ($NoPackage) { Write-Host '已跳过部署包生成。'; return }

# 自包含部署包（携带 .NET 10 桌面运行时）
$dotnetRoot = Split-Path -Parent (Get-Command dotnet.exe -ErrorAction Stop).Source
$coreRoot = Join-Path $dotnetRoot 'shared\Microsoft.NETCore.App'
$desktopRoot = Join-Path $dotnetRoot 'shared\Microsoft.WindowsDesktop.App'
$versions = @(Get-ChildItem -LiteralPath $coreRoot -Directory | Where-Object { $_.Name -match '^10\.0\.\d+$' -and (Test-Path -LiteralPath (Join-Path $desktopRoot $_.Name)) } | Sort-Object { [version]$_.Name } -Descending)
if ($versions.Count -eq 0) { throw '需要本机安装 .NET 10 x64 和 Windows Desktop 运行时。' }
$rtVersion = $versions[0].Name
if (!(Test-Path -LiteralPath (Join-Path $dotnetRoot "host\fxr\$rtVersion\hostfxr.dll"))) { throw "找不到匹配的 hostfxr：$rtVersion" }
foreach ($dir in @('app', 'server', 'runtime\host\fxr', 'runtime\shared\Microsoft.NETCore.App', 'runtime\shared\Microsoft.WindowsDesktop.App')) { New-Item -ItemType Directory -Path (Join-Path $packageRoot $dir) -Force | Out-Null }
foreach ($name in $appFiles) { Copy-Item -LiteralPath (Join-Path $appOut $name) -Destination (Join-Path $packageRoot 'app') }
Copy-Item -LiteralPath (Join-Path $projectRoot 'PixelCanvas\Assets\pixelcanvas.ico') -Destination (Join-Path $packageRoot 'app') -Force
foreach ($name in @('PixelCanvas.Mcp.exe', 'PixelCanvas.Mcp.dll', 'PixelCanvas.Mcp.deps.json', 'PixelCanvas.Mcp.runtimeconfig.json', 'PixelCanvas.dll')) { Copy-Item -LiteralPath (Join-Path $serverOut $name) -Destination (Join-Path $packageRoot 'server') }
Copy-Item -LiteralPath (Join-Path $dotnetRoot 'dotnet.exe'), (Join-Path $dotnetRoot 'LICENSE.txt'), (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') -Destination (Join-Path $packageRoot 'runtime')
Copy-Item -LiteralPath (Join-Path $dotnetRoot "host\fxr\$rtVersion") -Destination (Join-Path $packageRoot 'runtime\host\fxr') -Recurse
Copy-Item -LiteralPath (Join-Path $coreRoot $rtVersion) -Destination (Join-Path $packageRoot 'runtime\shared\Microsoft.NETCore.App') -Recurse
Copy-Item -LiteralPath (Join-Path $desktopRoot $rtVersion) -Destination (Join-Path $packageRoot 'runtime\shared\Microsoft.WindowsDesktop.App') -Recurse
Copy-Item -Path (Join-Path $projectRoot 'Agent部署包\*') -Destination $packageRoot -Recurse
Move-Item -LiteralPath (Join-Path $packageRoot 'README.md') -Destination (Join-Path $packageRoot 'DEPLOYMENT-README.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'), (Join-Path $projectRoot 'MCP.md') -Destination $packageRoot
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($packageRoot, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)
Write-Host "ZIP 已生成：$zipPath"
Write-Host "部署包已生成：$packageRoot（运行时 $rtVersion）"
