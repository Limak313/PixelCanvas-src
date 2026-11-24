using System.Text.Json;
using System.Text.Json.Nodes;
using PixelCanvas;
using Xunit;

namespace PixelCanvas.Tests;

/// <summary>
/// 帧结构撤销/重做：新增、删除、移动与批量加帧都进快照栈，并与图层像素历史按动作序号排序。
/// 只依赖窗体句柄，标 Category=UiHost 随 CI 门禁运行。
/// </summary>
[Trait("Category", "UiHost")]
public class FrameUndoTests
{
    private static JsonNode J(object value) => JsonSerializer.SerializeToNode(value)!;
    private static JsonNode Data(object reply) => J(reply)["data"]!;

    private static object Call(MainForm form, string action, object arguments) =>
        UiTestSupport.Handle(form, new { action, arguments });

    private static int FrameCount(MainForm form) =>
        Data(Call(form, "info", new { }))["frameCount"]!.GetValue<int>();

    private static int ActiveFrame(MainForm form) =>
        Data(Call(form, "info", new { }))["activeFrame"]!.GetValue<int>();

    private static string Color(MainForm form, int x, int y) =>
        Data(Call(form, "read_composite", new { x, y }))["color"]!.GetValue<string>();

    private static void Paint(MainForm form, int x, int y, string color) =>
        Call(form, "paint", new { pixels = new[] { new { x, y, color } } });

    private static void Undo(MainForm form) => Call(form, "undo", new { });

    private static void Redo(MainForm form) => Call(form, "redo", new { });

    [Fact]
    public void DeleteFrame_UndoRestoresPixelsAndOrder_RedoRemovesAgain()
    {
        UiTestSupport.RunInSta(() =>
        {
            using var form = new MainForm(4, 4);
            form.CreateControl();

            Paint(form, 0, 0, "#FF0000");
            Call(form, "add_frame", new { copy = false });
            Paint(form, 1, 1, "#00FF00");
            Call(form, "add_frame", new { copy = false });
            Paint(form, 2, 2, "#0000FF");
            Assert.Equal(3, FrameCount(form));

            Call(form, "select_frame", new { frame = 1 });
            Call(form, "delete_frame", new { confirm = true });
            Assert.Equal(2, FrameCount(form));

            Undo(form);
            Assert.Equal(3, FrameCount(form));
            Assert.Equal(1, ActiveFrame(form));
            Assert.Equal("#00FF00", Color(form, 1, 1));
            Call(form, "select_frame", new { frame = 2 });
            Assert.Equal("#0000FF", Color(form, 2, 2));

            Redo(form);
            Assert.Equal(2, FrameCount(form));
            Assert.Equal("#0000FF", Color(form, 2, 2));
        });
    }

    [Fact]
    public void AddFrame_UndoAndRedoKeepPixels()
    {
        UiTestSupport.RunInSta(() =>
        {
            using var form = new MainForm(4, 4);
            form.CreateControl();

            Paint(form, 0, 0, "#FF0000");
            Call(form, "add_frame", new { copy = false });
            Assert.Equal(2, FrameCount(form));
            Assert.Equal(1, ActiveFrame(form));

            Undo(form);
            Assert.Equal(1, FrameCount(form));
            Assert.Equal(0, ActiveFrame(form));
            Assert.Equal("#FF0000", Color(form, 0, 0));

            Redo(form);
            Assert.Equal(2, FrameCount(form));
        });
    }

    [Fact]
    public void MoveFrame_UndoRestoresOrder()
    {
        UiTestSupport.RunInSta(() =>
        {
            using var form = new MainForm(4, 4);
            form.CreateControl();

            Paint(form, 0, 0, "#FF0000");
            Call(form, "add_frame", new { copy = false });
            Paint(form, 1, 1, "#00FF00");
            Call(form, "add_frame", new { copy = false });
            Paint(form, 2, 2, "#0000FF");

            Call(form, "select_frame", new { frame = 2 });
            Call(form, "move_frame", new { frame = 2, target_index = 0 });
            Assert.Equal(3, FrameCount(form));
            Assert.Equal(0, ActiveFrame(form));
            Assert.Equal("#0000FF", Color(form, 2, 2));

            Undo(form);
            Call(form, "select_frame", new { frame = 0 });
            Assert.Equal("#FF0000", Color(form, 0, 0));
        });
    }

    [Fact]
    public void UndoPrefersNewerFrameChangeOverOlderStroke()
    {
        UiTestSupport.RunInSta(() =>
        {
            using var form = new MainForm(4, 4);
            form.CreateControl();

            Paint(form, 0, 0, "#FF0000");
            Call(form, "add_frame", new { copy = false });
            Assert.Equal(2, FrameCount(form));

            Undo(form);   // 先撤销“加帧”，不能先撤销更早的笔画
            Assert.Equal(1, FrameCount(form));
            Assert.Equal("#FF0000", Color(form, 0, 0));

            Undo(form);   // 再撤销笔画
            Assert.Equal("transparent", Color(form, 0, 0));
        });
    }

    [Fact]
    public void BatchFrames_UndoCoversWholeBatch()
    {
        UiTestSupport.RunInSta(() =>
        {
            using var form = new MainForm(4, 4);
            form.CreateControl();

            Call(form, "batch_frames", new
            {
                fps = 12,
                frames = new object[]
                {
                    new { copy = true, operations = new[] { new { type = "paint", pixels = new[] { new { x = 0, y = 0, color = "#FF0000" } } } } },
                    new { copy = true, operations = new[] { new { type = "paint", pixels = new[] { new { x = 1, y = 1, color = "#00FF00" } } } } }
                }
            });
            Assert.Equal(3, FrameCount(form));

            Undo(form);
            Assert.Equal(1, FrameCount(form));

            Redo(form);
            Assert.Equal(3, FrameCount(form));
        });
    }
}
