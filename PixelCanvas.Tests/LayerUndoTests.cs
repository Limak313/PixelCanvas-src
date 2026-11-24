using System.Text.Json;
using System.Text.Json.Nodes;
using PixelCanvas;
using Xunit;

namespace PixelCanvas.Tests;

/// <summary>
/// 图层结构撤销/重做：增删图层、调整顺序、切换显隐都进结构快照栈，与像素历史按动作序号统一排序。
/// 只依赖窗体句柄，标 Category=UiHost 随 CI 门禁运行。
/// </summary>
[Trait("Category", "UiHost")]
public class LayerUndoTests
{
    private static JsonNode J(object value) => JsonSerializer.SerializeToNode(value)!;
    private static JsonNode Data(object reply) => J(reply)["data"]!;

    private static object Call(MainForm form, string action, object arguments) =>
        UiTestSupport.Handle(form, new { action, arguments });

    private static JsonObject Info(MainForm form) => Data(Call(form, "info", new { })).AsObject();

    private static int LayerCount(MainForm form) => Info(form)["layers"]!.AsArray().Count;

    private static int ActiveLayer(MainForm form) => Info(form)["activeLayer"]!.GetValue<int>();

    private static string[] LayerNames(MainForm form) =>
        Info(form)["layers"]!.AsArray().Select(node => node!["name"]!.GetValue<string>()).ToArray();

    private static bool LayerVisible(MainForm form, int index) =>
        Info(form)["layers"]![index]!["visible"]!.GetValue<bool>();

    private static string Color(MainForm form, int x, int y) =>
        Data(Call(form, "read_composite", new { x, y }))["color"]!.GetValue<string>();

    private static void Paint(MainForm form, int x, int y, string color) =>
        Call(form, "paint", new { pixels = new[] { new { x, y, color } } });

    private static void Undo(MainForm form) => Call(form, "undo", new { });

    private static void Redo(MainForm form) => Call(form, "redo", new { });

    [Fact]
    public void DeleteLayer_UndoRestoresLayerAndPixels_RedoRemovesAgain()
    {
        UiTestSupport.RunInSta(() =>
        {
            using var form = new MainForm(4, 4);
            form.CreateControl();

            Paint(form, 0, 0, "#FF0000");
            Call(form, "add_layer", new { });
            Paint(form, 1, 1, "#00FF00");
            Assert.Equal(2, LayerCount(form));
            Assert.Equal(1, ActiveLayer(form));

            Call(form, "delete_layer", new { layer = 1, confirm = true });
            Assert.Equal(1, LayerCount(form));
            Assert.Equal("transparent", Color(form, 1, 1));

            Undo(form);
            Assert.Equal(2, LayerCount(form));
            Assert.Equal(1, ActiveLayer(form));
            Assert.Equal("#00FF00", Color(form, 1, 1));
            Assert.Equal("#FF0000", Color(form, 0, 0));

            Redo(form);
            Assert.Equal(1, LayerCount(form));
            Assert.Equal("transparent", Color(form, 1, 1));
        });
    }

    [Fact]
    public void AddLayer_UndoRemovesLayer_RedoBringsItBack()
    {
        UiTestSupport.RunInSta(() =>
        {
            using var form = new MainForm(4, 4);
            form.CreateControl();

            Paint(form, 0, 0, "#FF0000");
            Call(form, "add_layer", new { });
            Assert.Equal(2, LayerCount(form));
            Assert.Equal(1, ActiveLayer(form));

            Undo(form);
            Assert.Equal(1, LayerCount(form));
            Assert.Equal(0, ActiveLayer(form));
            Assert.Equal("#FF0000", Color(form, 0, 0));

            Redo(form);
            Assert.Equal(2, LayerCount(form));
            Assert.Equal(1, ActiveLayer(form));
        });
    }

    [Fact]
    public void MoveLayer_UndoRestoresOrder()
    {
        UiTestSupport.RunInSta(() =>
        {
            using var form = new MainForm(4, 4);
            form.CreateControl();

            Paint(form, 0, 0, "#FF0000");
            Call(form, "add_layer", new { });
            Paint(form, 1, 1, "#00FF00");
            string[] before = LayerNames(form);
            Assert.Equal(new[] { "图层 1", "图层 2" }, before);

            Call(form, "move_layer", new { layer = 1, target_index = 0 });
            Assert.Equal(new[] { "图层 2", "图层 1" }, LayerNames(form));

            Undo(form);
            Assert.Equal(before, LayerNames(form));
        });
    }

    [Fact]
    public void LayerVisibility_ToggleIsUndoable()
    {
        UiTestSupport.RunInSta(() =>
        {
            using var form = new MainForm(4, 4);
            form.CreateControl();

            Paint(form, 0, 0, "#FF0000");
            Call(form, "layer_visibility", new { layer = 0, visible = false });
            Assert.False(LayerVisible(form, 0));
            Assert.Equal("transparent", Color(form, 0, 0));

            Undo(form);
            Assert.True(LayerVisible(form, 0));
            Assert.Equal("#FF0000", Color(form, 0, 0));

            Redo(form);
            Assert.False(LayerVisible(form, 0));
        });
    }

    [Fact]
    public void StructureChange_DropsPixelRedo()
    {
        UiTestSupport.RunInSta(() =>
        {
            using var form = new MainForm(4, 4);
            form.CreateControl();

            Paint(form, 0, 0, "#FF0000");
            Paint(form, 1, 1, "#00FF00");
            Undo(form);                                  // 撤销第二笔，重做栈里留下这笔像素
            Assert.Equal("transparent", Color(form, 1, 1));

            Call(form, "add_layer", new { });             // 结构改动应让像素重做失效
            Assert.Equal(2, LayerCount(form));

            Redo(form);                                  // 重做栈已被结构改动清空：什么都不会发生
            Assert.Equal(2, LayerCount(form));
            Assert.Equal("transparent", Color(form, 1, 1));
        });
    }
}
