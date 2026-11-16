using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PixelCanvas;

/// <summary>
/// 渲染与交互层：把 PixelGrid 按整数倍放大绘制（棋盘格 + 网格线 + 悬停高亮叠加），
/// 并处理鼠标绘制（左键当前工具 / 右键快速取色）。
/// </summary>
public sealed class CanvasControl : Control
{
    private PixelGrid _grid;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<Layer>? Layers { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Editable { get; set; } = true;
    private Bitmap _bitmap = null!;      // 与画布同分辨率的原生位图（导出 PNG 也用它，不含网格线）
    private int[] _compositePixels = Array.Empty<int>();
    private int[] _compositeRow = Array.Empty<int>();
    private int _zoom = 1;
    private Brush _checkerBrush = null!;
    private Bitmap? _checkerBmp;         // 棋盘格纹理源图（与画刷同生命周期）
    private int _checkerCell = -1;
    private Image? _referenceImage;
    private string? _referenceSourcePath;
    private Bitmap? _onionPrevious, _onionNext;
    private float _referenceOpacity = 0.35f;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowReference { get; set; } = true;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float ReferenceOpacity
    {
        get => _referenceOpacity;
        set { _referenceOpacity = Math.Clamp(value, 0f, 1f); Invalidate(); }
    }

    public void SetReferenceImage(Image? image, string? sourcePath = null)
    {
        Image? previous = _referenceImage;
        _referenceImage = image;
        _referenceSourcePath = image is null ? null : sourcePath;
        previous?.Dispose();
        Invalidate();
    }

    internal void SetOnionFrames(AnimationFrame? previous, AnimationFrame? next)
    {
        _onionPrevious?.Dispose(); _onionNext?.Dispose();
        _onionPrevious = MakeOnionBitmap(previous, Color.FromArgb(80, 255, 95, 95));
        _onionNext = MakeOnionBitmap(next, Color.FromArgb(80, 80, 180, 255));
        Invalidate();
    }

    private Bitmap? MakeOnionBitmap(AnimationFrame? frame, Color tint)
    {
        if (frame is null || frame.Layers.Count == 0 || frame.Layers[0].Grid.Width != _grid.Width || frame.Layers[0].Grid.Height != _grid.Height) return null;
        var pixels = new int[_grid.Width * _grid.Height];
        foreach (var layer in frame.Layers) if (layer.Visible)
            for (int i = 0; i < pixels.Length; i++) if (layer.Grid.RawPixels[i] != Argb.Transparent) pixels[i] = tint.ToArgb();
        var bitmap = new Bitmap(_grid.Width, _grid.Height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); } finally { bitmap.UnlockBits(data); }
        return bitmap;
    }

    internal object ReferenceInfo() => new
    {
        loaded = _referenceImage is not null,
        width = _referenceImage?.Width,
        height = _referenceImage?.Height,
        sourcePath = _referenceSourcePath,
        visible = ShowReference,
        opacity = _referenceOpacity,
    };

    internal object ReferenceImage(int maxSide)
    {
        if (_referenceImage is null) throw new InvalidOperationException("当前画布没有加载参考图。");
        if (maxSide is < 1 or > 512) throw new ArgumentException("max_side 必须为 1–512。 ");
        int sourceWidth = _referenceImage.Width, sourceHeight = _referenceImage.Height;
        double scale = Math.Min(1.0, (double)maxSide / Math.Max(sourceWidth, sourceHeight));
        int width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        int height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(_referenceImage, new Rectangle(0, 0, width, height), 0, 0, sourceWidth, sourceHeight, GraphicsUnit.Pixel);
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return new { loaded = true, sourceWidth, sourceHeight, width, height, visible = ShowReference, opacity = _referenceOpacity, imageBase64 = Convert.ToBase64String(stream.ToArray()) };
    }

