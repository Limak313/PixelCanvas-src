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
    private Bitmap _bitmap = null!;      // 与画布同分辨率的原生位图（导出 PNG 也用它，不含网格线）
    private int _zoom = 1;
    private Brush _checkerBrush = null!;
    private Bitmap? _checkerBmp;         // 棋盘格纹理源图（与画刷同生命周期）
    private int _checkerCell = -1;

    // 当前绘制状态（由 MainForm 注入）
    public ToolKind Tool { get; set; } = ToolKind.Pencil;
    public int BrushSize { get; set; } = 1;
    public int CurrentColor { get; set; } = Argb.FromRgb(0x000000);
    public bool MirrorH { get; set; }
    public bool MirrorV { get; set; }
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
        RecreateBitmap();
        Invalidate();
    }

    /// <summary>整体刷新显示（撤销 / 重做 / 清空等数据整体变化后调用）。</summary>
    public void RefreshFromData()
    {
        RefreshBitmapFull();
        Invalidate();
    }

    /// <summary>导出用原生位图（无网格线、保留透明）。</summary>
    public Bitmap NativeBitmap => _bitmap;

    public int Zoom => _zoom;

    // ---------- 缩放 ----------

    /// <summary>根据可用区域重新计算整数缩放倍数（至少 1 倍；1 倍仍超出时由外层面板滚动）。</summary>
    public void UpdateZoom(Size available)
    {
        int zoom = Math.Max(1, Math.Min(available.Width / _grid.Width, available.Height / _grid.Height));
        var want = new Size(_grid.Width * zoom, _grid.Height * zoom);
        if (zoom != _zoom || Size != want)
        {
            _zoom = zoom;
            Size = want;
            Invalidate();
        }
    }

    // ---------- 渲染 ----------

    private void RecreateBitmap()
    {
        _bitmap?.Dispose();
        _bitmap = new Bitmap(_grid.Width, _grid.Height, PixelFormat.Format32bppArgb);
        RefreshBitmapFull();
    }

    /// <summary>把像素数组整块写入原生位图（一次 LockBits + Marshal.Copy，最大约 1MB）。</summary>
    private void RefreshBitmapFull()
    {
        var rect = new Rectangle(0, 0, _grid.Width, _grid.Height);
        BitmapData data = _bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(_grid.RawPixels, 0, data.Scan0, _grid.RawPixels.Length);
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }
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

        // 2) 像素数据（透明像素透出棋盘格）
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

        // 4) 悬停高亮：当前笔刷实际覆盖范围（按画笔大小与越界裁剪）
        if (_hover is { } h)
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

        var cell = PointToCell(e.Location);
        if (e.Button == MouseButtons.Right)
        {
            Eyedrop(cell);                       // 右键始终为快速取色（不附加右键菜单）
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        _strokeBefore = _grid.Snapshot();        // 一笔开始：记录之前状态
        _strokeChanged = false;
        _lastPaintCell = null;
        _drawing = true;
        ApplyTool(cell, isStart: true);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var cell = PointToCell(e.Location);
        SetHover(cell);
        if (_drawing && e.Button == MouseButtons.Left)
            ApplyTool(cell, isStart: false);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && _drawing)
            EndStroke();                          // 拖出画布后松开也能走到这里（WinForms 自动捕获鼠标）
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(null);
    }

    private void EndStroke()
    {
        _drawing = false;
        _lastPaintCell = null;
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
        if (changed) OnPixelsChanged();
    }

    /// <summary>取色：仅当格子有颜色时生效；透明格不改变当前颜色。</summary>
    private void Eyedrop((int X, int Y) cell)
    {
        int c = _grid.Get(cell.X, cell.Y);
        if (c <= 0) return;                       // 透明（或越界）不改变当前颜色
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
        }
        base.Dispose(disposing);
    }
}
