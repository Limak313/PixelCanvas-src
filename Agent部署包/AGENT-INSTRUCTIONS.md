# PixelCanvas MCP 使用提示

此文件可作为支持自定义指令的 Agent 的可选说明；具体加载方式取决于客户端。请使用客户端连接的 `pixelcanvas` MCP 服务，并以 `tools/list` 返回的工具和参数为准。

服务将以下操作名整合到分组工具中：根据分组工具描述选择操作，用 `pixelcanvas_tool_info` 查询该操作的参数，再将操作名作为 `action`、原参数作为 `arguments` 传给对应分组工具。

- 用户要求新画布时使用 `open_canvas` 或 `create_artwork`；已有多个画布且目标不明确时请用户指定。画布宽高为 4–512，默认 32×32，坐标从左上角 0 开始；图层从底层 0 开始。
- 使用 `canvas_batch_paint` 或 `canvas_batch_frames` 批量绘画；对已有作品先检查需要保留的区域。镜像绘画需要在操作中显式设置 `mirror_h`、`mirror_v`。可使用 `canvas_preview` 查看当前帧合成图，或用 `canvas_pick_composite_color` 读取可见图层的指定像素。
- 动画可新增、复制、排序和播放帧；默认可用 `canvas_export_gif` 导出 GIF，也可用 `canvas_export_sprite_sheet` 导出精灵表。工程 `.pxproj` 保留可编辑图层和帧。
- 用户请求保存或导出时执行保存，不要仅因绘画完成就擅自保存；未指定路径时使用默认输出目录，并向用户报告工具返回的真实路径。参考图不会保存在工程中。
- 调整画布尺寸会清空所有帧和历史；清空图层、删除帧／图层、覆盖预设等操作，应仅在用户明确要求相应操作时执行，并遵循工具的确认参数。不要因超时盲目重复创建窗口或覆盖文件。
- 如果画布显示 `recovery_pending`，请用户在窗口中处理恢复提示。工具返回错误时说明实际结果；不要假称成功。
