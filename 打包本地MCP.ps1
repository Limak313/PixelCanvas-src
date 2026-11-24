param(
    [string]$PackageName = '',
    [ValidateSet('Slim', 'Bundle', 'All')]
    [string]$Mode = 'Slim',
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Rid = 'win-x64',
    [switch]$NoPlugin,
    [switch]$NoPackage
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot

# 统一产品版本来自 Directory.Build.props（桌面程序、MCP 服务、插件清单对齐此版本）
[xml]$props = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props')
$version = [string]$props.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw 'Directory.Build.props 未设置 Version。' }

$modes = if ($Mode -eq 'All') { @('Slim', 'Bundle') } else { @($Mode) }

function Resolve-PackageBase([string]$mode) {
    if (-not [string]::IsNullOrWhiteSpace($PackageName)) {
        return $(if ($modes.Count -gt 1) { "$PackageName-$($mode.ToLower())" } else { $PackageName })
    }
    switch ($mode) {
        'Slim'   { "PixelCanvas-$version-$Rid-slim" }
        'Bundle' { "PixelCanvas-Agent-$version-$Rid" }
        default  { throw "未知打包模式：$mode" }
    }
}

function Format-Size([long]$bytes) { '{0:N1} MB' -f ($bytes / 1MB) }

$appProject = Join-Path $projectRoot 'PixelCanvas\PixelCanvas.csproj'
$serverProject = Join-Path $projectRoot 'PixelCanvas.Mcp\PixelCanvas.Mcp.csproj'
$appFiles = @('PixelCanvas.exe', 'PixelCanvas.dll', 'PixelCanvas.deps.json', 'PixelCanvas.runtimeconfig.json')
$serverFiles = @('PixelCanvas.Mcp.exe', 'PixelCanvas.Mcp.dll', 'PixelCanvas.Mcp.deps.json', 'PixelCanvas.Mcp.runtimeconfig.json', 'PixelCanvas.Core.dll', 'PixelCanvas.exe', 'PixelCanvas.dll', 'PixelCanvas.deps.json', 'PixelCanvas.runtimeconfig.json')

# 插件里的 app/server 始终是「多文件框架依赖」构建产物，与部署包模式无关
$pluginBuildRoot = Join-Path $projectRoot '.plugin-build'
if (-not $NoPlugin) {
    dotnet build $appProject -c Release --artifacts-path $pluginBuildRoot --no-incremental
    if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
    dotnet build $serverProject -c Release --artifacts-path $pluginBuildRoot --no-incremental
    if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
    $short = 'v' + ($version -replace '\.', '')
    $pluginDir = Join-Path $projectRoot "pixelcanvas-plugin\plugins\pixelcanvas-desktop-$short"
    if (-not (Test-Path -LiteralPath $pluginDir)) { throw "找不到插件目录：$pluginDir" }
    $pluginApp = Join-Path $pluginDir 'app'
    $pluginServer = Join-Path $pluginDir 'server'
    New-Item -ItemType Directory -Path $pluginApp, $pluginServer -Force | Out-Null
    foreach ($name in $appFiles) { Copy-Item -LiteralPath (Join-Path $pluginBuildRoot "bin\PixelCanvas\release\$name") -Destination $pluginApp -Force }
    Copy-Item -LiteralPath (Join-Path $pluginBuildRoot "bin\PixelCanvas\release\PixelCanvas.Core.dll") -Destination $pluginApp -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'PixelCanvas\Assets\pixelcanvas.ico') -Destination $pluginApp -Force
    foreach ($name in $serverFiles) {
        $src = Join-Path $pluginBuildRoot "bin\PixelCanvas.Mcp\release\$name"
        if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination $pluginServer -Force }
    }
    Write-Host "插件二进制已刷新：$pluginDir（$version）"
}

if ($NoPackage) { Write-Host '已跳过部署包生成。'; return }

Add-Type -AssemblyName System.IO.Compression.FileSystem

