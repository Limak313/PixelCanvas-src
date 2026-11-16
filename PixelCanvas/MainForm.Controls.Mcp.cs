using System.Text.Json.Nodes;

namespace PixelCanvas;

public sealed partial class MainForm
{
    private static bool? OptionalFlag(JsonObject args, string name)
    {
        if (args[name] is null) return null;
        if (args[name] is JsonValue v && v.TryGetValue<bool>(out bool flag)) return flag;
        throw new ArgumentException($"{name} 必须为布尔值。");
    }

    private object SelectLayerFromMcp(JsonObject args)
    {
        int index = McpCanvas.LayerIndex(args, _layers, _activeLayer);
        _activeLayer = index; RefreshLayerList(); ActivateLayer();
        return new { ok = true, data = FrameStatusFromMcp() };
    }

    private object ClearLayerFromMcp(JsonObject args)
    {
        if (OptionalFlag(args, "confirm") != true) throw new ArgumentException("清空图层须传入 confirm=true。");
        int index = McpCanvas.LayerIndex(args, _layers, _activeLayer);
        var layer = _layers[index];
        if (!layer.Visible) throw new ArgumentException("不能修改隐藏图层。");
        int[] before = layer.Grid.Snapshot();
        bool changed = before.Any(color => color != Argb.Transparent);
        if (changed) { layer.Grid.Clear(); layer.History.Commit(before); _canvas.RefreshFromData(); UpdateUndoButtons(); SetDirty(); }
        return new { ok = true, data = new { layer = index, changed } };
    }

    private object ResizeFromMcp(JsonObject args)
    {
        int width = McpCanvas.Number(args, "width"), height = McpCanvas.Number(args, "height");
        if (width is < 4 or > 512 || height is < 4 or > 512) throw new ArgumentException("宽高须为 4–512。");
        if (width == _grid.Width && height == _grid.Height) return new { ok = true, data = new { changed = false, canvas = FrameStatusFromMcp() } };
        if (OptionalFlag(args, "confirm") != true) throw new ArgumentException("调整尺寸会清空所有帧及撤销历史，须传入 confirm=true。");
        ApplyCanvasSize(width, height); RestoreSizeSelection();
        return new { ok = true, data = new { changed = true, canvas = FrameStatusFromMcp() } };
    }

    private object SelectionInfo() => new { bounds = _canvas.SelectionBounds is { } r ? new { x = r.X, y = r.Y, width = r.Width, height = r.Height } : null, copied = _canvas.HasCopiedSelection };

    private object SelectionSetFromMcp(JsonObject args)
    {
        bool clear = OptionalFlag(args, "clear") ?? false;
        if (clear) _canvas.SetSelection(null);
        else
        {
            var r = new Rectangle(McpCanvas.Number(args, "x"), McpCanvas.Number(args, "y"), McpCanvas.Number(args, "width"), McpCanvas.Number(args, "height"));
            _canvas.SetSelection(r); SetTool(ToolKind.Selection);
        }
        return new { ok = true, data = SelectionInfo() };
    }

    private object SelectionCopyFromMcp()
    {
        if (_canvas.SelectionBounds is null) throw new InvalidOperationException("没有选区。");
        _canvas.CopySelection(); return new { ok = true, data = SelectionInfo() };
    }

    private object SelectionPasteFromMcp()
    {
        if (!_canvas.HasCopiedSelection) throw new InvalidOperationException("选区缓存为空。");
        if (!_layers[_activeLayer].Visible) throw new InvalidOperationException("不能修改隐藏图层。");
        SetTool(ToolKind.Selection); _canvas.PasteSelection();
        return new { ok = true, data = new { changed = _canvas.LastStrokeChanged, selection = SelectionInfo() } };
    }

    private object DrawOptionsInfo() => new { tool = _canvas.Tool.ToString().ToLowerInvariant(), color = _currentColor == Argb.Transparent ? "transparent" : "#" + Argb.ToRgbHex(_currentColor), gradientEnd = "#" + Argb.ToRgbHex(_canvas.GradientEndColor), brushSize = (int)_brushSize.Value, mirrorH = _mirrorHCheck.Checked, mirrorV = _mirrorVCheck.Checked };

