# PixelCanvas 本地 MCP 服务

PixelCanvas 提供本地标准输入 / 输出（stdio）MCP 服务。连接正在运行的画布时，服务通过只允许当前 Windows 用户访问的命名管道通信，不监听网络端口。

服务本身是跨平台的 `net10.0` 宿主。Windows 上可连接实时画布并使用全部工具；Linux/macOS 上可直接使用 `project_*` 工程文件工具、GIF/SVG/PNG/精灵表/序列帧导出与路径查询，调用 `open_canvas` / `create_artwork` 等实时画布入口会返回「实时画布宿主仅支持 Windows」的明确错误，而不是启动失败。

## 启动

使用独立部署包时，运行包内 `生成客户端配置.ps1`，将生成的 `mcp-config.json` 中 `pixelcanvas` 服务合并到客户端配置。命令为包内 `server\PixelCanvas.Mcp.exe` 的绝对路径；自包含包与精简包结构相同，区别是前者两个 exe 各自携带 .NET 运行时。客户端负责启动和停止 MCP 服务，不需要长期打开终端。其他客户端的配置位置和具体格式以其自身说明为准。

Codex 桌面插件使用其自身的插件管理方式；独立部署包与 Codex 插件可以并存。部署包要求 Windows x64 或 Windows arm64 桌面会话，不提供网络服务；请选择与系统架构一致的包。

## 工具入口

新版 MCP 服务将 78 个操作分为七个入口：`pixelcanvas_canvas`（窗口与画布）、`pixelcanvas_draw`（绘画与读取）、`pixelcanvas_layers`（图层与选区）、`pixelcanvas_animation`（动画）、`pixelcanvas_settings`（视图、参考图与调色板）、`pixelcanvas_files`（保存与导出）和 `pixelcanvas_project`（工程文件）。第八个工具 `pixelcanvas_tool_info` 用于查询操作的完整参数 schema。每个操作只会出现在一个入口的 `action` 枚举里，`pixelcanvas_tool_info` 的枚举是它们的并集。

先从分组工具的描述中选择操作名，再用 `pixelcanvas_tool_info` 的 `action` 查询参数。调用分组工具时将原操作名放在 `action`，原参数对象放在 `arguments`，例如 `pixelcanvas_project` 的参数为 `{"action":"project_info","arguments":{"path":"C:\\art\\example.pxproj"}}`。下文列出的原名称均是操作名。为兼容已有客户端，服务仍接受直接使用原名称的调用，但不会在 `tools/list` 中列出这些旧名称。客户端通常需要重启或刷新 MCP 工具列表才能看到新入口；已安装的旧版本插件或部署包不会自动更新。

要一次查询多个操作，可传 `{"actions":["canvas_reference_info","canvas_import_frame","canvas_export_png"]}`，最多 32 个，结果按请求顺序返回。`action` 与 `actions` 不能同时提供；有未知名称时整次查询返回错误。

当前工程与绘画采用二值透明：导入帧时 alpha 小于 128 的像素变为透明，其余变为不透明；读取工程时 alpha 为 0 视为透明，其他值视为不透明，保存时只写入 00 或 FF。PNG 导出不能保留半透明像素，手工写入半透明 `.pxproj` 也不能绕过此限制。需要精确保留半透明时，应保留原始图像并在支持完整 alpha 的工具中处理。

## 正在运行的画布

桌面插件自带 PixelCanvas 程序，可调用 `open_canvas` 打开新画布。插件 1.0.5 起可用 `width` 和 `height` 指定 4–512 的尺寸，不指定时为 32×32。启动后返回进程 ID 与就绪状态；若提示尚未就绪，请检查新窗口是否正在等待恢复等操作。

自动恢复提示等待用户处理时，启动结果会返回 `ready: false` 和 `status: recovery_pending`；此时请先在画布窗口选择是否恢复。工程打开或上次保存后若被其他程序修改、替换或删除，保存会拒绝覆盖；可使用 `canvas_save_copy` 另存新文件。

可通过包内 `启动画布.cmd` 或 MCP `open_canvas` 启动画布，再调用 `list_canvases`。只有一个窗口时可以省略 `process_id`；多个窗口时必须指定目标进程 ID。旧版本窗口不一定支持所有接口。

