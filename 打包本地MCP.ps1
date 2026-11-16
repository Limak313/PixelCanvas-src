param([string]$PackageName = 'PixelCanvas-Agent-1.0.35-win-x64')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
if ($PackageName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { throw 'PackageName 必须是简单文件名。' }
$packageRoot = Join-Path $projectRoot $PackageName
$zipPath = Join-Path $projectRoot "$PackageName.zip"
$buildRoot = Join-Path $projectRoot "$PackageName-build"
foreach ($path in @($packageRoot, $zipPath, $buildRoot)) { if (Test-Path -LiteralPath $path) { throw "目标已存在，请选择其他 PackageName：$path" } }
$dotnetRoot = Split-Path -Parent (Get-Command dotnet.exe -ErrorAction Stop).Source
$coreRoot = Join-Path $dotnetRoot 'shared\Microsoft.NETCore.App'
$desktopRoot = Join-Path $dotnetRoot 'shared\Microsoft.WindowsDesktop.App'
$versions = @(Get-ChildItem -LiteralPath $coreRoot -Directory | Where-Object { $_.Name -match '^10\.0\.\d+$' -and (Test-Path -LiteralPath (Join-Path $desktopRoot $_.Name)) } | Sort-Object { [version]$_.Name } -Descending)
if ($versions.Count -eq 0) { throw '需要本机安装 .NET 10 x64 和 Windows Desktop 运行时。' }
$version = $versions[0].Name
if (!(Test-Path -LiteralPath (Join-Path $dotnetRoot "host\fxr\$version\hostfxr.dll"))) { throw "找不到匹配的 hostfxr：$version" }
dotnet build (Join-Path $projectRoot 'PixelCanvas.Mcp\PixelCanvas.Mcp.csproj') -c Release --artifacts-path $buildRoot --no-incremental
if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
foreach ($dir in @('app','server','runtime\host\fxr','runtime\shared\Microsoft.NETCore.App','runtime\shared\Microsoft.WindowsDesktop.App')) { New-Item -ItemType Directory -Path (Join-Path $packageRoot $dir) -Force | Out-Null }
foreach ($name in @('PixelCanvas.exe','PixelCanvas.dll','PixelCanvas.deps.json','PixelCanvas.runtimeconfig.json')) { Copy-Item -LiteralPath (Join-Path $projectRoot "$PackageName-build\bin\PixelCanvas\release\$name") -Destination (Join-Path $packageRoot 'app') }
foreach ($name in @('PixelCanvas.Mcp.exe','PixelCanvas.Mcp.dll','PixelCanvas.Mcp.deps.json','PixelCanvas.Mcp.runtimeconfig.json','PixelCanvas.dll')) { Copy-Item -LiteralPath (Join-Path $projectRoot "$PackageName-build\bin\PixelCanvas.Mcp\release\$name") -Destination (Join-Path $packageRoot 'server') }
Copy-Item -LiteralPath (Join-Path $dotnetRoot 'dotnet.exe'), (Join-Path $dotnetRoot 'LICENSE.txt'), (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') -Destination (Join-Path $packageRoot 'runtime')
Copy-Item -LiteralPath (Join-Path $dotnetRoot "host\fxr\$version") -Destination (Join-Path $packageRoot 'runtime\host\fxr') -Recurse
Copy-Item -LiteralPath (Join-Path $coreRoot $version) -Destination (Join-Path $packageRoot 'runtime\shared\Microsoft.NETCore.App') -Recurse
Copy-Item -LiteralPath (Join-Path $desktopRoot $version) -Destination (Join-Path $packageRoot 'runtime\shared\Microsoft.WindowsDesktop.App') -Recurse
Copy-Item -Path (Join-Path $projectRoot 'Agent部署包\*') -Destination $packageRoot -Recurse
Move-Item -LiteralPath (Join-Path $packageRoot 'README.md') -Destination (Join-Path $packageRoot 'DEPLOYMENT-README.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'), (Join-Path $projectRoot 'MCP.md') -Destination $packageRoot
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($packageRoot, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)
Write-Host "ZIP 已生成：$zipPath"
Write-Host "部署包已生成：$packageRoot（运行时 $version）"