    private object DrawOptionsFromMcp(JsonObject args)
    {
        ToolKind? tool = null; int? color = null, endColor = null, size = null;
        if (args["tool"] is not null)
        {
            string text = McpCanvas.Text(args, "tool");
            if (!Enum.TryParse<ToolKind>(text, true, out var parsed) || !Enum.IsDefined(parsed) || int.TryParse(text, out _)) throw new ArgumentException("绘画工具名称不正确。");
            tool = parsed;
        }
        if (args["color"] is not null) color = McpCanvas.ParseColor(McpCanvas.Text(args, "color"));
        if (args["gradient_end"] is not null) { endColor = McpCanvas.ParseColor(McpCanvas.Text(args, "gradient_end")); if (endColor == Argb.Transparent) throw new ArgumentException("渐变颜色不能透明。"); }
        if (args["brush_size"] is not null) { size = McpCanvas.Number(args, "brush_size"); if (size is < 1 or > 4) throw new ArgumentException("画笔大小须为 1–4。"); }
        bool? h = OptionalFlag(args, "mirror_h"), v = OptionalFlag(args, "mirror_v");
        if (tool.HasValue) SetTool(tool.Value);
        if (color.HasValue) SetCurrentColor(color.Value);
        if (endColor.HasValue) { _canvas.GradientEndColor = endColor.Value; _gradientPreview.Invalidate(); }
        if (size.HasValue) _brushSize.Value = size.Value;
        if (h.HasValue) _mirrorHCheck.Checked = h.Value;
        if (v.HasValue) _mirrorVCheck.Checked = v.Value;
        return new { ok = true, data = DrawOptionsInfo() };
    }

    private object ViewInfo() => new { zoom = _canvas.Zoom, manualZoom = _manualZoom, placement = _centerCanvas ? "center" : "top_left", scrollX = -_canvasPanel.AutoScrollPosition.X, scrollY = -_canvasPanel.AutoScrollPosition.Y, grid = _gridCheck.Checked, immersive = _immersive, leftSidebar = _immersive ? _leftWasVisible : _leftSidebar.Visible, rightSidebar = _immersive ? _rightWasVisible : _rightSidebar.Visible };

    private object SetViewFromMcp(JsonObject args)
    {
        int? zoom = args["zoom"] is null ? null : McpCanvas.Number(args, "zoom"), x = args["scroll_x"] is null ? null : McpCanvas.Number(args, "scroll_x"), y = args["scroll_y"] is null ? null : McpCanvas.Number(args, "scroll_y");
        bool? fit = OptionalFlag(args, "fit"), grid = OptionalFlag(args, "grid"), immersive = OptionalFlag(args, "immersive"), left = OptionalFlag(args, "left_sidebar"), right = OptionalFlag(args, "right_sidebar");
        string? placement = args["placement"] is null ? null : McpCanvas.Text(args, "placement");
        if (zoom is < 1 or > 128 || x < 0 || y < 0 || (fit == true && zoom.HasValue)) throw new ArgumentException("缩放必须为 1–128，滚动坐标不能为负，fit 不能与 zoom 同时使用。");
        if (placement is not (null or "center" or "top_left")) throw new ArgumentException("placement 必须为 center 或 top_left。");
        if ((immersive ?? _immersive) && (left.HasValue || right.HasValue)) throw new ArgumentException("沉浸模式下请先退出再设置侧边栏。");
        if (immersive.HasValue && immersive.Value != _immersive) ToggleImmersive();
        if (left.HasValue) _leftSidebar.Visible = left.Value;
        if (right.HasValue) _rightSidebar.Visible = right.Value;
        if (placement is not null) _placementCombo.SelectedIndex = placement == "center" ? 1 : 0;
        if (grid.HasValue) _gridCheck.Checked = grid.Value;
        if (fit == true) FitCanvas();
        if (zoom.HasValue) { _manualZoom = true; _canvas.SetZoom(zoom.Value); }
        if (x.HasValue || y.HasValue) { _canvasPanel.AutoScrollPosition = new Point(x ?? -_canvasPanel.AutoScrollPosition.X, y ?? -_canvasPanel.AutoScrollPosition.Y); UpdateCanvasPlacement(); }
        return new { ok = true, data = ViewInfo() };
    }
}