- `canvas_info`、`canvas_read_pixels`：读取尺寸、图层和矩形区域；图层索引从底层 0 开始。
- `canvas_reference_info`、`canvas_reference_image`：读取窗口中已加载参考图的信息和 PNG 预览，预览最长边最多 512 像素，不修改画布；参考图不会保存在工程中。
- `canvas_paint_pixels`：批量修改像素，单次最多 4096 个，一次调用对应一步撤销。
- `canvas_draw_shape`：绘制直线、空心矩形或椭圆，一次调用对应一步撤销。
- `canvas_fill`、`canvas_gradient`：在起点所在的四连通同色区域填充纯色或线性渐变，一次调用对应一步撤销；渐变起止颜色为不透明的 `#RRGGBB`。
- `canvas_copy_region`、`canvas_move_region`：将矩形区域复制或移动到同图层的指定位置，源和目标需完整位于画布内；透明像素不覆盖目标，移动会清除源区域，可撤销。
- `canvas_add_layer`、`canvas_undo`、`canvas_redo`：新建图层和撤销 / 重做。
- `canvas_delete_layer`、`canvas_move_layer`、`canvas_set_layer_visibility`：删除、排序和设置图层显隐，三者都可用 `canvas_undo` 撤销。索引从底层 0 开始；删除须明确提供 `confirm: true`，且至少保留一层。
- `canvas_save_copy`、`canvas_export_png`：另存为新的工程或 PNG 文件，不更改当前工程路径。
- `canvas_export_svg`、`canvas_export_frame_sequence`：导出当前帧的 SVG 矢量色块，或把全部帧写成 PNG 序列目录；都不覆盖既有文件。
- `canvas_open_project`：将绝对路径的工程打开到窗口；有未保存修改时拒绝操作。
- `canvas_save_project`：保存当前工程；未命名画布省略路径时使用默认目录，已有路径的工程会保存回原文件。另存副本使用 `canvas_save_copy`。

用户正在拖动画布或绘制时，实时操作会返回错误，避免打断当前操作。隐藏图层不能绘制。批量像素写入在修改前检查所有参数；颜色格式为 `#RRGGBB` 或 `transparent`。

多帧工程中，画布工具操作当前帧；`canvas_info` 返回帧数、当前帧索引、帧率和播放状态。`canvas_add_frame`、`canvas_select_frame`、`canvas_delete_frame`、`canvas_import_frame` 和 `canvas_set_playback` 可管理当前窗口的动画帧；帧索引从 0 开始，删除需明确确认，确认后仍可用 `canvas_undo` 撤销。播放期间修改画布的 MCP 请求会被拒绝，停止播放后可继续编辑。工程文件工具修改当前帧并另存时会保留其他帧。

`canvas_export_gif` 和 `project_export_gif` 将全部帧导出为循环 GIF，未指定路径时保存到 `output\动画`。可指定 1–16 倍缩放，总输出像素（宽 × 高 × 帧数）不超过 16,777,216。GIF 保留透明像素和可见图层合成，但不保留独立图层。调色板有两种模式：省略 `palette` / `use_project_palette` 时自动输出，最多 255 色、超出后量化；传 `palette`（1–255 个 `#RRGGBB`）或 `use_project_palette=true` 时按给定顺序写入固定索引色，透明固定索引 0，画面外颜色映射到最近色。两者不能同时使用，工程调色板超过 255 色会报错。帧延时按 GIF 的百分之一秒精度近似帧率。要继续编辑请另存 `.pxproj`。

`canvas_preview` 以 MCP 图片返回当前帧的可见图层合成 PNG，不含网格与参考图；`max_side` 为 1–512，默认 512。`canvas_batch_frames` 向末尾一次追加 1–32 帧；每项 `copy=true` 复制上一项（第一项复制当前帧），否则新建空白帧，每项可提供与 `canvas_batch_paint` 相同的 `operations`；可选 `fps`。整批成功后切换到最后一帧，失败不修改动画或帧率。整批最多 256 个操作、16384 个显式像素，每帧最多 64 操作；整批作为一步撤销。

`canvas_move_frame` 使用从 0 开始的 `frame` 和 `target_index` 调整帧顺序，当前选中的帧保持选中，排序可用 `canvas_undo` 撤销；播放中需先停止。`canvas_undo` / `canvas_redo` 不传 `layer` 时按动作先后自动选择图层像素或结构改动（帧与图层），传 `layer` 时只作用于该图层。`canvas_set_onion_skin` 的 `enabled` 控制洋葱皮；`canvas_info` 中 `onionEnabled` 表示开关，`onionVisible` 表示当前是否显示，播放中会暂时隐藏。洋葱皮不进入合成预览、PNG 或 GIF。

