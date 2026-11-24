# PixelCanvas 像素画布

## 功能简介

原生 Windows 桌面像素画工具（C# WinForms / .NET 10），支持桌面插件与本地 MCP：

- 画布：默认 32×32；预设 8/16/32/64/128/256/512；支持自定义 4–512（可非正方形），透明格显示棋盘格。
- 工具：画笔（B）、橡皮（E）、取色器（I）、油漆桶（G）、渐变（D），以及直线、矩形、椭圆和选区；画笔大小 1–4，右键可快速取色。
- 渐变：当前颜色为起始色，可设置结束色并交换两色；在连续同色区域内拖动确定方向和范围，支持镜像与撤销。
- 本地 MCP：支持连接正在运行的画布或操作工程文件，配置和工具说明见 [MCP.md](MCP.md)。
- 调色板：内置 32 色 DawnBringer（DB32）+ 自定义颜色（系统原生取色器）。
- 对称绘制：水平镜像 / 垂直镜像独立开关，可同时开启（一点对称 4 点）。
- 图层：最多 32 层，支持新建、删除、调整顺序和显示 / 隐藏；隐藏的图层不能绘制。
- 帧动画：底部可新增、复制、删除帧，导入同画布尺寸的 PNG、JPEG 或 BMP 作为新帧，并以 1–60 帧/秒循环播放；每帧拥有独立图层。最多 128 帧，多帧总图层像素不超过 2200 万。导入的半透明像素会转换为透明或不透明。
- 动画时间轴：点击缩略图切换帧，拖动或使用左右按钮调整顺序（排序可用 Ctrl+Z 撤销）；可开启洋葱皮，以红色和蓝色显示前后帧。洋葱皮仅供编辑参考，播放及导出时不显示。
- 精灵表：动画栏点击“精灵表”，设置每行帧数，按当前导出放大倍数将全部帧依次导出为透明 PNG 网格。
- 导出预设：右侧“导出设置”可保存和复用 PNG、GIF、精灵表的缩放、GIF 帧率、精灵表列数及输出目录；按预设导出会创建新文件，GIF 预设帧率不修改工程帧率。
- 颜色管理：可替换当前帧当前图层中的一种颜色（可撤销），并删除调色板颜色、保存及加载 `.pxpal` 色板；调色板随 `.pxproj` 工程保存。
- 撤销 / 重做：Ctrl+Z / Ctrl+Y（或 Ctrl+Shift+Z），每个图层最多 100 步，一整笔一步；所有图层的历史共用 64 MiB 上限，超限时最早的记录会被移除。结构与像素共用同一条动作链：帧的新增、复制、导入、删除、排序、批量加帧，以及图层的新增、删除、排序、显隐切换各记一步结构快照（最多 32 步，按网格估算占用另设 64 MiB 预算）；撤销按全局动作先后推进，命中非当前帧时自动切换过去。
- 工程保存 / 载入：Ctrl+S / Ctrl+O，`.pxproj`（UTF-8 JSON，按行 RLE 像素编码）；多帧工程保存全部帧、图层和帧率，并可读取旧版单帧工程。旧版程序无法打开多帧工程。
- 工程文件上限：256 MiB，调色板最多 1024 色；超限时会提示错误并保留现有文件。旧版工程同样适用此上限。
- 自动保存与恢复：未保存的修改每分钟写入独立恢复副本；异常退出后下次启动可恢复，恢复后需手动保存工程。
- 参考图：打开本地图片作为画布底图，可调整不透明度和显示状态；参考图不会保存在工程中或出现在导出的 PNG 中。
- 沉浸画图：F11 进入或退出全屏画布，Esc 退出；F1 查看全部快捷键，F2 查看当前画布和工具状态。
- 导出 PNG：1×–16× 最近邻放大，无网格线、透明背景保留。
- 导出 GIF：多帧画布的导出对话框默认选择循环 GIF，保存到动画文件夹；支持透明背景和 1×–16× 最近邻放大。GIF 最多使用 255 种不透明颜色，超过时会量化。普通工程保存仍为可编辑的 `.pxproj`。
- 其他：网格线开关、悬停笔刷高亮、整数倍自动缩放（高 DPI 支持）。

