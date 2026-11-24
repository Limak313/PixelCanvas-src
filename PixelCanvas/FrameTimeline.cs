using System.Drawing.Drawing2D;

namespace PixelCanvas;

/// <summary>横向帧缩略图；仅绘制滚动区域中可见的帧。</summary>
internal sealed class FrameTimeline : Control
{
    internal const int CellWidth = 78;
    internal readonly record struct FrameDrop(int Pressed, int Target, bool Move);
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal IReadOnlyList<AnimationFrame> Frames { get; set; } = Array.Empty<AnimationFrame>();
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int SelectedFrame { get; set; }
    internal event Action<int>? FrameSelected;
    internal event Action<int, int>? FrameMoved;
    private int _pressed = -1;
    private Point _down;

    internal FrameTimeline()
    {
        DoubleBuffered = true; Height = 84; AccessibleName = "动画帧缩略图时间轴";
        SetStyle(ControlStyles.Selectable, true); TabStop = true;
    }

    internal void UpdateFrames(IReadOnlyList<AnimationFrame> frames, int selected)
    {
        Frames = frames; SelectedFrame = selected; Width = Math.Max(1, frames.Count * CellWidth); Invalidate();
    }

    internal void InvalidateSelected() => Invalidate(new Rectangle(SelectedFrame * CellWidth, 0, CellWidth, Height));

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int delta = e.KeyCode == Keys.Left ? -1 : e.KeyCode == Keys.Right ? 1 : 0;
        int target = SelectedFrame + delta;
        if (delta == 0 || target < 0 || target >= Frames.Count) return;
        if (e.Control) FrameMoved?.Invoke(SelectedFrame, target); else FrameSelected?.Invoke(target);
        e.Handled = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(Color.FromArgb(30, 34, 43));
        int first = Math.Max(0, e.ClipRectangle.Left / CellWidth), last = Math.Min(Frames.Count - 1, e.ClipRectangle.Right / CellWidth);
        for (int i = first; i <= last; i++)
        {
            var frame = Frames[i]; var grid = frame.Layers[0].Grid;
            var cell = new Rectangle(i * CellWidth + 3, 3, CellWidth - 6, 76);
            using var background = new SolidBrush(i == SelectedFrame ? Color.FromArgb(62, 78, 99) : Color.FromArgb(48, 53, 65));
            using var border = new Pen(i == SelectedFrame ? Color.FromArgb(105, 210, 245) : Color.FromArgb(80, 87, 100), i == SelectedFrame ? 2 : 1);
            e.Graphics.FillRectangle(background, cell); e.Graphics.DrawRectangle(border, cell);
            int w = Math.Max(1, 50 * grid.Width / Math.Max(grid.Width, grid.Height));
            int h = Math.Max(1, 50 * grid.Height / Math.Max(grid.Width, grid.Height));
            using var image = new Bitmap(w, h);
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                int source = (y * grid.Height / h) * grid.Width + x * grid.Width / w;
                int color = ((x / 5 + y / 5) % 2 == 0 ? Color.White : Color.LightGray).ToArgb();
                foreach (var layer in frame.Layers) if (layer.Visible && layer.Grid.RawPixels[source] != Argb.Transparent) color = layer.Grid.RawPixels[source];
                image.SetPixel(x, y, Color.FromArgb(color));
            }
            e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            e.Graphics.DrawImageUnscaled(image, cell.X + (cell.Width - w) / 2, cell.Y + 3 + (50 - h) / 2);
            TextRenderer.DrawText(e.Graphics, $"帧 {i + 1}", Font, new Rectangle(cell.X, cell.Y + 55, cell.Width, 18), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = e.X / CellWidth; _down = e.Location;
        if (_pressed >= Frames.Count) _pressed = -1;
    }

    /// <summary>
    /// 根据按下/松开坐标决定“移动帧”还是“选中帧”。抽成纯函数，避免测试依赖真实鼠标捕获与屏幕拖拽阈值。
    /// </summary>
    internal static FrameDrop ResolveDrop(int pressed, int downX, int upX, int frameCount, int dragThreshold)
    {
        if (pressed < 0 || pressed >= frameCount) return new(-1, -1, false);
        int target = Math.Clamp(upX / CellWidth, 0, frameCount - 1);
        bool move = Math.Abs(upX - downX) >= Math.Max(1, dragThreshold) / 2 && target != pressed;
        return new(pressed, target, move);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var drop = ResolveDrop(_pressed, _down.X, e.X, Frames.Count, SystemInformation.DragSize.Width);
        _pressed = -1;
        if (e.Button != MouseButtons.Left || drop.Pressed < 0) return;
        if (drop.Move) FrameMoved?.Invoke(drop.Pressed, drop.Target);
        else FrameSelected?.Invoke(drop.Pressed);
    }

    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) _pressed = -1; }
}