`canvas_export_sprite_sheet` 和 `project_export_sprite_sheet` 将全部帧从左到右、从上到下排列成 PNG；可选 `columns`（1–64，默认帧数平方根向上取整）和 `scale`（1–16）。结果的 `layout` 包含行列数、每帧尺寸和整图尺寸，空余单元格透明；不包含隐藏图层、网格、参考图和洋葱皮。单边最多 16384 像素、总计最多 16777216 像素。未指定输出路径时使用动画文件夹，不覆盖已有文件。

`canvas_export_svg` 把当前帧的可见图层写成 SVG：相邻同色像素先横向合并、再纵向合并为 `<rect>`，透明像素不输出，`shape-rendering="crispEdges"` 保证缩放后边缘清晰。结果不随缩放失真，因此没有 `scale` 参数；返回 `rectangles` 为色块数。`path` 必须是尚不存在的 `.svg` 绝对路径，省略时写入默认 PNG 目录。

`canvas_export_frame_sequence` 把全部帧按顺序写成 PNG 序列到 `directory`（必须是新目录或空目录，已有文件不覆盖），文件名默认 `frame-001.png`、`frame-002.png`…，可用 `base_name` 改前缀、`scale` 指定 1–16 倍。返回 `directory`、`frameCount`、`width`、`height`、`scale` 与 `files` 列表；适合游戏引擎逐帧导入。该动作只作用于运行中的画布，磁盘工程请先 `canvas_open_project` 或按需用 `project_export_png` 逐帧处理。

`canvas_replace_color` 在当前帧指定图层替换所有匹配的 `from_color`，支持目标 `to_color` 为 `#RRGGBB` 或 `transparent`，作为一步撤销。`canvas_palette_get` / `canvas_palette_set` 读取或替换工程调色板，`canvas_palette_save` / `canvas_palette_load` 保存和加载 `.pxpal`；更改调色板不改变画布像素，也不进入画布撤销历史。文件最多 128 KiB、最多 1024 色，输出不覆盖已有文件。

`canvas_export_presets_list`、`canvas_export_presets_put`、`canvas_export_presets_delete` 和 `canvas_export_with_preset` 管理并应用本机预设。格式为 `png`、`gif`、`sheet`；支持 `scale`、GIF `fps`、精灵表 `columns` 和可留空的绝对目录。覆盖同名预设需 `overwrite=true`，删除需 `confirm=true`。导出生成新文件；GIF 的预设帧率只影响导出，不改变工程。预设保存在本机 LocalAppData 的 PixelCanvas 目录，不随工程文件保存。

`canvas_reference_load` 读取本地 PNG、JPEG、BMP 或 GIF 参考图，文件最多 64 MiB、图像最多 33554432 像素；`canvas_reference_info` 返回加载时的 `sourcePath`。参考图是加载时的内存快照，路径可能已失效或内容已改变，清除参考图后路径为 null。`canvas_reference_image` 的 `max_side` 可直接设为目标最长边（1–512），返回信息中包含原图尺寸；缩放会重采样，不保证与其他重采样算法的结果一致。`canvas_reference_settings` 设置 `visible` 和 0–100 的 `opacity`，`canvas_reference_clear` 移除参考图。参考图不保存到工程，也不影响像素导出。`canvas_pick_composite_color` 读取当前帧指定位置的可见图层合成色，不包含参考图、网格或洋葱皮。绘制像素、形状、填充、渐变及其批量操作支持布尔参数 `mirror_h`、`mirror_v`，默认均为 false，不继承界面镜像开关；重叠像素按输入顺序覆盖。

`canvas_select_layer` 切换当前图层；`canvas_clear_layer` 清空指定图层并可撤销，`canvas_resize` 切换尺寸并清空所有帧和撤销历史，两者须 `confirm=true`。`canvas_selection_get/set/copy/paste` 控制界面选区及内部缓存，粘贴位置和透明像素行为与界面一致。`canvas_draw_options_get/set` 控制界面工具、颜色、笔刷及镜像；`canvas_view_get/set` 管理缩放、适应、滚动、位置、网格、侧栏与沉浸显示。界面镜像设置不会隐式改变 MCP 绘画请求。

## 工程文件

