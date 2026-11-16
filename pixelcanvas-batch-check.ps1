$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot 'PixelCanvas-Agent-1.0.35-parentdir-20261105'
$env:DOTNET_ROOT = Join-Path $root 'runtime'
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$app = Start-Process -FilePath "$root\app\PixelCanvas.exe" -ArgumentList '4','4' -PassThru
$server = [Diagnostics.Process]::new()
$server.StartInfo.FileName = Join-Path $root 'runtime\dotnet.exe'
$server.StartInfo.ArgumentList.Add("$root\server\PixelCanvas.Mcp.dll")
$server.StartInfo.UseShellExecute = $false
$server.StartInfo.RedirectStandardInput = $true
$server.StartInfo.RedirectStandardOutput = $true
$server.StartInfo.CreateNoWindow = $true
[void]$server.Start()
try {
 $server.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}')
 [void]$server.StandardOutput.ReadLine()
 $server.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
 $server.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list"}')
 $list = $server.StandardOutput.ReadLine() | ConvertFrom-Json
 if (-not ($list.result.tools.name -contains 'pixelcanvas_draw') -or -not ($list.result.tools.name -contains 'pixelcanvas_files')) { throw 'missing tools' }
 Start-Sleep -Seconds 2
 $req = @{jsonrpc='2.0';id=3;method='tools/call';params=@{name='pixelcanvas_draw';arguments=@{action='canvas_batch_paint';arguments=@{process_id=$app.Id;operations=@(@{type='paint';pixels=@(@{x=0;y=0;color='#FF0000'})},@{type='shape';shape='line';x1=0;y1=1;x2=3;y2=1;color='#00FF00'})}}}} | ConvertTo-Json -Depth 15 -Compress
 $server.StandardInput.WriteLine($req)
 $answer = $server.StandardOutput.ReadLine() | ConvertFrom-Json
 if ($answer.result.isError) { throw $answer.result.content[0].text }
 $req = @{jsonrpc='2.0';id=4;method='tools/call';params=@{name='pixelcanvas_draw';arguments=@{action='canvas_read_pixels';arguments=@{process_id=$app.Id;x=0;y=0;width=1;height=2}}}} | ConvertTo-Json -Depth 10 -Compress
 $server.StandardInput.WriteLine($req)
 $answer = $server.StandardOutput.ReadLine() | ConvertFrom-Json
 if ($answer.result.isError -or $answer.result.content[0].text -notmatch '#FF0000' -or $answer.result.content[0].text -notmatch '#00FF00') { throw "bad read: $($answer.result.content[0].text)" }
 'PASS MCP 工具发现、跨进程批量绘画与读取'
} finally { if (!$server.HasExited) {$server.Kill()}; if (!$app.HasExited) {$app.Kill()} }