    // 当前绘制状态（由 MainForm 注入）
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ToolKind Tool { get; set; } = ToolKind.Pencil;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int BrushSize { get; set; } = 1;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CurrentColor { get; set; } = Argb.FromRgb(0x000000);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int GradientEndColor { get; set; } = Argb.FromRgb(0xFFFFFF);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool MirrorH { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool MirrorV { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowGrid { get; set; } = true;

    /// <summary>取色器取到颜色（左键或右键）。</summary>
    public event Action<int>? ColorPicked;
    /// <summary>像素发生变化（用于未保存标记）。</summary>
    public event Action? PixelsChanged;
    /// <summary>一笔结束；参数为这一笔之前的画布快照（供撤销历史）。</summary>
    public event Action<int[]>? StrokeCommitted;

    /// <summary>最近一笔是否实际改变了像素（MainForm 据此决定是否入历史）。</summary>
    public bool LastStrokeChanged { get; private set; }

    private bool _drawing;
    private bool _strokeChanged;
    private int[]? _strokeBefore;
    private (int X, int Y)? _lastPaintCell;
    private (int X, int Y)? _hover;
    private (int X, int Y) _shapeStart;
    private (int X, int Y)? _lastPreviewCell;
    private (int X, int Y)? _pendingPreviewCell;
    private long _lastGradientPreviewAt;
    private bool _strokeUsesPreview;
    private ToolKind _strokeTool;
    private int _interactionGeneration;
    private Rectangle? _selection;
    private Rectangle _selectionOrigin;
    private bool _selecting, _movingSelection;
    private int[]? _copiedPixels;
    private Size _copiedSize;
    private bool IsShape => Tool is ToolKind.Line or ToolKind.Rectangle or ToolKind.Ellipse or ToolKind.Gradient;
    private bool _panModifier;
    private bool _panning;
    public bool InteractionInProgress => _drawing || _selecting || _movingSelection || _panning;
    private MouseButtons _panButton;
    private Point _panStart;
    private Point _panScrollStart;

    public void SetPanModifier(bool active)
    {
        _panModifier = active;
        if (!active && _panButton == MouseButtons.Left) _panning = false;
        Cursor = active || _panning ? Cursors.Hand : Cursors.Cross;
    }

    public CanvasControl(PixelGrid grid)
    {
        _grid = grid;
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Cross;
        RecreateBitmap();
    }

    /// <summary>替换数据网格（切换尺寸 / 载入工程后调用）。</summary>
    public void SetGrid(PixelGrid grid)
    {
        _grid = grid;
        _drawing = false;
        _strokeChanged = false;
        _strokeBefore = null;
        _lastPaintCell = null;
        _hover = null;
        _selection = null;
        _selecting = _movingSelection = false;
        _lastPreviewCell = _pendingPreviewCell = null;
        RecreateBitmap();
        Invalidate();
    }

    /// <summary>整体刷新显示（撤销 / 重做 / 清空等数据整体变化后调用）。</summary>
    public void RefreshFromData()
    {
        _selection = null;
        RefreshBitmapFull();
        Invalidate();
    }

    /// <summary>导出用原生位图（无网格线、保留透明）。</summary>
    public Bitmap NativeBitmap => _bitmap;

    public int Zoom => _zoom;
    public event Action? ZoomChanged;

    // ---------- 缩放 ----------

    /// <summary>根据可用区域重新计算整数缩放倍数（至少 1 倍；1 倍仍超出时由外层面板滚动）。</summary>
    public void UpdateZoom(Size available)
    {
        int zoom = Math.Clamp(Math.Min(available.Width / _grid.Width, available.Height / _grid.Height), 1, 128);
        SetZoom(zoom);
    }

    public void SetZoom(int zoom)
    {
        zoom = Math.Clamp(zoom, 1, 128);
        var want = new Size(_grid.Width * zoom, _grid.Height * zoom);
        if (zoom != _zoom || Size != want)
        {
            _zoom = zoom;
            Size = want;
            Invalidate();
            ZoomChanged?.Invoke();
        }
    }

    // ---------- 渲染 ----------

    private void RecreateBitmap()
    {
        _bitmap?.Dispose();
        _bitmap = new Bitmap(_grid.Width, _grid.Height, PixelFormat.Format32bppArgb);
        _compositePixels = new int[_grid.Width * _grid.Height];
        _compositeRow = new int[_grid.Width];
        RefreshBitmapFull();
    }

    /// <summary>把像素数组整块写入原生位图（一次 LockBits + Marshal.Copy，最大约 1MB）。</summary>
    private void RefreshBitmapFull()
    {
        var rect = new Rectangle(0, 0, _grid.Width, _grid.Height);
        BitmapData data = _bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            if (Layers is null)
                Marshal.Copy(_grid.RawPixels, 0, data.Scan0, _grid.RawPixels.Length);
            else
            {
                var pixels = _compositePixels;
                Array.Clear(pixels);
                foreach (var layer in Layers)
                {
                    if (!layer.Visible) continue;
                    var source = layer.Grid.RawPixels;
                    for (int i = 0; i < pixels.Length; i++)
                        if (source[i] != Argb.Transparent) pixels[i] = source[i];
                }
                Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            }
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }
    }

    private void RefreshBitmapRegion(Rectangle region)
    {
        region = Rectangle.Intersect(region, new Rectangle(0, 0, _grid.Width, _grid.Height));
        if (region.IsEmpty) return;
        BitmapData data = _bitmap.LockBits(region, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < region.Height; y++)
            {
                int offset = (region.Y + y) * _grid.Width + region.X;
                if (Layers is null) Array.Copy(_grid.RawPixels, offset, _compositeRow, 0, region.Width);
                else
                {
                    Array.Clear(_compositeRow, 0, region.Width);
                    foreach (var layer in Layers)
                    {
                        if (!layer.Visible) continue;
                        var source = layer.Grid.RawPixels;
                        for (int x = 0; x < region.Width; x++)
                            if (source[offset + x] != Argb.Transparent) _compositeRow[x] = source[offset + x];
                    }
                }
                Marshal.Copy(_compositeRow, 0, IntPtr.Add(data.Scan0, y * data.Stride), region.Width);
            }
        }
        finally { _bitmap.UnlockBits(data); }
    }

