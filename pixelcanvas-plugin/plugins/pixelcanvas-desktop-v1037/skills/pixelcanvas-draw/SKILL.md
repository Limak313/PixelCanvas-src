---
name: pixelcanvas-draw
description: 在 PixelCanvas Desktop 中通过本地 MCP 直接创建、读取和绘制像素画，适用于用户要求在 PixelCanvas 画布上绘画或编辑。
---

# PixelCanvas 直接绘画

通过 PixelCanvas Desktop 插件的 MCP 工具操作画布，不要使用电脑控制、鼠标或屏幕坐标来绘画。优先使用插件自带的 `pixelcanvas-desktop` 服务，而不是可能缺少新版工具的 `pixelcanvas` 服务。如果插件服务没有加载，说明具体情况；不要推断其他服务有 `open_canvas`。

用户同时要求新建画布、绘画并保存工程时，优先调用 `create_artwork` 一次完成。用户允许复用窗口时设置 `reuse_existing=true`，只会复用尺寸相同、未修改且未命名的空白画布；多个候选时先确认并指定 `process_id`，没有候选时会新开窗口。用户明确要求新窗口时不要启用复用。`layers` 按底到顶提供图层名称（1–32 层），`operations` 使用批量绘画格式且每项明确填写从 0 开始的 `layer`。根据返回的 `success`、`completedStage`、`processId`、`reused` 和错误报告实际结果；未就绪或通信结果不确定时先检查指定画布与文件，勿重复调用造成多个窗口或重复写入。用户只要求绘画时不要自动保存。用户指定路径时优先使用该路径；要求保存但未指定路径时省略输出路径，工具会在 `D:\像素画布\output` 下的工程、动画或 PNG 子文件夹自动生成不重名文件，返回实际路径并告知用户。

1. 用户明确要求打开新画布时，直接调用一次 `open_canvas`，无需先扫描画布，按用户指定传入宽高（未指定默认 32×32），并使用返回的进程 ID。用户允许复用同尺寸空白窗口时可设置 `reuse_existing=true`；返回多个候选时确认目标并指定 `process_id`。等待该次调用完成，不要并行或重复调用；若返回尚未就绪，检查该进程而不是再次打开。明确要求在短时间内再打开另一窗口时设置 `force_new`。其他情况先用 `list_canvases` 查找目标；多个窗口无法确定目标则询问用户。现有画布尺寸与用户要求不符时，不要直接改变画布：创建所需尺寸的新画布。
2. 根据 `open_canvas` 或 `create_artwork` 的返回信息确认尺寸、图层和就绪状态；信息不足时才调用 `canvas_info`，不要重复查询已知状态。对于刚创建且确认没有恢复或其他修改的空白画布，以及已确认的新空白图层，直接绘画，无需读取像素。对已有作品或状态不明的画布，使用 `canvas_read_pixels` 只读取需要保留、避开或修改的目标图层区域；填充、渐变或移动可能影响区域之外的像素时，还需检查相关边界和目标区域。单次读取不超过 4096 像素，必要时分块；不要把尺寸正确或单图层当成画布为空的证据。坐标从左上角 `(0, 0)` 开始，图层索引从底部 0 开始。
   如果 `frameCount` 大于 1，绘画和读取工具只作用于 `activeFrame` 所指的当前帧；播放中无法修改。用户请求动画或跨帧操作时使用同插件的 pixelcanvas-animate 技能；动画默认导出 GIF，可编辑工程另存为 `.pxproj`。
   用户提供本机图片路径并要求加载参考图时使用 `canvas_reference_load`；`canvas_reference_settings` 调整 `visible` 和 0–100 的 `opacity`，用户要求移除时使用 `canvas_reference_clear`。使用 `canvas_reference_info` 确认加载状态，再用 `canvas_reference_image` 获取图片预览；预览最长边至多 512 像素，保持原图比例，不包含画布像素和网格。软件在画布上会将参考图铺满画布，应据此考虑比例。参考图即使隐藏仍可读取，不会保存在工程中。
