$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$runtime = Join-Path $root 'runtime\dotnet.exe'
$serverDll = Join-Path $root 'server\PixelCanvas.Mcp.dll'
$serverExe = Join-Path $root 'server\PixelCanvas.Mcp.exe'
# 自包含单文件包与精简包都直接启动 server\PixelCanvas.Mcp.exe；runtime 目录只保留许可证时忽略。
if (Test-Path -LiteralPath $runtime -PathType Leaf) {
    if (!(Test-Path -LiteralPath $serverDll -PathType Leaf)) { throw '部署包不完整，请保留 runtime、server 和 app 文件夹。' }
    $command = $runtime
    $serverArgs = @($serverDll)
} elseif (Test-Path -LiteralPath $serverExe -PathType Leaf) {
    $command = $serverExe
    $serverArgs = @()
} else {
    throw '部署包不完整：缺少 server\PixelCanvas.Mcp.exe。'
}
$output = Join-Path $root 'output'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$config = @{ mcpServers = @{ pixelcanvas = @{ command = $command; args = $serverArgs; env = @{ PIXELCANVAS_OUTPUT_DIR = $output } } } }
$target = Join-Path $root 'mcp-config.json'
if (Test-Path -LiteralPath $target) { throw "配置文件已存在：$target。请先备份或删除后重新生成。" }
$json = $config | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($target, $json, [Text.UTF8Encoding]::new($false))
Write-Host "配置已生成：$target"
Write-Host '将 pixelcanvas 配置合并到你的 MCP 客户端配置中，然后重启该客户端。'
