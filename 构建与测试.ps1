# 一键 Release 全量构建并运行核心测试。
# 默认只跑核心测试（排除 Category=InteractiveUI 的真交互冒烟用例）。
# 加 -Ui 会在核心测试后再跑交互组，要求当前会话可交互（能在本机桌面上创建窗体句柄）。
# 加 -Coverage 会在核心测试时收集覆盖率，并在结束时打印行/分支覆盖率（HTML 报告在 coverage\report）。
# 在仓库根目录执行：powershell -NoProfile -ExecutionPolicy Bypass -File .\构建与测试.ps1 [-Ui] [-Coverage]
param(
    [switch]$Ui,
    [switch]$Coverage
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'PixelCanvas.Tests\PixelCanvas.Tests.csproj'
$coverageDir = Join-Path $root 'coverage'

Write-Host '==> 还原并 Release 构建解决方案' -ForegroundColor Cyan
dotnet build (Join-Path $root 'PixelCanvas.slnx') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw '解决方案构建失败。' }

if ($Coverage) {
    if (Test-Path -LiteralPath $coverageDir) { Remove-Item -LiteralPath $coverageDir -Recurse -Force }
    Write-Host '==> 运行核心测试并收集覆盖率（排除 InteractiveUI）' -ForegroundColor Cyan
    dotnet test $proj -c Release --no-build --nologo --filter 'Category!=InteractiveUI' --collect:'XPlat Code Coverage' --results-directory $coverageDir
    if ($LASTEXITCODE -ne 0) { throw '核心测试未通过。' }
    $xmlFile = Get-ChildItem $coverageDir -Recurse -Filter 'coverage.cobertura.xml' | Select-Object -First 1
    if ($xmlFile) {
        [xml]$cov = Get-Content -LiteralPath $xmlFile.FullName
        $line = [math]::Round([double]$cov.coverage.'line-rate' * 100, 1)
        $branch = [math]::Round([double]$cov.coverage.'branch-rate' * 100, 1)
        Write-Host ("覆盖率：行 {0}%（{1}/{2}），分支 {3}%" -f $line, $cov.coverage.'lines-covered', $cov.coverage.'lines-valid', $branch) -ForegroundColor Green
        if (Get-Command reportgenerator -ErrorAction SilentlyContinue) {
            reportgenerator "-reports:$($xmlFile.FullName)" "-targetdir:$(Join-Path $coverageDir 'report')" -reporttypes:Html | Out-Null
            Write-Host "HTML 报告：$(Join-Path $coverageDir 'report\index.html')" -ForegroundColor Green
        }
    }
} else {
    Write-Host '==> 运行核心测试（排除 InteractiveUI）' -ForegroundColor Cyan
    dotnet test $proj -c Release --no-build --nologo --filter 'Category!=InteractiveUI'
    if ($LASTEXITCODE -ne 0) { throw '核心测试未通过。' }
}

if ($Ui) {
    if (-not [Environment]::UserInteractive) {
        throw '当前会话不可交互（UserInteractive=false），无法运行 UI 冒烟用例。请在本机登录桌面会话后重试。'
    }
    Write-Host '==> 运行交互冒烟用例（Category=InteractiveUI，需要交互式桌面）' -ForegroundColor Cyan
    Write-Host '    前置条件：本机已登录桌面；运行期间会反复创建窗体句柄，请勿锁屏或切换到无桌面会话；150% 缩放下才能复现高 DPI 路径。' -ForegroundColor DarkGray
    dotnet test $proj -c Release --no-build --nologo --filter 'Category=InteractiveUI'
    if ($LASTEXITCODE -ne 0) { throw '交互冒烟用例未通过。' }
}

Write-Host '全部构建与测试通过。' -ForegroundColor Green