3. 绘画前自动判断图层：用户明确指定单层、图层数量或目标图层时遵循其要求。简单单一图标、点状修改或对现有指定图层的局部编辑，通常使用当前或指定图层。包含独立背景与主体、多个对象、前后遮挡或用户强调后续分别修改时，按画面内容分为必要的 2–4 层（如背景、主体、前景／细节）；透明背景无需专门创建背景层。不以画布尺寸单独决定是否分层。
   对已有作品，按上述规则读取必要区域，避免覆盖已有内容；添加独立内容时优先另建图层，修改已有内容时使用相应原图层。`canvas_add_layer` 在当前图层上方创建并选中新层，创建后使用返回的索引，逐层绘制；索引从底层 0 开始，增加或排序图层后根据返回信息确认索引。总图层最多 32 层；数量不足时减少新增层数并向用户说明，不删除或隐藏已有层。若上层会遮挡新层，使用 `canvas_move_layer` 调整新层顺序，不改变已有图层的相对顺序。
   多个像素、形状、填充或渐变操作优先用 `canvas_batch_paint`，按顺序执行，每次最多 64 个操作、总共 16384 个像素；单个 paint 仍最多 4096 个像素。任何操作无效时整批不生效，每个有变化的图层各产生一步撤销。图层须先创建，再在操作中指定索引。颜色使用 `#RRGGBB` 或 `transparent`。单一操作也可使用 `canvas_paint_pixels` 或 `canvas_draw_shape`。
   用户要求对称绘画时，按需设置操作的 `mirror_h`（左右）和 `mirror_v`（上下）；像素、形状、填充和渐变及批量操作均支持。默认不镜像，不继承软件界面镜像开关。镜像产生重叠像素时按输入顺序覆盖。
   连续同色区域使用 `canvas_fill` 填充，线性渐变使用 `canvas_gradient`；渐变起止颜色为不透明的 `#RRGGBB`，方向由起点指向终点。
   用户要求批量替换颜色时，用 `canvas_replace_color` 指定当前帧的目标图层和 `from_color`、`to_color`（支持 `#RRGGBB` 或 `transparent`）；该工具替换目标图层所有匹配像素，可撤销，不影响其他图层或帧。读取调色板使用 `canvas_palette_get`，用户要求修改时使用 `canvas_palette_set`；`canvas_palette_save` / `canvas_palette_load` 保存或加载 `.pxpal` 文件。调色板改变不修改画布颜色，替换调色板不可通过画布撤销；修改前保留用户需要的原色板。
   矩形区域复制或移动使用 `canvas_copy_region` 或 `canvas_move_region`，源和目标均需完整位于画布中；透明像素不会覆盖目标。移动会清除源区域。
   操作软件界面上的选区和内部复制缓存时，使用 `canvas_selection_get`、`canvas_selection_set`、`canvas_selection_copy`、`canvas_selection_paste`；粘贴位置遵循软件界面行为。需要切换界面当前图层时使用 `canvas_select_layer`。仅在用户明确要求清空图层时使用 `canvas_clear_layer` 并传 `confirm=true`，可撤销；仅在用户明确要求重置画布尺寸、知悉会清空所有帧和历史时使用 `canvas_resize` 并传 `confirm=true`，不要用它替代创建新画布。
   用户要求改变界面绘画设置时可用 `canvas_draw_options_get` / `canvas_draw_options_set`，控制工具、画笔、颜色和界面镜像；MCP 实际绘画仍需单独传镜像参数。用户要求调整视图时可用 `canvas_view_get` / `canvas_view_set` 控制缩放、适应、滚动、画布位置、网格、沉浸模式和侧栏。沉浸模式下设置侧栏须先退出沉浸。
   使用 `canvas_move_layer` 和 `canvas_set_layer_visibility` 调整图层；索引从底层 0 开始。仅在用户明确要求删除特定图层时调用 `canvas_delete_layer` 并传入 confirm=true，删除会丢掉该层像素但可用 `canvas_undo` 撤销，至少保留一个图层；调整顺序与显隐同样可撤销。
4. 先检查绘画工具返回是否成功；需要判断整体效果时用 `canvas_preview` 获取当前帧的可见图层合成 PNG，预览不包含参考图或网格；要读取指定坐标的可见图层合成颜色使用 `canvas_pick_composite_color`，要读取单一图层则使用 `canvas_read_pixels`。简单操作且结果明确时可省略读取；用户要求核验、操作失败或返回状态不确定时应检查相关区域并如实报告，无法核实就说明。简要告知用户作品在哪个画布、是否分层以及各层用途。只有用户要求保存或导出时才使用保存或导出工具。

用户要求打开已有工程时，使用 `canvas_open_project`；窗口有未保存修改时先保存或另开窗口，不丢弃修改。用户要求保存当前工程时使用 `canvas_save_project`：未命名画布未指定路径时自动保存到默认产出文件夹，已有路径的工程会更新原文件；用户要求保留原件时使用 `canvas_save_copy`。

用户同时要求保存工程和导出图片时使用 `canvas_save_and_export`，未指定路径时使用默认产出文件夹；只导出当前帧。检查 `projectSaved` 和 `pngExported`：导出失败时工程可能已保存，应说明实际结果，不要重复保存或声称导出成功。

用户要求使用导出预设时，通过 `canvas_export_presets_list` 查看，再用 `canvas_export_with_preset` 按名称导出；预设支持 PNG 当前帧、GIF、精灵表，GIF 预设帧率不改变工程帧率。用户要求创建预设时用 `canvas_export_presets_put`；覆盖同名预设须用户明确要求并传 `overwrite=true`。仅在用户明确要求删除时使用 `canvas_export_presets_delete` 并传 `confirm=true`。输出始终是新文件，报告返回的实际路径。

如果画布状态为 `recovery_pending`，请用户先在画布窗口处理自动恢复提示，不要再次启动画布或修改文件。如果保存提示工程已被外部修改，请保留当前画布，告知冲突并在用户指定新路径后另存副本，不要覆盖外部修改。

如果 MCP 工具不可用或画布忙，说明实际错误，不要声称已绘制成功；不要改用电脑控制，除非用户明确要求。
