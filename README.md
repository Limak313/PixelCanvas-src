# PixelCanvas 像素画布

## 功能简介

原生 Windows 桌面像素画工具（C# WinForms / .NET 8），免安装、双击即用：

- 画布：默认 16×16；预设 8/16/32/64/128；支持自定义 4–256（可非正方形），透明格显示棋盘格。
- 工具：画笔（B）、橡皮（E）、取色器（I）、油漆桶（G），画笔大小 1–4；画布上右键随时快速取色。
- 调色板：内置 32 色 DawnBringer（DB32）+ 自定义颜色（系统原生取色器）。
- 对称绘制：水平镜像 / 垂直镜像独立开关，可同时开启（一点对称 4 点）。
- 撤销 / 重做：Ctrl+Z / Ctrl+Y（或 Ctrl+Shift+Z），最多 100 步，一整笔一步。
- 工程保存 / 载入：Ctrl+S / Ctrl+O，`.pxproj`（UTF-8 JSON，按行 RLE 像素编码）。
- 导出 PNG：1×–16× 最近邻放大，无网格线、透明背景保留。
- 其他：网格线开关、悬停笔刷高亮、整数倍自动缩放（高 DPI 支持）。

## 构建命令

需要 .NET SDK（可在 [dotnet.microsoft.com](https://dotnet.microsoft.com) 下载，或 `winget install Microsoft.DotNet.SDK.8`）。

```powershell
cd D:\像素画布\PixelCanvas
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## 运行方法

双击 `D:\像素画布\PixelCanvas\publish\PixelCanvas.exe` 即可。自包含单文件发布，运行端无需安装 .NET；程序运行期间不进行任何网络访问。
