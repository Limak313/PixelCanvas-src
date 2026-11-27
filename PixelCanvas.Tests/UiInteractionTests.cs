using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using PixelCanvas;
using Xunit;

namespace PixelCanvas.Tests;

/// <summary>
/// 控件事件层：不移动真实鼠标，直接向 CanvasControl 发送 WinForms 鼠标消息，
/// 验证坐标映射、绘制、镜像、选区与工具快捷键。所有用例只需要窗口句柄，可进无头 CI。
/// </summary>
public sealed class UiInteractionTests
{
    private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    private static void Mouse(Control control, string method, MouseButtons button, int x, int y)
    {
        control.GetType().GetMethod(method, Flags)!.Invoke(
            control, new object[] { new MouseEventArgs(button, 1, x, y, 0) });
    }

    private static void MouseDown(Control control, int x, int y, MouseButtons button = MouseButtons.Left) =>
        Mouse(control, "OnMouseDown", button, x, y);

    private static void MouseMove(Control control, int x, int y) =>
        Mouse(control, "OnMouseMove", MouseButtons.Left, x, y);

    private static void MouseUp(Control control, int x, int y) =>
        Mouse(control, "OnMouseUp", MouseButtons.Left, x, y);

    [Fact]
    [Trait("Category", "UiHost")]
    public void Canvas_LeftClickPaintsAndCommitsStroke()
    {
        UiTestSupport.RunInSta(() =>
        {
            using var canvas = new CanvasControl(new PixelGrid(4, 4));
            canvas.SetZoom(10);
            canvas.CreateControl();
            canvas.Tool = ToolKind.Pencil;
            canvas.CurrentColor = Argb.FromRgb(0xFF0000);

            int[]? committed = null;
            int changed = 0;
            canvas.StrokeCommitted += before => committed = before;
            canvas.PixelsChanged += () => changed++;

            MouseDown(canvas, 15, 15);
            MouseUp(canvas, 15, 15);

            var grid = UiTestSupport.Field<PixelGrid>(canvas, "_grid");
            Assert.Equal(Argb.FromRgb(0xFF0000), grid.Get(1, 1));
            Assert.True(canvas.LastStrokeChanged);
            Assert.Equal(1, changed);
            Assert.NotNull(committed);
            Assert.Equal(Argb.Transparent, committed![1 * 4 + 1]);
        });
    }

    [Fact]
    [Trait("Category", "UiHost")]
    public void Canvas_DragPaintsContinuousLine()
    {
        UiTestSupport.RunInSta(() =>
        {
            using var canvas = new CanvasControl(new PixelGrid(6, 3));
            canvas.SetZoom(10);
            canvas.CreateControl();
            canvas.Tool = ToolKind.Pencil;
            canvas.CurrentColor = Argb.FromRgb(0x00FF00);

            MouseDown(canvas, 5, 5);
            MouseMove(canvas, 15, 5);
            MouseMove(canvas, 45, 5);
            MouseUp(canvas, 45, 5);

            var grid = UiTestSupport.Field<PixelGrid>(canvas, "_grid");
            for (int x = 0; x < 5; x++) Assert.Equal(Argb.FromRgb(0x00FF00), grid.Get(x, 0));
            Assert.Equal(Argb.Transparent, grid.Get(0, 1));
        });
    }

    [Fact]
    [Trait("Category", "UiHost")]
    public void Canvas_RightClickPicksColorWithoutEditing()
    {
        UiTestSupport.RunInSta(() =>
        {
            var grid = new PixelGrid(4, 4);
            grid.Set(1, 1, Argb.FromRgb(0x123456));
            using var canvas = new CanvasControl(grid);
            canvas.SetZoom(10);
            canvas.CreateControl();
            canvas.RefreshFromData();
            canvas.Tool = ToolKind.Pencil;
            canvas.CurrentColor = Argb.FromRgb(0xFF0000);

            int? picked = null;
            canvas.ColorPicked += color => picked = color;

            MouseDown(canvas, 15, 15, MouseButtons.Right);

            Assert.Equal(Argb.FromRgb(0x123456), picked);
            Assert.Equal(Argb.FromRgb(0x123456), grid.Get(1, 1));
            Assert.False(canvas.LastStrokeChanged);
        });
    }

    [Fact]
    [Trait("Category", "UiHost")]
    public void Canvas_MirrorPaintCoversAllQuadrants()
    {
        UiTestSupport.RunInSta(() =>
        {
            using var canvas = new CanvasControl(new PixelGrid(5, 5));
            canvas.SetZoom(10);
            canvas.CreateControl();
            canvas.Tool = ToolKind.Pencil;
            canvas.CurrentColor = Argb.FromRgb(0x0000FF);
            canvas.MirrorH = true;
            canvas.MirrorV = true;

            MouseDown(canvas, 5, 15);
            MouseUp(canvas, 5, 15);

            var grid = UiTestSupport.Field<PixelGrid>(canvas, "_grid");
            Assert.Equal(Argb.FromRgb(0x0000FF), grid.Get(0, 1));
            Assert.Equal(Argb.FromRgb(0x0000FF), grid.Get(4, 1));
            Assert.Equal(Argb.FromRgb(0x0000FF), grid.Get(0, 3));
            Assert.Equal(Argb.FromRgb(0x0000FF), grid.Get(4, 3));
        });
    }