# ---------- Slim：框架依赖的单文件 exe（需要本机安装 .NET 10 Desktop Runtime） ----------
function New-SlimPackage([string]$base) {
    $buildRoot = Join-Path $projectRoot "$base-build"
    $packageRoot = Join-Path $projectRoot $base
    $zipPath = Join-Path $projectRoot "$base.zip"
    foreach ($path in @($packageRoot, $zipPath, $buildRoot)) { if (Test-Path -LiteralPath $path) { throw "目标已存在，请选择其他 PackageName：$path" } }

    $appDir = Join-Path $buildRoot 'app'
    $serverDir = Join-Path $buildRoot 'server'
    dotnet publish $appProject -c Release -r $Rid -p:PublishSingleFile=true --self-contained false -p:DebugType=none -o $appDir
    if ($LASTEXITCODE -ne 0) { throw '桌面程序发布失败。' }
    dotnet publish $serverProject -c Release -r $Rid -p:PublishSingleFile=true --self-contained false -p:DebugType=none -o $serverDir
    if ($LASTEXITCODE -ne 0) { throw 'MCP 服务发布失败。' }

    New-Item -ItemType Directory -Path (Join-Path $packageRoot 'app'), (Join-Path $packageRoot 'server') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $appDir 'PixelCanvas.exe') -Destination (Join-Path $packageRoot 'app')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'PixelCanvas\Assets\pixelcanvas.ico') -Destination (Join-Path $packageRoot 'app')
    Copy-Item -LiteralPath (Join-Path $serverDir 'PixelCanvas.Mcp.exe') -Destination (Join-Path $packageRoot 'server')
    Copy-Item -LiteralPath (Join-Path $appDir 'PixelCanvas.exe') -Destination (Join-Path $packageRoot 'server')
    Copy-Item -Path (Join-Path $projectRoot 'Agent部署包\*') -Destination $packageRoot -Recurse
    Move-Item -LiteralPath (Join-Path $packageRoot 'README.md') -Destination (Join-Path $packageRoot 'DEPLOYMENT-README.md')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'), (Join-Path $projectRoot 'MCP.md') -Destination $packageRoot

    [System.IO.Compression.ZipFile]::CreateFromDirectory($packageRoot, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    Write-Host "已生成框架依赖单文件包：$zipPath"
}

# ---------- Bundle：自包含单文件（免安装 .NET，可作为 win-arm64 交叉发布） ----------
function New-BundlePackage([string]$base) {
    $buildRoot = Join-Path $projectRoot "$base-build"
    $packageRoot = Join-Path $projectRoot $base
    $zipPath = Join-Path $projectRoot "$base.zip"
    foreach ($path in @($packageRoot, $zipPath, $buildRoot)) { if (Test-Path -LiteralPath $path) { throw "目标已存在，请选择其他 PackageName：$path" } }

    $appOut = Join-Path $buildRoot 'app'
    $serverOut = Join-Path $buildRoot 'server'
    dotnet publish $appProject -c Release -r $Rid --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $appOut
    if ($LASTEXITCODE -ne 0) { throw '桌面程序自包含发布失败。' }
    dotnet publish $serverProject -c Release -r $Rid --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $serverOut
    if ($LASTEXITCODE -ne 0) { throw 'MCP 服务自包含发布失败。' }

    New-Item -ItemType Directory -Path (Join-Path $packageRoot 'app'), (Join-Path $packageRoot 'server'), (Join-Path $packageRoot 'runtime') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $appOut 'PixelCanvas.exe') -Destination (Join-Path $packageRoot 'app')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'PixelCanvas\Assets\pixelcanvas.ico') -Destination (Join-Path $packageRoot 'app') -Force
    Copy-Item -LiteralPath (Join-Path $serverOut 'PixelCanvas.Mcp.exe') -Destination (Join-Path $packageRoot 'server')
    $dotnetRoot = Split-Path -Parent (Get-Command dotnet.exe -ErrorAction Stop).Source
    foreach ($name in @('LICENSE.txt', 'ThirdPartyNotices.txt')) { Copy-Item -LiteralPath (Join-Path $dotnetRoot $name) -Destination (Join-Path $packageRoot 'runtime') }
    Copy-Item -Path (Join-Path $projectRoot 'Agent部署包\*') -Destination $packageRoot -Recurse
    Move-Item -LiteralPath (Join-Path $packageRoot 'README.md') -Destination (Join-Path $packageRoot 'DEPLOYMENT-README.md')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'), (Join-Path $projectRoot 'MCP.md') -Destination $packageRoot

    [System.IO.Compression.ZipFile]::CreateFromDirectory($packageRoot, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    Write-Host "已生成自包含单文件包：$zipPath（$Rid）"
}

foreach ($mode in $modes) {
    $base = Resolve-PackageBase $mode
    if ($base -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { throw 'PackageName 必须是简单文件名。' }
    switch ($mode) {
        'Slim'   { New-SlimPackage $base }
        'Bundle' { New-BundlePackage $base }
    }

    $zipPath = Join-Path $projectRoot "$base.zip"
    $zipMb = (Get-Item -LiteralPath $zipPath).Length / 1MB
    $packageRoot = Join-Path $projectRoot $base
    $fileCount = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File).Count
    Write-Host ("  {0}：{1:N1} MB / {2} 个文件（解包后 {3:N1} MB）" -f $mode, $zipMb, $fileCount, ((Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Measure-Object Length -Sum).Sum / 1MB))
}