## 构建命令

需要 .NET 10 SDK（可在 [dotnet.microsoft.com](https://dotnet.microsoft.com) 下载，或 `winget install Microsoft.DotNet.SDK.10`）。版本号由根目录 `Directory.Build.props` 集中管理，桌面程序、MCP 服务与插件清单对齐。

一键 Release 全量构建并运行核心测试（默认排除交互式 UI 冒烟组）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\构建与测试.ps1
```

连交互式 UI 冒烟组一起跑（需要已登录的桌面会话）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\构建与测试.ps1 -Ui
```

或手动分步执行：

```powershell
# 全量构建解决方案（PixelCanvas / PixelCanvas.Mcp / PixelCanvas.Tests）
dotnet build PixelCanvas.slnx -c Release

# 运行无头核心测试（排除 UI 组）
dotnet test PixelCanvas.Tests/PixelCanvas.Tests.csproj -c Release --filter "Category!=UI"

# 运行交互式 UI 冒烟组（需桌面会话；150% 缩放下才能复现高 DPI 路径）
dotnet test PixelCanvas.Tests/PixelCanvas.Tests.csproj -c Release --filter "Category=UI"
```

测试基于 xUnit：工程读写与格式兼容、MCP 路由、导出（GIF/精灵表/路径分类）、原子写入、绘画算法、调色板为无头可运行的核心用例；`UiSmokeTests` 的 9 条界面路径用例按环境依赖分两组：7 条只要求能创建窗体句柄，标 `Category=UiHost`，随默认门禁进入 CI；2 条依赖播放定时器消息泵与时间轴坐标映射（`FrameHistory_SwitchFrameUndoAndPlayback`、`AnimationFrames_OnionSkin_ReorderAndTimelineDrag`），标 `Category=UI`，在本机用 `-Ui` 执行。`FrameUndoTests`、`LayerUndoTests` 与 `UiHostProbeTests` 同属 `Category=UiHost`，分别覆盖帧与图层的结构撤销、以及运行环境能否承载界面用例。当前门禁 95 条，本机 UI 组 2 条。

## 发布打包

打包脚本 `打包本地MCP.ps1` 先按 Release 构建，再把产物组装成携带 .NET 10 桌面运行时的自包含部署包并生成 zip：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\打包本地MCP.ps1
```

- 包名默认按 `Directory.Build.props` 的版本号生成（如 `PixelCanvas-Agent-1.0.36-win-x64`）；可用 `-PackageName` 覆盖。
- 默认会把 Release 产物同步刷新到插件目录 `pixelcanvas-plugin\plugins\pixelcanvas-desktop-v*\app` 与 `server`（排除 pdb）；`-NoPlugin` 可跳过刷新，`-NoPackage` 只刷新插件不生成部署包。
- 生成的部署包目录、`.zip`、`output/` 均在 `.gitignore` 中，不纳入版本管理。
- 部署包产出后，可用 `pixelcanvas-batch-check.ps1 -PackageRoot <部署包目录>` 做一次跨进程 MCP 绘画与读取校验。

## 运行方法

双击桌面的“PixelCanvas 新版”快捷方式，或运行仓库根目录的 `启动PixelCanvas.ps1`。快捷方式打开已安装的 PixelCanvas Desktop 插件所附带的画布，需安装 .NET 10 桌面运行时。本地 MCP 使用仅限当前用户的命名管道，不监听网络端口。

## 许可

MIT License，见 [`LICENSE`](LICENSE)。可自由使用、修改、分发与商用，保留版权声明即可。第三方依赖（.NET 运行时、WinForms）遵循其各自许可。
