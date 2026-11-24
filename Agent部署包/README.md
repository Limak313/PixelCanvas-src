# PixelCanvas 本地 MCP 部署包

此包适用于 **Windows x64 桌面**，包含 PixelCanvas 程序、标准输入输出（stdio）MCP 服务。客户端须支持启动本机 stdio MCP 服务，并与画布运行在同一台 Windows 电脑的同一用户会话中；远程或仅支持 HTTP 的客户端不能直接使用此包。

包有两种形态，文件位置一致，按 `server` 目录判断属于哪种：

- **精简包**（`server\PixelCanvas.Mcp.exe`）：桌面程序与服务都是框架依赖的单文件 exe，压缩后约 1 MB。需要该电脑已安装 **.NET 10 Desktop Runtime**（[下载](https://dotnet.microsoft.com/download/dotnet/10.0)）；未安装时启动会提示安装运行时。
- **自包含包**（`runtime\dotnet.exe` + `server\PixelCanvas.Mcp.dll`）：随包携带 .NET 10 桌面运行时，免安装，体积大（解压后约 170 MB）。

1. 将整个文件夹解压到有写入权限的固定位置，保留 `app` 和 `server`（自包含包还有 `runtime`）的相对位置。
2. 在该文件夹内打开 PowerShell，执行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\生成客户端配置.ps1`。脚本生成 `mcp-config.json`，其中是该电脑的绝对路径。移动部署包后需要重新生成配置。
3. 将生成文件里的 `mcpServers.pixelcanvas` 配置合并到 Agent 客户端的 MCP 配置中，不要覆盖客户端已有的其他服务。具体配置文件位置和格式由客户端决定；完成后重新启动客户端。
4. 在 Agent 中请求“列出 PixelCanvas 画布”。也可以请求“打开一个 32×32 的 PixelCanvas 画布”。客户端会自动启动 MCP 服务，无需长期打开终端。也可双击 `启动画布.cmd` 手动打开画布。

输出默认写入部署包的 `output` 目录下，按动画、PNG 和工程分类。预设和自动恢复信息保存在当前用户的本地应用数据目录。Windows 防火墙不需要开放端口：服务通过本机标准输入输出和当前用户命名管道通信。遇到恢复提示时需先在画布窗口处理。

如果 Agent 不支持 stdio MCP、无法启动本地进程，或者在另一台电脑／容器内运行，需要该 Agent 自己提供适配；此包不会自动提供网络服务。请勿从普通终端手工运行 MCP 服务后期望看到窗口：它等待客户端发来的协议消息。

精灵表、GIF、参考图、图层、调色板和动画工具的参数可由客户端通过 MCP `tools/list` 获取。详细说明见 `MCP.md`。自包含包中的运行时许可证和第三方声明随包提供。
