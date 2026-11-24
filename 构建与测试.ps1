# 一键 Release 全量构建并运行核心测试。
# 默认只跑核心测试（排除 Category=UI 的交互式冒烟用例）。
# 加 -Ui 会在核心测试后再跑 UI 组，要求当前会话可交互（能在本机桌面上创建窗体句柄）。
# 在仓库根目录执行：powershell -NoProfile -ExecutionPolicy Bypass -File .\构建与测试.ps1 [-Ui]
param(
    [switch]$Ui
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'PixelCanvas.Tests\PixelCanvas.Tests.csproj'

Write-Host '==> 还原并 Release 构建解决方案' -ForegroundColor Cyan
dotnet build (Join-Path $root 'PixelCanvas.slnx') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw '解决方案构建失败。' }

Write-Host '==> 运行核心测试（排除 Category=UI）' -ForegroundColor Cyan
dotnet test $proj -c Release --no-build --nologo --filter 'Category!=UI'
if ($LASTEXITCODE -ne 0) { throw '核心测试未通过。' }

if ($Ui) {
    if (-not [Environment]::UserInteractive) {
        throw '当前会话不可交互（UserInteractive=false），无法运行 UI 冒烟用例。请在本机登录桌面会话后重试。'
    }
    Write-Host '==> 运行 UI 冒烟用例（Category=UI，需要交互式桌面）' -ForegroundColor Cyan
    Write-Host '    前置条件：本机已登录桌面；运行期间会反复创建窗体句柄，请勿锁屏或切换到无桌面会话；150% 缩放下才能复现高 DPI 路径。' -ForegroundColor DarkGray
    dotnet test $proj -c Release --no-build --nologo --filter 'Category=UI'
    if ($LASTEXITCODE -ne 0) { throw 'UI 冒烟用例未通过。' }
}

Write-Host '全部构建与测试通过。' -ForegroundColor Green
