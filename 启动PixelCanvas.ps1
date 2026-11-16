$ErrorActionPreference = 'Stop'

try {
    $codex = Get-Command codex.exe -ErrorAction SilentlyContinue
    if (-not $codex) { $codex = Get-Command codex.cmd -ErrorAction Stop }
    $json = & $codex.Source plugin list --marketplace pixelcanvas-local --json 2>$null
    if ($LASTEXITCODE -ne 0) { throw '无法读取 PixelCanvas 插件安装信息，请先启动 Codex 检查插件。' }
    $list = ($json -join "`n") | ConvertFrom-Json
    $plugin = @($list.installed | Where-Object { $_.pluginId -eq 'pixelcanvas-desktop@pixelcanvas-local' -and $_.enabled }) | Select-Object -First 1
    if (-not $plugin) { throw '未找到已启用的 PixelCanvas Desktop 插件。' }
    $version = [string]$plugin.version
    if ($version -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { throw '插件版本信息无效。' }
    $codexRoot = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
    $app = Join-Path $codexRoot "plugins\cache\pixelcanvas-local\pixelcanvas-desktop\$version\app\PixelCanvas.exe"
    if (-not (Test-Path -LiteralPath $app -PathType Leaf)) { throw "找不到插件 $version 的画布程序，请重新安装插件。" }
    Start-Process -FilePath $app -WorkingDirectory (Split-Path -Parent $app)
}
catch {
    Add-Type -AssemblyName System.Windows.Forms
    [void][System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'PixelCanvas 启动失败', 'OK', 'Error')
}
