using System.Text.Json;
using System.Text.Json.Nodes;
using PixelCanvas;
using Xunit;

namespace PixelCanvas.Tests;

/// <summary>新增导出动作的 MCP 路由：export_svg / export_frames 能穿过分组层到达画布。</summary>
[Trait("Category", "UiHost")]
public class McpExportFormatTests
{
    private static JsonNode J(object value) => JsonSerializer.SerializeToNode(value)!;

    private static JsonObject Data(object reply) => J(reply)["data"]!.AsObject();

    private static object Call(MainForm form, string action, object arguments) =>
        UiTestSupport.Handle(form, new { action, arguments });

    [Fact]
    public void ExportSvg_WritesVectorFileForCurrentFrame()
    {
        using var dir = new TempDir();
        string path = dir.File("art.svg");
        UiTestSupport.RunInSta(() =>
        {
            using var form = new MainForm(4, 4);
            form.CreateControl();
            Call(form, "paint", new { pixels = new[] { new { x = 0, y = 0, color = "#FF0000" } } });

            var data = Data(Call(form, "export_svg", new { path }));

            Assert.Equal(path, data["path"]!.GetValue<string>());
            Assert.True(data["rectangles"]!.GetValue<int>() >= 1);
            Assert.Contains("<rect x=\"0\" y=\"0\" width=\"1\" height=\"1\" fill=\"#FF0000\"/>", File.ReadAllText(path));
        });
    }

    [Fact]
    public void ExportFrameSequence_WritesOnePngPerFrame()
    {
        using var dir = new TempDir();
        string directory = Path.Combine(dir.Path, "seq");
        UiTestSupport.RunInSta(() =>
        {
            using var form = new MainForm(4, 4);
            form.CreateControl();
            Call(form, "add_frame", new { copy = true });

            var data = Data(Call(form, "export_frames", new { directory, base_name = "ghost", scale = 2 }));

            Assert.Equal(2, data["frameCount"]!.GetValue<int>());
            Assert.True(File.Exists(Path.Combine(directory, "ghost-001.png")));
            Assert.True(File.Exists(Path.Combine(directory, "ghost-002.png")));
        });
    }
}
