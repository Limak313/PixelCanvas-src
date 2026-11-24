---
name: pixelcanvas-animate
description: 在 PixelCanvas Desktop 中通过本地 MCP 创建、编辑、导入或播放逐帧动画；适用于用户要求操作动画帧或制作像素动画。
---

# PixelCanvas 逐帧动画

通过 PixelCanvas Desktop 的本地 MCP 工具操作，不使用电脑控制。坐标和帧索引均从 0 开始，图层索引从底层 0 开始。

1. 用户要求新画布时调用 `open_canvas`，否则通过 `list_canvases` 选择窗口；多个窗口目标不明确时询问用户。调用 `canvas_info` 确认尺寸、`frameCount`、`activeFrame`、`fps` 和 `playing`。如果提示 `recovery_pending`，请用户先处理恢复提示，不要重复打开窗口。
2. 编辑动画前，如正在播放，用 `canvas_set_playback` 设置 `playing=false`。用 `canvas_add_frame` 在当前帧后新增：`copy=false` 创建空白帧，`copy=true` 复制当前帧及其图层。新增后使用返回的 `activeFrame`；需要编辑其他帧时调用 `canvas_select_frame` 并验证返回状态。
   用户要求调整帧顺序时用 `canvas_move_frame`，`frame` 和 `target_index` 从 0 开始；排序可撤销，保留当前选中帧。洋葱皮通过 `canvas_set_onion_skin` 的 `enabled` 设置；`canvas_info` 返回 `onionEnabled` 和 `onionVisible`，播放时暂时隐藏但保持开关状态。洋葱皮只影响窗口显示，不包含在 `canvas_preview` 或导出中；查看某帧的画面可切换帧后调用 `canvas_preview`。
   批量追加动画时优先用 `canvas_batch_frames`：每次最多 32 帧，依次追加到末尾。每帧的 `copy=true` 复制上一帧（第一项复制调用时的当前帧），`copy=false` 新建空白帧；可对每帧提供批量绘画 `operations`，可选设置 `fps`。整批失败时没有帧或帧率变化；整批作为一步撤销（`canvas_undo` 可整批回退），每帧至多 64 操作，整批至多 256 操作、16384 个显式像素。已有帧不被修改。需要检查画面时可用 `canvas_preview` 查看当前帧可见图层合成图。
3. 每帧独立绘制：新建空白帧或新建空白图层无需事先读取像素；复制帧、导入帧和已有帧不能假定为空，仅用 `canvas_read_pixels` 读取需要保留、修改或避开的图层区域。填充、渐变或移动还要检查可能受影响的边界及目标区域。使用 `canvas_batch_paint` 批量执行当前帧的像素、形状、填充和渐变；每批最多 64 个操作、16384 个像素，单个 paint 最多 4096 个像素，失败整批不生效。用户要求对称时在每个操作中指定 `mirror_h`、`mirror_v`，默认不继承界面开关。对连续动作优先复制前一帧再修改。检查工具返回；按风险或用户要求抽查关键内容，简单且结果明确时可省略读取，状态不确定时核实并如实报告。
4. 用户提供本机图片路径并要求导入时调用 `canvas_import_frame`；PNG、JPEG、BMP 图片需与画布尺寸一致，半透明像素转换为透明或不透明。不要假设参考图已导入为帧。
5. 用户要求预览或播放时用 `canvas_set_playback`，`playing=true`，可选 `fps` 为 1–60；用户要求停止时设置 `playing=false`。正在播放时不要尝试编辑或保存，先停止。除非用户要求停止，播放后保持播放状态。

用户要求精灵表时，使用 `canvas_export_sprite_sheet`，或对磁盘工程使用 `project_export_sprite_sheet`；可选 `columns`（每行帧数）和 `scale`（1–16），省略路径时生成在 PNG 文件夹。全部帧按从左到右、从上到下排列，透明格保留；返回的 `layout` 含行列数、缩放后的帧尺寸和整图尺寸，告知用户这些信息。输出不含网格、洋葱皮或参考图。导出最大边长 16384、总像素 16777216，超限时减少列数或放大倍数。

用户要求用导出预设时先调用 `canvas_export_presets_list`，再用 `canvas_export_with_preset`；GIF 预设帧率只影响输出，不修改工程帧率。预设的创建、替换和删除遵循用户的明确要求，覆盖或删除分别须传 `overwrite=true` 或 `confirm=true`。

仅在用户明确要求删除当前指定帧时调用 `canvas_delete_frame` 并传入 `confirm=true`；确认后仍可用 `canvas_undo` 撤销，且至少保留一帧。至多 128 帧，受总图层像素上限约束，超限时说明而不是删除已有内容。用户要求保存或导出动画时，先停止播放，默认使用 `canvas_export_gif` 导出循环 GIF（磁盘工程可使用 `project_export_gif`），未指定路径时保存在动画文件夹并报告实际路径。GIF 颜色超过 255 种时会量化，无法保存图层；用户要求可继续编辑的工程时另用 `canvas_save_project` 或 `canvas_save_copy` 保存 `.pxproj`。PNG 仅包含当前帧。报告实际帧数、帧率和保存情况。