    [Fact]
    [Trait("Category", "UiHost")]
    public void Canvas_SelectionDragThenMoveLeavesHole()
    {
        UiTestSupport.RunInSta(() =>
        {
            var grid = new PixelGrid(12, 12);
            for (int y = 2; y < 5; y++)
                for (int x = 2; x < 5; x++) grid.Set(x, y, Argb.FromRgb(0xFF0000));
            using var canvas = new CanvasControl(grid);
            canvas.SetZoom(10);
            canvas.CreateControl();
            canvas.RefreshFromData();
            canvas.Tool = ToolKind.Selection;

            MouseDown(canvas, 25, 25);
            MouseMove(canvas, 45, 45);
            MouseUp(canvas, 45, 45);
            Assert.Equal(new Rectangle(2, 2, 3, 3), canvas.SelectionBounds);

            MouseDown(canvas, 25, 25);
            MouseMove(canvas, 85, 85);
            MouseUp(canvas, 85, 85);

            Assert.Equal(Argb.Transparent, grid.Get(2, 2));
            Assert.Equal(Argb.FromRgb(0xFF0000), grid.Get(8, 8));
            Assert.Equal(new Rectangle(8, 8, 3, 3), canvas.SelectionBounds);
            Assert.True(canvas.LastStrokeChanged);
        });
    }

    [Fact]
    [Trait("Category", "UiHost")]
    public void Keyboard_ToolShortcutsSwitchTools()
    {
        UiTestSupport.RunInSta(() =>
        {
            using var form = new MainForm(5, 5);
            form.CreateControl();
            var canvas = UiTestSupport.Field<CanvasControl>(form, "_canvas");

            foreach (var (key, expected) in new[]
            {
                (Keys.B, ToolKind.Pencil),
                (Keys.E, ToolKind.Eraser),
                (Keys.I, ToolKind.Eyedropper),
                (Keys.G, ToolKind.Fill),
                (Keys.L, ToolKind.Line),
                (Keys.R, ToolKind.Rectangle),
                (Keys.O, ToolKind.Ellipse),
                (Keys.M, ToolKind.Selection),
                (Keys.D, ToolKind.Gradient),
            })
            {
                UiTestSupport.Invoke(form, "OnKeyDown", new KeyEventArgs(key));
                Assert.Equal(expected, canvas.Tool);
            }
        });
    }

    [Fact]
    [Trait("Category", "UiHost")]
    public void Keyboard_MirrorGridBrushAndUndoRedo()
    {
        UiTestSupport.RunInSta(() =>
        {
            using var form = new MainForm(5, 5);
            form.CreateControl();
            var canvas = UiTestSupport.Field<CanvasControl>(form, "_canvas");
            var mirrorH = UiTestSupport.Field<CheckBox>(form, "_mirrorHCheck");
            var mirrorV = UiTestSupport.Field<CheckBox>(form, "_mirrorVCheck");
            var gridCheck = UiTestSupport.Field<CheckBox>(form, "_gridCheck");
            var brush = UiTestSupport.Field<NumericUpDown>(form, "_brushSize");

            bool wasMirrorH = mirrorH.Checked;
            bool wasMirrorV = mirrorV.Checked;
            bool wasGrid = gridCheck.Checked;
            UiTestSupport.Invoke(form, "OnKeyDown", new KeyEventArgs(Keys.H));
            UiTestSupport.Invoke(form, "OnKeyDown", new KeyEventArgs(Keys.V));
            UiTestSupport.Invoke(form, "OnKeyDown", new KeyEventArgs(Keys.T));
            Assert.NotEqual(wasMirrorH, mirrorH.Checked);
            Assert.NotEqual(wasMirrorV, mirrorV.Checked);
            Assert.NotEqual(wasGrid, gridCheck.Checked);

            int before = (int)brush.Value;
            UiTestSupport.Invoke(form, "OnKeyDown", new KeyEventArgs(Keys.Oemplus));
            Assert.Equal(Math.Min(brush.Maximum, before + 1), brush.Value);
            UiTestSupport.Invoke(form, "OnKeyDown", new KeyEventArgs(Keys.OemMinus));
            Assert.Equal(before, brush.Value);

            UiTestSupport.Handle(form, new { action = "paint", arguments = new { pixels = new[] { new { x = 0, y = 0, color = "#FF0000" } } } });
            var grid = UiTestSupport.Field<PixelGrid>(form, "_grid");
            Assert.Equal(Argb.FromRgb(0xFF0000), grid.Get(0, 0));
            var undo = new KeyEventArgs(Keys.Control | Keys.Z);
            UiTestSupport.Invoke(form, "OnKeyDown", undo);
            Assert.True(undo.Handled);
            Assert.Equal(Argb.Transparent, UiTestSupport.Field<PixelGrid>(form, "_grid").Get(0, 0));

            var redo = new KeyEventArgs(Keys.Control | Keys.Y);
            UiTestSupport.Invoke(form, "OnKeyDown", redo);
            Assert.True(redo.Handled);
            Assert.Equal(Argb.FromRgb(0xFF0000), UiTestSupport.Field<PixelGrid>(form, "_grid").Get(0, 0));
        });
    }

    [Fact]
    [Trait("Category", "UiHost")]
    public void Keyboard_ImmersiveF11AndEscapeRestores()
    {
        UiTestSupport.RunInSta(() =>
        {
            using var form = new MainForm(5, 5);
            form.CreateControl();
            Assert.False(UiTestSupport.Field<bool>(form, "_immersive"));

            UiTestSupport.Invoke(form, "OnKeyDown", new KeyEventArgs(Keys.F11));
            Assert.True(UiTestSupport.Field<bool>(form, "_immersive"));

            UiTestSupport.Invoke(form, "OnKeyDown", new KeyEventArgs(Keys.Escape));
            Assert.False(UiTestSupport.Field<bool>(form, "_immersive"));
        });
    }
}