    /// <summary>棋盘格纹理画刷（透明格指示，浅灰/白相间；按格子显示尺寸缓存）。</summary>
    private Brush GetCheckerBrush()
    {
        int cell = _zoom;
        if (_checkerCell == cell && _checkerBrush is TextureBrush) return _checkerBrush;

        _checkerBrush?.Dispose();
        _checkerBmp?.Dispose();
        _checkerBmp = new Bitmap(cell * 2, cell * 2);
        using (var g = Graphics.FromImage(_checkerBmp))
        {
            g.Clear(Color.White);                                       // 白
            using var dark = new SolidBrush(Color.FromArgb(0xCC, 0xCC, 0xCC)); // 浅灰
            g.FillRectangle(dark, 0, 0, cell, cell);
            g.FillRectangle(dark, cell, cell, cell, cell);
        }
        _checkerCell = cell;
        _checkerBrush = new TextureBrush(_checkerBmp) { WrapMode = WrapMode.Tile };
        return _checkerBrush;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var dest = new Rectangle(0, 0, _grid.Width * _zoom, _grid.Height * _zoom);

        g.InterpolationMode = InterpolationMode.NearestNeighbor;   // 最近邻：像素锐利
        g.PixelOffsetMode = PixelOffsetMode.Half;

        // 1) 透明格棋盘格
        g.FillRectangle(GetCheckerBrush(), dest);

        // 参考图只参与屏幕显示，不改变工程像素或导出位图。
        if (ShowReference && _referenceImage is not null && _referenceOpacity > 0)
        {
            using var attributes = new ImageAttributes();
            var matrix = new ColorMatrix { Matrix33 = _referenceOpacity };
            attributes.SetColorMatrix(matrix);
            g.DrawImage(_referenceImage, dest, 0, 0, _referenceImage.Width, _referenceImage.Height,
                GraphicsUnit.Pixel, attributes);
        }

        // 洋葱皮只用于显示，位于当前帧下方，不写入合成位图或工程。
        if (_onionPrevious is not null) g.DrawImage(_onionPrevious, dest, 0, 0, _grid.Width, _grid.Height, GraphicsUnit.Pixel);
        if (_onionNext is not null) g.DrawImage(_onionNext, dest, 0, 0, _grid.Width, _grid.Height, GraphicsUnit.Pixel);
        // 2) 像素数据（透明像素透出参考图和棋盘格）
        g.DrawImage(_bitmap, dest, 0, 0, _grid.Width, _grid.Height, GraphicsUnit.Pixel);

        // 3) 网格线：格子显示尺寸小于 4px 时自动隐藏（无论开关状态）
        if (ShowGrid && _zoom >= 4)
        {
            using var pen = new Pen(Color.FromArgb(170, 170, 170));
            for (int x = 1; x < _grid.Width; x++)
                g.DrawLine(pen, x * _zoom, 0, x * _zoom, dest.Height - 1);
            for (int y = 1; y < _grid.Height; y++)
                g.DrawLine(pen, 0, y * _zoom, dest.Width - 1, y * _zoom);
        }

        if (_selection is { } selection && Tool == ToolKind.Selection)
        {
            using var selectionPen = new Pen(Color.DodgerBlue, 2) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(selectionPen, selection.X * _zoom, selection.Y * _zoom,
                Math.Max(1, selection.Width * _zoom - 1), Math.Max(1, selection.Height * _zoom - 1));
        }

        // 4) 悬停高亮：当前笔刷实际覆盖范围
        if (_hover is { } h && Tool != ToolKind.Selection)
        {
            int size = EffectiveBrushSize();
            int x1 = Math.Min(h.X + size - 1, _grid.Width - 1);
            int y1 = Math.Min(h.Y + size - 1, _grid.Height - 1);
            var r = Rectangle.FromLTRB(h.X * _zoom, h.Y * _zoom, (x1 + 1) * _zoom - 1, (y1 + 1) * _zoom - 1);
            using var pen = new Pen(Color.Red);
            g.DrawRectangle(pen, r);
        }
    }