`project_create` 创建新工程；`project_info` 和 `project_read_pixels` 读取工程；`project_paint_pixels`、`project_draw_shape`、`project_fill`、`project_gradient`、`project_copy_region`、`project_move_region`、`project_add_layer` 将修改保存到新的 `output_path`；`project_export_png` 导出图片。所有路径必须为绝对路径，输出文件必须不存在，原工程不会被覆盖。

工程图层管理还支持 `project_delete_layer`、`project_move_layer` 和 `project_set_layer_visibility`，结果保存到新工程。MCP 新建文件的显式输出路径（包括 `path`、`output_path`、`png_path` 和 `project_path`）须为符合格式的绝对路径；父目录不存在时自动创建，仍不会覆盖已有输出。输入路径不会自动创建，已打开工程的普通保存仍使用原有路径与外部修改保护。没有权限或父路径被文件占用时返回错误；目录可能在后续参数或写入失败时已创建。

图层更改和文件输出需由调用方选择正确路径。PNG 导出支持 1–16 倍，输出最多 16,777,216 像素；导出不包含参考图或网格线。

## 编译和发布

在仓库根目录构建无头 MCP 服务（需安装 .NET 10 SDK，Windows/Linux/macOS 均可）：

```bash
dotnet build PixelCanvas.Mcp/PixelCanvas.Mcp.csproj -c Release
```

桌面程序仍需 Windows：`dotnet build PixelCanvas/PixelCanvas.csproj -c Release`。无头核心测试可在任意平台运行：

```bash
dotnet test PixelCanvas.Core.Tests/PixelCanvas.Core.Tests.csproj -c Release
```

### 批量绘画与保存导出

`canvas_batch_paint` 的 `operations` 按顺序支持 `paint`、`shape`、`fill`、`gradient`，各操作使用原工具的参数，并用 `type` 指定类型。一次最多 64 个操作、合计 16384 个像素（单个 paint 最多 4096）。全部成功才应用；每个发生变化的图层各有一步撤销，仅作用于当前帧。

`canvas_save_and_export` 接收 `png_path`（不存在的 PNG 绝对路径）、可选 `scale`（1–16），可选 `project_path`（不存在的 .pxproj 绝对路径），省略路径时使用默认目录。已有工程沿用当前路径并检查外部修改；PNG 仅导出当前帧。结果分别提供 `projectSaved` 和 `pngExported`，导出失败时工程可能已保存，返回 `error` 说明原因。

`create_artwork` 一次打开或复用窗口、创建图层、批量绘画并保存新工程。`width`、`height` 默认 32，`layers` 是从底到顶排列的图层名称数组，`operations` 与 `canvas_batch_paint` 格式相同但每项必须明确指定 `layer`，`project_path` 可指定不存在的 .pxproj 绝对路径，省略时使用默认目录。完成后返回 `success`、`processId`、`completedStage`；失败时保留画布，并标识已完成阶段。如果通信结果为 `unknown`，应先检查窗口和文件，不要直接重试。

`open_canvas` 和 `create_artwork` 可设置 `reuse_existing=true`，复用新版软件中尺寸相同、未修改、未命名的单帧单层空白画布。没有可复用窗口时新建；多个候选时返回 `candidates`，可再次调用并指定 `process_id`。指定窗口不可用时返回错误，不自动替换目标。明确要求新窗口时 `open_canvas` 可使用 `force_new=true`，不可与复用同时使用。组合工具在实际绘制前再次检查状态与尺寸，以保护期间被修改的画布。

默认产出根目录优先级：`PIXELCANVAS_OUTPUT_DIR` 环境变量 > 当前目录含 `PixelCanvas.slnx` 时使用其下 `output` > 用户文档目录的 `PixelCanvas\output`。单帧和多帧 `.pxproj` 工程放入 `工程`，普通 PNG 放入 `PNG`，GIF 和精灵表 PNG 放入 `动画`。相应保存和导出对话框使用这些默认目录；已有工程的普通保存仍使用原路径。MCP 在新工程或导出操作省略输出路径时自动生成唯一文件名，并在结果中返回实际路径。指定路径及导出预设中指定的目录优先，不覆盖已有输出。可通过绝对路径环境变量 `PIXELCANVAS_OUTPUT_DIR` 覆盖输出**根目录**；例如设置为 `D:\像素出图`，PNG 会写入 `D:\像素出图\PNG`，不要将变量设置为 `PNG` 子目录。运行中画布沿用自身启动时的环境变量，修改服务配置不会改变已打开的画布；需从新服务启动新画布。工程文件操作则使用服务进程的环境变量。
