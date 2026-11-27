using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using PixelCanvas;
using Xunit;

namespace PixelCanvas.Tests;

/// <summary>
/// 画布像素级 golden：只比对 CanvasControl.NativeBitmap（导出用 RGBA，不含窗口边框、
/// 网格线和悬停框），避免整窗截图在不同 DPI/主题下漂移。基线缺省时用
/// PIXELCANVAS_UPDATE_GOLDEN=1 生成，生成后把 TestData/Golden 下的 PNG 提交。
/// </summary>
public sealed class CanvasGoldenTests
{
    private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    private static void Mouse(Control control, string method, MouseButtons button, int x, int y) =>
        control.GetType().GetMethod(method, Flags)!.Invoke(
            control, new object[] { new MouseEventArgs(button, 1, x, y, 0) });

    private static void Drag(CanvasControl canvas, int x1, int y1, int x2, int y2)
    {
        Mouse(canvas, "OnMouseDown", MouseButtons.Left, x1, y1);
        Mouse(canvas, "OnMouseMove", MouseButtons.Left, x2, y2);
        Mouse(canvas, "OnMouseUp", MouseButtons.Left, x2, y2);
    }

    private static CanvasControl NewCanvas(PixelGrid grid)
    {
        var canvas = new CanvasControl(grid);
        canvas.SetZoom(1);
        canvas.CreateControl();
        return canvas;
    }

    [Fact]
    [Trait("Category", "UiHost")]
    public void Golden_PencilStroke()
    {
        UiTestSupport.RunInSta(() =>
        {
            using var canvas = NewCanvas(new PixelGrid(16, 16));
            canvas.Tool = ToolKind.Pencil;
            canvas.CurrentColor = Argb.FromRgb(0xFF0000);
            Drag(canvas, 0, 0, 15, 15);
            GoldenAssert.Verify("pencil-stroke", canvas.NativeBitmap);
        });
    }

    [Fact]
    [Trait("Category", "UiHost")]
    public void Golden_MirrorQuadrants()
    {
        UiTestSupport.RunInSta(() =>
        {
            using var canvas = NewCanvas(new PixelGrid(16, 16));
            canvas.Tool = ToolKind.Pencil;
            canvas.CurrentColor = Argb.FromRgb(0x00FF00);
            canvas.MirrorH = true;
            canvas.MirrorV = true;
            Drag(canvas, 0, 0, 3, 3);
            GoldenAssert.Verify("mirror-quadrants", canvas.NativeBitmap);
        });
    }

    [Fact]
    [Trait("Category", "UiHost")]
    public void Golden_Gradient()
    {
        UiTestSupport.RunInSta(() =>
        {
            using var canvas = NewCanvas(new PixelGrid(16, 16));
            canvas.Tool = ToolKind.Gradient;
            canvas.CurrentColor = Argb.FromRgb(0xFF0000);
            canvas.GradientEndColor = Argb.FromRgb(0x0000FF);
            Drag(canvas, 0, 0, 15, 0);
            GoldenAssert.Verify("gradient", canvas.NativeBitmap);
        });
    }

    [Fact]
    [Trait("Category", "UiHost")]
    public void Golden_LayerComposite()
    {
        UiTestSupport.RunInSta(() =>
        {
            var bottom = new PixelGrid(16, 16);
            bottom.Set(2, 2, Argb.FromRgb(0xFF0000));
            bottom.Set(3, 2, Argb.FromRgb(0xFF0000));
            var top = new PixelGrid(16, 16);
            top.Set(2, 3, Argb.FromRgb(0x00FF00));
            top.Set(12, 12, Argb.FromRgb(0x0000FF));
            var layers = new List<Layer>
            {
                new("底层", bottom),
                new("顶层", top),
            };
            using var canvas = NewCanvas(layers[0].Grid);
            canvas.Layers = layers;
            canvas.RefreshFromData();
            GoldenAssert.Verify("layer-composite", canvas.NativeBitmap);
        });
    }

    [Fact]
    [Trait("Category", "UiHost")]
    public void Golden_SelectionMove()
    {
        UiTestSupport.RunInSta(() =>
        {
            var grid = new PixelGrid(16, 16);
            for (int y = 2; y < 6; y++)
                for (int x = 2; x < 6; x++) grid.Set(x, y, Argb.FromRgb(0xFF0000));
            using var canvas = NewCanvas(grid);
            canvas.Tool = ToolKind.Selection;
            canvas.SetSelection(new Rectangle(2, 2, 4, 4));
            Drag(canvas, 2, 2, 9, 9);
            GoldenAssert.Verify("selection-move", canvas.NativeBitmap);
        });
    }

    [Fact]
    [Trait("Category", "UiHost")]
    public void Golden_Eraser()
    {
        UiTestSupport.RunInSta(() =>
        {
            var grid = new PixelGrid(16, 16);
            for (int y = 0; y < grid.Height; y++)
                for (int x = 0; x < grid.Width; x++) grid.Set(x, y, Argb.FromRgb(0x336699));
            using var canvas = NewCanvas(grid);
            canvas.Tool = ToolKind.Eraser;
            canvas.BrushSize = 3;
            Drag(canvas, 7, 7, 8, 8);
            GoldenAssert.Verify("eraser", canvas.NativeBitmap);
        });
    }

    [Fact]
    [Trait("Category", "UiHost")]
    public void Golden_ShapeLine()
    {
        UiTestSupport.RunInSta(() =>
        {
            using var canvas = NewCanvas(new PixelGrid(16, 16));
            canvas.Tool = ToolKind.Line;
            canvas.BrushSize = 1;
            canvas.CurrentColor = Argb.FromRgb(0xFFFFFF);
            Drag(canvas, 1, 14, 14, 1);
            GoldenAssert.Verify("shape-line", canvas.NativeBitmap);
        });
    }
}

internal static class GoldenAssert
{
    private const int Tolerance = 1;

    public static void Verify(string name, Bitmap actual)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "TestData", "Golden");
        string path = Path.Combine(dir, name + ".png");
        if (Environment.GetEnvironmentVariable("PIXELCANVAS_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(dir);
            actual.Save(path, ImageFormat.Png);
            return;
        }

        if (!File.Exists(path))
        {
            Assert.Fail($"缺少 golden 基线：{path}。在本机运行 " +
                "PIXELCANVAS_UPDATE_GOLDEN=1 dotnet test ... 生成后提交。");
        }

        using var expected = new Bitmap(path);
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        var differences = new List<string>();
        for (int y = 0; y < actual.Height; y++)
            for (int x = 0; x < actual.Width; x++)
            {
                int left = expected.GetPixel(x, y).ToArgb();
                int right = actual.GetPixel(x, y).ToArgb();
                if (ChannelsDiffer(left, right))
                {
                    if (differences.Count < 8)
                        differences.Add($"({x},{y}) {left:X8} != {right:X8}");
                }
            }
        Assert.True(differences.Count == 0,
            $"golden 不匹配：{name}，前 8 个差异：{string.Join("; ", differences)}");
    }

    private static bool ChannelsDiffer(int left, int right)
    {
        for (int shift = 0; shift < 32; shift += 8)
        {
            int a = (left >> shift) & 0xFF;
            int b = (right >> shift) & 0xFF;
            if (Math.Abs(a - b) > Tolerance) return true;
        }
        return false;
    }
}