    private int EffectiveBrushSize() => Tool == ToolKind.Eyedropper ? 1 : BrushSize;

    // ---------- 鼠标交互 ----------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        if ((e.Button == MouseButtons.Middle || (_panModifier && e.Button == MouseButtons.Left))
            && Parent is ScrollableControl parent)
        {
            _interactionGeneration++;
            _panning = true;
            _panButton = e.Button;
            _panStart = PointToScreen(e.Location);
            _panScrollStart = parent.AutoScrollPosition;
            Cursor = Cursors.Hand;
            Capture = true;
            return;
        }

        var cell = PointToCell(e.Location);
        if (e.Button == MouseButtons.Right)
        {
            Eyedrop(cell);                       // 右键始终为快速取色（不附加右键菜单）
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (!Editable) return;
        _interactionGeneration++;

        if (Tool == ToolKind.Selection)
        {
            _shapeStart = cell;
            if (_selection is { } selected && selected.Contains(cell.X, cell.Y))
            {
                _movingSelection = true;
                _selectionOrigin = selected;
                _strokeBefore = _grid.Snapshot();
                _strokeChanged = false;
            }
            else
            {
                _selecting = true;
                _selection = new Rectangle(cell.X, cell.Y, 1, 1);
                Invalidate();
            }
            return;
        }

        _strokeBefore = _grid.Snapshot();        // 一笔开始：记录之前状态
        _strokeChanged = false;
        _lastPaintCell = null;
        _drawing = true;
        _strokeTool = Tool;
        _strokeUsesPreview = IsShape;
        if (_strokeUsesPreview)
        {
            _shapeStart = cell;
            _lastPreviewCell = _pendingPreviewCell = null;
            _lastGradientPreviewAt = 0;
            PreviewShape(cell);
            return;
        }
        ApplyTool(cell, isStart: true);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_panning && Parent is ScrollableControl parent)
        {
            Point current = PointToScreen(e.Location);
            parent.AutoScrollPosition = new Point(
                Math.Max(0, -_panScrollStart.X - (current.X - _panStart.X)),
                Math.Max(0, -_panScrollStart.Y - (current.Y - _panStart.Y)));
            return;
        }
        var cell = PointToCell(e.Location);
        SetHover(cell);
        if (_selecting && e.Button == MouseButtons.Left)
        {
            _selection = Rectangle.FromLTRB(Math.Min(_shapeStart.X, cell.X), Math.Min(_shapeStart.Y, cell.Y),
                Math.Max(_shapeStart.X, cell.X) + 1, Math.Max(_shapeStart.Y, cell.Y) + 1);
            Invalidate();
            return;
        }
        if (_movingSelection && e.Button == MouseButtons.Left)
        {
            PreviewSelectionMove(cell);
            return;
        }
        if (_drawing && e.Button == MouseButtons.Left)
        {
            if (_strokeUsesPreview)
            {
                if (_strokeTool == ToolKind.Gradient && Environment.TickCount64 - _lastGradientPreviewAt < 40)
                    _pendingPreviewCell = cell;
                else PreviewShape(cell);
            }
            else ApplyTool(cell, isStart: false);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_panning && e.Button == _panButton)
        {
            _panning = false;
            _panButton = MouseButtons.None;
            Cursor = _panModifier ? Cursors.Hand : Cursors.Cross;
            Capture = false;
            return;
        }
        if (e.Button == MouseButtons.Left && _selecting)
        {
            _selecting = false;
            return;
        }
        if (e.Button == MouseButtons.Left && _movingSelection)
        {
            PreviewSelectionMove(PointToCell(e.Location));
            _movingSelection = false;
            if (_strokeChanged) PixelsChanged?.Invoke();
            EndStroke();
            return;
        }
        if (e.Button == MouseButtons.Left && _drawing)
        {
            if (_strokeUsesPreview)
            {
                PreviewShape(PointToCell(e.Location));
                if (_strokeChanged) PixelsChanged?.Invoke();
            }
            EndStroke();
        }
    }

    private void PreviewSelectionMove((int X, int Y) cell)
    {
        if (_strokeBefore is null) return;
        Rectangle source = _selectionOrigin;
        int left = Math.Clamp(source.X + cell.X - _shapeStart.X, 0, _grid.Width - source.Width);
        int top = Math.Clamp(source.Y + cell.Y - _shapeStart.Y, 0, _grid.Height - source.Height);
        _grid.Restore(_strokeBefore);
        if (left != source.X || top != source.Y)
        {
            for (int y = 0; y < source.Height; y++)
                for (int x = 0; x < source.Width; x++) _grid.Set(source.X + x, source.Y + y, Argb.Transparent);
            for (int y = 0; y < source.Height; y++)
                for (int x = 0; x < source.Width; x++)
                {
                    int color = _strokeBefore[(source.Y + y) * _grid.Width + source.X + x];
                    if (color != Argb.Transparent) _grid.Set(left + x, top + y, color);
                }
        }
        _selection = new Rectangle(left, top, source.Width, source.Height);
        _strokeChanged = !_grid.RawPixels.AsSpan().SequenceEqual(_strokeBefore);
        RefreshBitmapFull();
        Invalidate();
    }

    public void CopySelection()
    {
        if (_selection is not { } selection) return;
        _copiedSize = selection.Size;
        _copiedPixels = new int[selection.Width * selection.Height];
        for (int y = 0; y < selection.Height; y++)
            for (int x = 0; x < selection.Width; x++)
                _copiedPixels[y * selection.Width + x] = _grid.Get(selection.X + x, selection.Y + y);
    }

    internal Rectangle? SelectionBounds => _selection;
    internal bool HasCopiedSelection => _copiedPixels is not null;
    internal void SetSelection(Rectangle? selection)
    {
        if (selection is { } r && (r.X < 0 || r.Y < 0 || r.Width < 1 || r.Height < 1 || r.Width > _grid.Width || r.Height > _grid.Height || r.X > _grid.Width - r.Width || r.Y > _grid.Height - r.Height))
            throw new ArgumentException("选区必须完整位于画布内。");
        _selection = selection; Invalidate();
    }

    public void PasteSelection()
    {
        if (!Editable || _copiedPixels is null) return;
        int width = Math.Min(_copiedSize.Width, _grid.Width), height = Math.Min(_copiedSize.Height, _grid.Height);
        int left = Math.Clamp((_selection?.X ?? -1) + 1, 0, _grid.Width - width);
        int top = Math.Clamp((_selection?.Y ?? -1) + 1, 0, _grid.Height - height);
        int[] before = _grid.Snapshot();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int color = _copiedPixels[y * _copiedSize.Width + x];
                if (color != Argb.Transparent) _grid.Set(left + x, top + y, color);
            }
        _selection = new Rectangle(left, top, width, height);
        LastStrokeChanged = !_grid.RawPixels.AsSpan().SequenceEqual(before);
        if (LastStrokeChanged)
        {
            RefreshBitmapFull();
            PixelsChanged?.Invoke();
            StrokeCommitted?.Invoke(before);
        }
        Invalidate();
    }

    private void PreviewShape((int X, int Y) end)
    {
        if (_strokeBefore is null) return;
        _pendingPreviewCell = null;
        if (_lastPreviewCell == end) return;
        _lastPreviewCell = end;
        if (_strokeTool == ToolKind.Gradient) _lastGradientPreviewAt = Environment.TickCount64;
        _grid.Restore(_strokeBefore);
        _strokeChanged = _strokeTool == ToolKind.Gradient
            ? PaintOps.GradientFill(_grid, _shapeStart.X, _shapeStart.Y, end.X, end.Y, CurrentColor, GradientEndColor, MirrorH, MirrorV)
            : PaintOps.DrawShape(_grid, _strokeTool, _shapeStart.X, _shapeStart.Y, end.X, end.Y, BrushSize, CurrentColor, MirrorH, MirrorV);
        RefreshBitmapFull();
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(null);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (Capture || !InteractionInProgress || !IsHandleCreated || IsDisposed) return;

        // 鼠标正常松开时也可能先触发捕获变化；延后到该消息处理完再判断。
        int generation = _interactionGeneration;
        try
        {
            BeginInvoke((Action)(() =>
            {
                if (IsDisposed || !IsHandleCreated || Capture || generation != _interactionGeneration) return;
                FinishInterruptedInteraction();
            }));
        }
        catch (InvalidOperationException) { /* 控件正在销毁 */ }
    }

    private void FinishInterruptedInteraction()
    {
        _panning = false;
        _panButton = MouseButtons.None;
        Cursor = _panModifier ? Cursors.Hand : Cursors.Cross;
        if (_selecting)
        {
            _selecting = false;
            Invalidate();
        }
        if (_movingSelection)
        {
            _movingSelection = false;
            if (_strokeChanged) PixelsChanged?.Invoke();
            EndStroke();
        }
        else if (_drawing)
        {
            if (_strokeUsesPreview && _pendingPreviewCell is { } pending) PreviewShape(pending);
            if (_strokeUsesPreview && _strokeChanged) PixelsChanged?.Invoke();
            EndStroke();
        }
    }

    private void EndStroke()
    {
        _drawing = false;
        _strokeUsesPreview = false;
        _lastPaintCell = null;
        _lastPreviewCell = _pendingPreviewCell = null;
        int[]? before = _strokeBefore;
        _strokeBefore = null;
        LastStrokeChanged = _strokeChanged;
        _strokeChanged = false;
        if (before != null)
            StrokeCommitted?.Invoke(before);
    }

    /// <summary>鼠标坐标 → 格子索引（按规范 clamp 到有效范围）。</summary>
    private (int X, int Y) PointToCell(Point p)
    {
        int x = Math.Clamp(p.X / _zoom, 0, _grid.Width - 1);
        int y = Math.Clamp(p.Y / _zoom, 0, _grid.Height - 1);
        return (x, y);
    }

    private void ApplyTool((int X, int Y) cell, bool isStart)
    {
        switch (Tool)
        {
            case ToolKind.Pencil:
                PaintStroke(cell, CurrentColor);
                break;
            case ToolKind.Eraser:
                PaintStroke(cell, Argb.Transparent);
                break;
            case ToolKind.Eyedropper:
                if (isStart) Eyedrop(cell);       // 取色器：单击生效
                break;
            case ToolKind.Fill:
                if (isStart && PaintOps.FloodFill(_grid, cell.X, cell.Y, CurrentColor, MirrorH, MirrorV))
                    OnPixelsChanged();            // 油漆桶：单击生效
                break;
        }
    }

    /// <summary>画笔/橡皮一笔：对上一格到当前格之间做 Bresenham 插值，快速拖动也保持连续。</summary>
    private void PaintStroke((int X, int Y) to, int argb)
    {
        var from = _lastPaintCell ?? to;
        _lastPaintCell = to;
        int x = from.X, y = from.Y;
        int dx = Math.Abs(to.X - x), dy = Math.Abs(to.Y - y);
        int sx = x < to.X ? 1 : -1, sy = y < to.Y ? 1 : -1;
        int err = dx - dy;
        bool changed = false;
        while (true)
        {
            if (PaintOps.ApplyBrush(_grid, x, y, BrushSize, argb, MirrorH, MirrorV)) changed = true;
            if (x == to.X && y == to.Y) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x += sx; }
            if (e2 < dx) { err += dx; y += sy; }
        }
        if (changed)
        {
            int left = Math.Min(from.X, to.X), top = Math.Min(from.Y, to.Y);
            int right = Math.Min(_grid.Width, Math.Max(from.X, to.X) + BrushSize);
            int bottom = Math.Min(_grid.Height, Math.Max(from.Y, to.Y) + BrushSize);
            var region = Rectangle.FromLTRB(left, top, right, bottom);
            RefreshBitmapRegion(region);
            if (MirrorH) RefreshBitmapRegion(new Rectangle(_grid.Width - region.Right, region.Y, region.Width, region.Height));
            if (MirrorV) RefreshBitmapRegion(new Rectangle(region.X, _grid.Height - region.Bottom, region.Width, region.Height));
            if (MirrorH && MirrorV) RefreshBitmapRegion(new Rectangle(_grid.Width - region.Right, _grid.Height - region.Bottom, region.Width, region.Height));
            _strokeChanged = true;
            Invalidate();
            PixelsChanged?.Invoke();
        }
    }

    /// <summary>取色：仅当格子有颜色时生效；透明格不改变当前颜色。</summary>
    private void Eyedrop((int X, int Y) cell)
    {
        // 合成位图与画布上可见的图层一致，不包含参考图和网格。
        int c = _bitmap.GetPixel(cell.X, cell.Y).ToArgb();
        if (c == Argb.Transparent) return;         // 透明格不改变当前颜色
        CurrentColor = c;
        ColorPicked?.Invoke(c);
    }

    private void OnPixelsChanged()
    {
        RefreshBitmapFull();
        _strokeChanged = true;
        Invalidate();
        PixelsChanged?.Invoke();
    }

    private void SetHover((int X, int Y)? cell)
    {
        if (_hover == cell) return;
        _hover = cell;
        // 悬停框用整块重绘：局部矩形失效无法可靠清除旧框线（实测移动悬停后旧框的
        // 部分边缘仍残留，累积成鼠标划过后的红色拖尾），整块重绘保证画布状态干净。
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bitmap?.Dispose();
            _checkerBrush?.Dispose();
            _checkerBmp?.Dispose();
            _referenceImage?.Dispose();
            _onionPrevious?.Dispose();
            _onionNext?.Dispose();
        }
        base.Dispose(disposing);
    }
}
