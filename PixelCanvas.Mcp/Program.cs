using System.Threading;
using PixelCanvas;

// 供安装器检测“MCP 服务仍在运行”，避免升级时替换正在被加载的 exe。
using var appMutex = new Mutex(true, @"Local\PixelCanvas.Running", out _);

await McpBridge.RunAsync();
