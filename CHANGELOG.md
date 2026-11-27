# 更新日志

本项目遵循语义化版本；版本号由根目录 `Directory.Build.props` 集中管理，桌面程序、MCP 服务与插件清单对齐同一版本。

## [未发布]

### 变更
- 安装器签名覆盖主程序 `PixelCanvas.exe`、MCP 入口 `PixelCanvas.Mcp.exe` 与 Setup.exe，不再只签安装包外壳。
- 支持 PFX、本机证书指纹与 CI Base64 Secret 三种证书来源；配置签名后 Release 工作流强制要求 Authenticode 有效。
- 新增 `安装包\创建测试证书.ps1`，可生成开发用自签名证书；私钥落在 `.signing\` 并被 `.gitignore` 排除。
- 验证脚本增加已安装主程序/MCP 的签名者一致性检查。
- 清理 1.0.38–1.0.45 本地旧安装包、旧 zip、重复解包/构建目录，以及不再被 marketplace 引用的旧插件版本；仅保留 1.0.46 当前产物。

## [1.0.46] - 2026-11-27

### 变更
- 安装器改为简体中文优先、英文可选的双语界面，补齐中文开始菜单项、README/MCP 说明与卸载入口。
- 安装包写入 FileDescription、ProductName、CompanyName、ProductVersion、Copyright 等版本信息；限制 Windows 10 及以上，保持当前用户安装。
- 升级时沿用上次安装目录、语言与快捷方式选项；注册 `App Paths\PixelCanvas.exe`，可直接从“运行”启动。
- 桌面程序与 MCP 入口创建 `Local\PixelCanvas.Running` 互斥量标记，安装器通过 `AppMutex` 检测运行中的程序/服务，升级前提示关闭。
- 安装脚本可在存在 PFX 时调用 `signtool` 做 SHA-256 时间戳签名并验证 Authenticode；无论是否签名都生成 `.sha256` 侧车文件。
- 新增 `安装包\验证安装包.ps1`：校验版本信息、签名、SHA-256，并在临时目录做静默安装/卸载与 App Paths、卸载注册项检查。

### 测试
- 无头门禁 150 条（Core 86 + Windows 64）与本机交互组 2 条继续通过。
- x64 安装包静默安装/卸载生命周期通过；版本信息、App Paths、卸载注册项和 SHA-256 均按预期清理。

## [1.0.45] - 2026-11-27

### 变更
- 新增 `UiInteractionTests` 8 条控件事件层用例：直接向 `CanvasControl` 发送鼠标消息并调用 `MainForm.OnKeyDown`，覆盖点击/拖拽绘画、右键取色、双轴镜像、选区拖拽与移动、工具快捷键、镜像/网格/笔刷快捷键、Ctrl+Z/Y、F11/Esc，不需要移动真实鼠标。
- 新增 `CanvasGoldenTests` 7 张画布 golden：只比对 `CanvasControl.NativeBitmap` 的 RGBA，固定 16×16 与 1× 缩放，避开窗口 DPI、主题和网格线；基线为 `PixelCanvas.Tests/TestData/Golden/*.png`，用 `PIXELCANVAS_UPDATE_GOLDEN=1` 重建。
- 新增 `UiMessagePumpTests.PlaybackTimer_AdvancesUnderApplicationRun`：用 `Application.Run` + 6 秒看门狗跑真实消息循环，和原 `DoEvents` 用例形成两条 `InteractiveUI` 烟测。
- CI 新增非阻塞 `ui-probe`：在 `windows-latest` 连跑 20 次 `Category=InteractiveUI` 并归档 trx；全绿前不把该组设成 PR 阻塞门禁。

### 测试
- 无头门禁 150 条（Core 86 + Windows 64）全部通过，其中 `Category=UiHost` 40 条。
- 本机交互组 2 条通过，单次约 1–2 秒。
- 覆盖率提升到行 **65.0%**、分支 **46.5%**。

## [1.0.44] - 2026-11-24

### 变更
- 播放帧推进从 WinForms 定时器回调抽成 `MainForm.AdvancePlayback(elapsedMs)`，定时器只提供耗时；测试可直接注入时间，不再依赖 `Thread.Sleep` 与消息泵。
- 时间轴落点判断抽成 `FrameTimeline.ResolveDrop(...)` 纯函数，`OnMouseUp` 只负责读取坐标并派发；拖拽阈值增加最小 1 像素保护。
- 测试分类改名：`Category=UI` → `Category=InteractiveUI`。依赖窗体句柄但不依赖消息泵的用例全部回到 `Category=UiHost` 随 CI 运行；本机真交互冒烟从 2 条缩至 1 条（`PlaybackTimer_AdvancesFrameOnRealMessagePump`）。
- 新增 `FrameTimelineDropTests` 6 条纯函数用例，覆盖跨帧拖拽、右边界钳制、同格点击、阈值不足、无效按下帧。

### 测试
- 无头门禁 135 条（Core 86 + Windows 49）全部通过；本机交互组 1 条通过。
- 合并覆盖率提升到行 **64.3%**、分支 **46.4%**。

## [1.0.43] - 2026-11-24

### 变更
- MCP 按平台裁剪工具面：Windows 保持 8 个分组工具；Linux/macOS 因没有实时画布宿主，`tools/list` 只暴露 `pixelcanvas_project` 与 `pixelcanvas_tool_info`，`tool_info` 的动作枚举也只保留 `project_*`，避免模型先选中必然失败的实时画布工具。
- 初始化说明按平台区分：无头平台明确提示实时画布仅支持 Windows。
- 直接调用被隐藏的实时画布工具仍返回明确的 Windows 平台错误，保持旧客户端兼容。

### 测试
- 工具清单、动作枚举与 `tool_info` 按平台断言；Linux 冒烟新增“只返回工程工具”和“实时动作被平台拒绝”检查。

## [1.0.42] - 2026-11-24

### 变更
- MCP 动作发现优化：8 个分组工具的描述从“逐条动作长说明”改为“用途 + 动作名 + 示例”，`tools/list` 从约 24,000 字符降到约 13,400 字符（约 44%），完整参数与限制仍由 `pixelcanvas_tool_info` 按需提供。
- 动作路由容错：省略 `canvas_` / `project_` 前缀且唯一匹配时直接执行（如 `export_svg`）；多义时返回全部候选并要求带前缀（如 `export_png`）。
- 错误提示更具体：动作属于别的分组时直接提示应调用的工具；动作拼错时返回最多 3 个最接近的候选。`pixelcanvas_tool_info` 同样支持唯一别名与拼写建议。
- `arguments` 的公共字段（`path`、`output_path`、`x/y/width/height`、`color`、`scale`、`frame`、`layer`、`process_id`、`confirm`）写入 schema 描述，减少不必要的参数查询。

### 测试
- 新增唯一别名、歧义候选、跨分组重定向、拼写建议与 `tool_info` 别名用例；核心测试增至 85 条，合并覆盖率门禁覆盖两套无头测试。

## [1.0.41] - 2026-11-24

### 新增
- Windows ARM64 覆盖：`打包本地MCP.ps1` 新增 `-Rid win-x64|win-arm64`，可在 x64 机器上交叉产出 ARM64 的精简包、自包含包与 `PixelCanvas-<version>-win-arm64-setup.exe`。Inno Setup 按目标架构写入 `ArchitecturesAllowed` / `ArchitecturesInstallIn64BitMode`，安装包不再只接受 x64。

### 变更
- 自包含包改为单文件：`app\PixelCanvas.exe` 与 `server\PixelCanvas.Mcp.exe` 各自携带 .NET 10 运行时，不再解包共享的 `runtime\dotnet.exe` 运行时目录；`runtime` 只保留 .NET 许可证与第三方声明。解压体积约 180–200 MB，压缩包约 74–77 MB。
- 打包脚本与安装包脚本接受 `-Rid`，输出文件名自动带上 `win-x64` / `win-arm64`；发布工作流同时构建两种架构的 setup、slim 与 Agent 包。Agent 插件仍为 x64。

### 修复
- 修正 `.gitignore` 的 `PixelCanvas-*/` 规则：根目录包目录仍忽略，但不再误伤 `pixelcanvas-plugin\plugins\pixelcanvas-desktop-v*`。
- `pixelcanvas-batch-check.ps1` 的 `DOTNET_ROOT_<ARCH>` 改为按实际架构设置，不再只写 `X64`。

### 测试
- x64 与 arm64 包均已交叉构建并通过 PE 头校验（app / server 为 `0xAA64`）；x64 自包含包通过跨进程 MCP 绘画与读取检查。arm64 包需在 ARM 设备上执行运行期检查。

## [1.0.40] - 2026-11-24

### 变更
- 架构拆分：新增 `PixelCanvas.Core`（`net10.0`），迁入像素模型、工程读写、撤销历史、GIF/SVG/PNG/精灵表/序列帧导出、预设、路径与 MCP 画布模型共 24 个文件；`PixelCanvas.Mcp` 同步改为 `net10.0`，只引用 Core，不再依赖 WinForms。Windows 桌面界面保持原样。
- 核心库去掉 `System.Drawing`：PNG 改为手写 RGBA8 编码（CRC32 + `ZLibStream`），精灵表改为像素数组拼装后复用同一编码器；GIF/SVG 仍为手写编码。核心库因此可在 Linux/macOS 构建。
- 输出根目录不再写死 `D:\像素画布`：优先 `PIXELCANVAS_OUTPUT_DIR`，其次当前目录含 `PixelCanvas.slnx` 时使用 `output`，否则回退到用户文档目录的 `PixelCanvas\output`。
- MCP 实时画布宿主在非 Windows 平台直接返回明确错误，文件级 `project_*` 工具与无头导出仍可用；启动路径与运行时目录改为按平台和架构推导，不再只认 x64。

### 工程化
- 测试按依赖拆分：新增 `PixelCanvas.Core.Tests`（`net10.0`，可在 Linux 运行，75 条），`PixelCanvas.Tests` 保留界面与位图相关用例（40 条 + 2 条 UI）。合计 117 条，本机全通过。
- CI 新增 Linux job：在 `ubuntu-latest` 构建 Core / MCP 并运行核心测试；Windows job 与发布门禁改为运行整个解决方案的测试。
- 当前基线：门禁 115 条，覆盖率行 58.0%、分支 38.2%（CI 下限仍为 55% / 38%）。

## [1.0.39] - 2026-11-24

### 新增
- GIF 索引色自定义：导出前可选自动调色板或工程调色板；工程调色板 1–255 色按顺序写入全局色表，透明固定索引 0，画面外颜色映射到最近色。MCP 的 `canvas_export_gif` / `project_export_gif` 新增 `palette` 与 `use_project_palette` 参数。

### 测试
- 新增固定索引色写入顺序、最近色映射、非法色板拒绝与工程调色板导出用例；门禁 115 条，覆盖率行 62.3%、分支 43.4%。

## [1.0.38] - 2026-11-24

### 新增
- 自包含安装包：`安装包\生成安装包.ps1` 复用 Bundle 部署包，用 Inno Setup 产出 `PixelCanvas-<version>-win-x64-setup.exe`（约 52 MB）。按用户安装到 `%LOCALAPPDATA%\Programs\PixelCanvas`，无需管理员与预装 .NET，创建开始菜单 / 桌面快捷方式，卸载不碰用户工程；`release.yml` 随版本自动构建并上传。

### 工程化
- 覆盖率统计接入：测试项目加 `coverlet.collector`，CI 采集 Cobertura 报告、生成 HTML 报告并作为构建产物上传；`构建与测试.ps1` 新增 `-Coverage` 开关，本地可直接查看行/分支覆盖率。
- CI 增加覆盖率下限：行 55%、分支 38%，低于下限直接失败，防止后续改动悄悄削薄测试。

### 测试
- 基线覆盖率：行 62.2%（2404/3866）、分支 42.8%。

## [1.0.37] - 2026-11-24

### 新增
- 图层结构可撤销：新增、删除、调整顺序、切换显隐各记一步结构快照，与帧结构共用同一条动作链和同一 64 MiB 预算；`canvas_undo` / `canvas_redo` 不传 `layer` 时自动选择最新的像素或结构改动。
- MIT 许可证（`LICENSE`），README 增加许可章节。
- 撤销历史随恢复副本落盘：自动保存时把各图层的像素撤销 / 重做栈写入同目录 `.undo` 文件（gzip + 行 RLE，每层最多 20 步），异常退出后恢复作品时连同 Ctrl+Z 历史一并接回；文件损坏或与工程不符（尺寸、帧数、图层数）时整份丢弃，不影响作品载入。
- 导出 SVG 矢量色块（`canvas_export_svg`，界面导出对话框同步支持 `.svg`）：当前帧可见图层按行 RLE 后再纵向合并同一列区间，写出 `crispEdges` 的 `<rect>` 列表，透明像素留空；矢量缩放不失真，适合排版与印刷。
- 导出 PNG 序列帧（`canvas_export_frame_sequence`，动画栏「序列帧」按钮）：全部帧逐张写成 `base-001.png`…到指定新目录，可选前缀与 1–16 倍缩放；目录已有文件时拒绝，不覆盖任何既有产物。

### 修复
- 导出与保存统一改为原子写入（`AtomicFile`：同目录临时文件 + 刷盘 + 整文件替换）：工程、GIF、精灵表、PNG、`.pxpal` 与导出预设都先写临时文件再替换。顺带修掉旧实现的隐藏问题——`FileMode.CreateNew` 失败后 `catch` 里会 `File.Delete(path)`，把用户已存在的导出文件删掉；现在目标文件保持原样。

### 变更
- 播放时钟改为按累计耗时反推帧号（`AnimationClock`）：WinForms 定时器精度约 15 ms，原先每 Tick 前进一帧会让 30/60 fps 越播越慢；现在播放速率与设定帧率一致，定时器只决定取样时机。播放中修改帧率会从当前帧重新计时。

### 工程化
- 结构撤销类由 `FrameUndo` 更名为 `StructureUndo`，快照从「帧数组 + 当前帧」扩为「每帧图层列表 + 当前图层 + 各层可见性与名称」。
- 部署包分两种形态：默认 `-Mode Slim` 用 `dotnet publish -p:PublishSingleFile` 产出框架依赖单文件包（压缩 1.1 MB / 10 个文件，原来是 73.4 MB / 496 个文件；需目标机装 .NET 10 桌面运行时），`-Mode Bundle` 保留携带运行时的自包含包（73.4 MB），`-Mode All` 两种都出；`release.yml` 同时上传两个 zip。
- `PixelCanvas.csproj` 显式声明 `<SelfContained>false</SelfContained>`，避免 MCP 服务以框架依赖单文件发布时引用可执行项目触发 NETSDK1151。
- `pixelcanvas-batch-check.ps1` 与 `Agent部署包\生成客户端配置.ps1` 同时支持两种包形态（按 `server` 内容判断走 `runtime\dotnet.exe` + dll，还是直接跑单文件 exe）。
- README 增加「界面预览」：主界面、18× 网格细节两张截图与一段 5 帧眨眼动画 GIF（`docs/images/`，由插件 MCP 实际绘制并截图导出）。
- CI 门禁加严：`dotnet format whitespace --verify-no-changes` 守住代码格式，构建带 `-warnaserror`；工作流限定 `contents: read` 权限、同分支并发自动取消、NuGet 缓存与 20 分钟超时。
- 新增 `.github/dependabot.yml`：NuGet 与 GitHub Actions 每周各聚合成一个 PR（minor/patch），减少逐包刷屏。
- `canvas_delete_layer` 与界面删除图层不再清空被删图层的历史，撤销后该层像素与其撤销记录一并恢复。

### 测试
- 门禁 74 → 112：新增 `AtomicFileTests` 7 条（原子写入、失败保护、覆盖语义）、`LayerUndoTests` 5 条（删/加/排序/显隐撤销、结构改动清空像素重做）、`AnimationClockTests` 9 条（帧号推进、长时间无漂移、起始帧偏移、轮询间隔边界）、`UndoJournalTests` 7 条（栈序还原、重做栈、尺寸/帧数/图层数不符拒绝、损坏或缺文件拒绝、步数截断、恢复后序号连续）、`ExportFormatsTests` 5 条（SVG 合并矩形与透明、像素数校验、可见图层合成、序列帧命名与缩放、非空目录与非法参数拒绝）、`McpExportFormatTests` 2 条（`export_svg` / `export_frames` 穿过分组层路由），另在 `McpRoutingTests` 补 3 条分组不变量（每个操作只属于一个入口、`tool_info` 枚举为并集、新增导出操作在分组调用下可分发）。

## [1.0.36] - 2026-11-24

### 新增
- 应用图标：exe 内嵌 Win32 图标资源，窗体标题栏从自身 exe 提取（异常时静默兜底）；打包脚本把 ico 同步到插件 `app` 与部署包 `app`；`<Content>` 复制规则保证 clean/rebuild 后图标不丢失。
- GitHub Actions 工作流两条：构建与测试（归档 trx 与 Release 产物）、Release 发布打包。
- 帧操作可撤销：新增、复制、导入、删除、排序与批量加帧各记一步快照（`FrameUndo`，最多 32 步，按网格估算另设 64 MiB 预算）；Ctrl+Z / Ctrl+Y 与 `canvas_undo` / `canvas_redo` 均可回退或恢复。

### 变更
- 撤销 / 重做按全局动作序号推进（图层像素历史与帧结构快照共用计数器）：撤销取最新动作，命中非当前帧时自动切换过去；`canvas_undo` / `canvas_redo` 不传 `layer` 时走该自动选择，传 `layer` 时仍只作用于指定图层。
- 默认输出根目录由 `input` 改为 `output`，并按类型分入子目录：`.pxproj` → `工程`、GIF 与精灵表 → `动画`、普通 PNG → `PNG`；环境变量 `PIXELCANVAS_OUTPUT_DIR` 仍可整体覆盖根目录。`README`、`MCP.md` 与插件技能说明同步。

### 规格上限
- 帧数上限 64 → 128。
- 动画总像素预算 1100 万 → 2200 万，工程文件上限 128 MiB → 256 MiB 配套放宽。
- 帧率上限 30 → 60。
- 以上校验统一贯通工程读写、GIF 与精灵表导出、导出预设、界面控件及 MCP schema；文件体积与帧率的报错文案改为按常量插值，避免上限调整后文案脱节。

### 修复
- 画布绘制改为整数像素对齐（`PixelOffsetMode.None`）并关闭网格线与选区抗锯齿，改善 150% 显示缩放下色块边缘发虚、网格观感不齐的问题。
- `open_canvas` 在静态复用前校验可复用性，避免连回已被修改的旧窗口。

### 测试
- 无头用例 48 → 60：新增帧数边界（128 帧往返、129 帧拒绝）、帧率边界（60 帧往返、61 帧拒绝），文件上限断言改为引用常量。
- 补 `reuse_info` 可复用判定回归用例。
- 9 条界面路径冒烟用例不再靠 `Skip` 沉底：整类标记 `[Trait("Category","UI")]`，CI 门禁与 `构建与测试.ps1` 默认按标签排除，本机 `-Ui`（或 `--filter "Category=UI"`）一键执行；本机实测 9 条全部通过。
- 新增 UI 宿主探针（`Category=UiHost`）：在 CI runner 上实测可创建 `MainForm` 句柄、可建画布控件句柄并 `DrawToBitmap`。
- 据探针结果分级迁移：7 条只依赖窗体句柄的界面用例改标 `Category=UiHost` 进入 CI 门禁，仅播放定时器（`FrameHistory_SwitchFrameUndoAndPlayback`）与时间轴拖拽（`AnimationFrames_OnionSkin_ReorderAndTimelineDrag`）保留 `Category=UI` 在本机执行。
- 关闭 xUnit 测试集合并行（`xunit.runner.json`）：`ExportTests` 与 `UiSmokeTests` 都会改写进程级 `PIXELCANVAS_OUTPUT_DIR`，并行时会出现拿到另一个临时目录的断言竞态（CI 首跑即暴露）；改串行后连跑稳定。
- 新增帧撤销用例 5 条（`Category=UiHost`）：删帧、加帧、移帧、批量加帧的撤销与重做，以及撤销优先取更新的帧改动；门禁合计 74 条，CI 实跑 74/74 通过，本机另跑 2 条纯界面用例。

### 工程化
- 插件目录名随版本改为 `pixelcanvas-desktop-v1036`，市场清单路径同步。
- 清理 20261105 历史自包含部署快照（3 组目录与 3 个 zip，合计约 735 MB）；`.gitignore` 移除已废弃的 `input/` 规则。

## [1.0.35] - 2026-11-16

### 新增
- 本地 stdio MCP 服务与桌面插件：8 个分组工具入口，旧操作名兼容路由；仅限当前用户命名管道，不监听网络端口。
- 导出预设（PNG / GIF / 精灵表）保存与复用；调色板 `.pxpal` 保存与加载。
- 自动恢复副本与异常退出后恢复提示；参考图加载、透明度与显隐控制。

### 工程化（P0）
- 集中版本号到 `Directory.Build.props`，移除各项目硬编码版本；新增 `PixelCanvas.slnx` 三项目解决方案。
- 补充 `.gitignore`：排除构建产物、部署包目录、压缩归档、输出目录、临时文件与 pdb；新增一键构建测试脚本。

### 测试（P1）
- 测试项目从自写控制台程序迁移到 xUnit，接入 `dotnet test` 门禁。
- 覆盖工程格式兼容矩阵（v1/v2/v3 读写与损坏拒绝）、MCP 路由、导出路径与防覆盖、绘画算法、调色板往返。
- 无头核心用例 48 项通过；交互 UI 冒烟用例默认跳过，需 STA 桌面会话手动验证。

### 架构与健壮性（P2）
- 新增 `AppPaths` 集中输出、预设、恢复目录与环境变量名，消除三处重复硬编码。
- `MainForm` 纯状态字段物理分组到 `MainForm.State.cs`，主窗体只保留控件字段。
- 复核 MCP 管道后台线程到 UI 线程的封送模型；关闭时释放 MCP 取消源；补充并发闸门与取消边界注释。

### 发布收尾（P3）
- MCP 握手返回的服务版本改为读取程序集版本，不再硬编码。
- 打包脚本从 `Directory.Build.props` 解析版本，可将 Release 产物同步刷新到插件目录（排除 pdb），并支持跳过插件刷新或跳过部署包。
- MCP 客户端配置改为 `${PACKAGE_ROOT}` 占位符模板；批量校验脚本按参数传入部署包路径。

### 修复
- 工程、调色板、预设等文件写入采用临时文件 + 原子替换，失败时清理残留临时文件。

## [1.0.0]
- 初始版本：像素画桌面工具，支持 4–512 画布、9 种工具、双轴镜像、图层、帧动画、洋葱皮、DB32 调色板、撤销重做与 PNG/GIF/精灵表导出。
