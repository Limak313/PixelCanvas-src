# 一键 Release 全量构建并运行核心测试（不含 --ui 交互分支）。
# 在仓库根目录执行：powershell -NoProfile -ExecutionPolicy Bypass -File .\构建与测试.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Write-Host '==> 还原并 Release 构建解决方案' -ForegroundColor Cyan
dotnet build (Join-Path $root 'PixelCanvas.slnx') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw '解决方案构建失败。' }

Write-Host '==> 运行核心测试（dotnet test，UI 冒烟用例默认跳过）' -ForegroundColor Cyan
dotnet test (Join-Path $root 'PixelCanvas.Tests\PixelCanvas.Tests.csproj') -c Release --no-build --nologo
if ($LASTEXITCODE -ne 0) { throw '测试未通过。' }

Write-Host '全部构建与核心测试通过。' -ForegroundColor Green
