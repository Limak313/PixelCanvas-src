using System.Text.Json;
using System.Text.Json.Nodes;
using PixelCanvas;
using Xunit;

namespace PixelCanvas.Tests;

public class McpRoutingTests
{
    [Fact]
    public void ListsEightGroupedTools()
    {
        var advertised = JsonSerializer.SerializeToNode(McpBridge.ListTools())!["tools"]!.AsArray();
        Assert.Equal(8, advertised.Count);
    }

    [Fact]
    public async Task ToolInfo_SingleAction_ReturnsSchema()
    {
        var lookup = await McpTestHost.InvokeAsync("pixelcanvas_tool_info", new { action = "project_info" });
        Assert.False(McpTestHost.IsError(lookup));
        Assert.Contains("inputSchema", McpTestHost.Text(lookup));
    }

    [Fact]
    public async Task ToolInfo_Batch_ReturnsInRequestOrder()
    {
        var node = await McpTestHost.InvokeAsync("pixelcanvas_tool_info", new { actions = new[] { "project_info", "canvas_info" } });
        var defs = JsonNode.Parse(McpTestHost.Text(node))!.AsArray();
        Assert.False(McpTestHost.IsError(node));
        Assert.Equal(2, defs.Count);
        Assert.Equal("project_info", defs[0]!["name"]!.GetValue<string>());
        Assert.Equal("canvas_info", defs[1]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task ToolInfo_PrefixlessAliasResolves()
    {
        var node = await McpTestHost.InvokeAsync("pixelcanvas_tool_info", new { action = "export_svg" });
        Assert.False(McpTestHost.IsError(node));
        Assert.Contains("canvas_export_svg", McpTestHost.Text(node));
    }

    [Fact]
    public async Task ToolInfo_UnknownActionWithoutCandidateExplainsLookup()
    {
        var node = await McpTestHost.InvokeAsync("pixelcanvas_tool_info", new { action = "zzzzzz" });
        Assert.True(McpTestHost.IsError(node));
        Assert.Contains("未知动作", McpTestHost.Text(node));
        Assert.Contains("tool_info", McpTestHost.Text(node));
    }

    [Fact]
    public async Task ToolInfo_AmbiguousAliasListsCandidates()
    {
        var node = await McpTestHost.InvokeAsync("pixelcanvas_tool_info", new { action = "export_png" });
        Assert.True(McpTestHost.IsError(node));
        Assert.Contains("canvas_export_png", McpTestHost.Text(node));
        Assert.Contains("project_export_png", McpTestHost.Text(node));
    }

    [Fact]
    public async Task ToolInfo_UnknownAction_BatchRejected()
    {
        var node = await McpTestHost.InvokeAsync("pixelcanvas_tool_info", new { actions = new[] { "project_info", "unknown" } });
        Assert.True(McpTestHost.IsError(node));
    }

    [Fact]
    public async Task ToolInfo_BatchInvalidItemRejected()
    {
        var node = await McpTestHost.InvokeAsync("pixelcanvas_tool_info", new { actions = new object[] { "project_info", 42 } });
        Assert.True(McpTestHost.IsError(node));
        Assert.Contains("有效的操作名称", McpTestHost.Text(node));
    }

    [Fact]
    public async Task ToolInfo_ActionAndActionsMixed_Rejected()
    {
        var node = await McpTestHost.InvokeAsync("pixelcanvas_tool_info", new { action = "project_info", actions = new[] { "canvas_info" } });
        Assert.True(McpTestHost.IsError(node));
    }

    [Fact]
    public async Task GroupedTool_RejectsForeignAction()
    {
        var node = await McpTestHost.InvokeAsync("pixelcanvas_draw", new { action = "project_info", arguments = new { path = "invalid" } });
        Assert.True(McpTestHost.IsError(node));
        Assert.Contains("pixelcanvas_project", McpTestHost.Text(node));
    }

    [Fact]
    public async Task GroupedTool_AcceptsUniquePrefixlessAlias()
    {
        var node = await McpTestHost.InvokeAsync("pixelcanvas_files", new { action = "export_svg", arguments = new { path = "invalid" } });
        Assert.DoesNotContain("未知动作", McpTestHost.Text(node));
        Assert.DoesNotContain("有多个候选", McpTestHost.Text(node));
    }

    [Fact]
    public async Task GroupedTool_EmptyActionRejected()
    {
        var node = await McpTestHost.InvokeAsync("pixelcanvas_project", new { action = "  ", arguments = new { path = "invalid" } });
        Assert.True(McpTestHost.IsError(node));
        Assert.Contains("必须是非空字符串", McpTestHost.Text(node));
    }

    [Fact]
    public async Task GroupedTool_AcceptsCaseInsensitiveCanonicalName()
    {
        var node = await McpTestHost.InvokeAsync("pixelcanvas_project", new { action = "PROJECT_INFO", arguments = new { path = "invalid" } });
        Assert.DoesNotContain("未知动作", McpTestHost.Text(node));
    }

    [Fact]
    public async Task GroupedTool_RequiresArgumentsObject()
    {
        var node = await McpTestHost.InvokeAsync("pixelcanvas_project", new { action = "project_info" });
        Assert.True(McpTestHost.IsError(node));
        Assert.Contains("arguments", McpTestHost.Text(node));
    }

    [Fact]
    public async Task GroupedTool_AmbiguousAliasListsCandidates()
    {
        var node = await McpTestHost.InvokeAsync("pixelcanvas_project", new { action = "export_png", arguments = new { path = "invalid" } });
        Assert.True(McpTestHost.IsError(node));
        Assert.Contains("canvas_export_png", McpTestHost.Text(node));
        Assert.Contains("project_export_png", McpTestHost.Text(node));
    }

    [Fact]
    public async Task GroupedTool_UnknownActionSuggestsClosest()
    {
        var node = await McpTestHost.InvokeAsync("pixelcanvas_project", new { action = "project_inf", arguments = new { path = "invalid" } });
        Assert.True(McpTestHost.IsError(node));
        Assert.Contains("project_info", McpTestHost.Text(node));
        Assert.Contains("是否想用", McpTestHost.Text(node));
    }

    [Fact]
    public void EveryOperation_IsAdvertisedInExactlyOneGroup()
    {
        var tools = JsonSerializer.SerializeToNode(McpBridge.ListTools())!["tools"]!.AsArray()
            .Select(t => t!.AsObject()).ToArray();
        Assert.Equal(8, tools.Length);

        var expectedGroups = new[]
        {
            "pixelcanvas_canvas", "pixelcanvas_animation", "pixelcanvas_draw", "pixelcanvas_files",
            "pixelcanvas_layers", "pixelcanvas_project", "pixelcanvas_settings"
        };
        var groups = tools.Where(t => t["name"]!.GetValue<string>() != "pixelcanvas_tool_info").ToArray();
        Assert.Equal(expectedGroups.OrderBy(n => n), groups.Select(t => t["name"]!.GetValue<string>()).OrderBy(n => n));

        var advertised = new List<string>();
        foreach (var group in groups)
        {
            var schema = group["inputSchema"]!.AsObject();
            var actions = schema["properties"]!["action"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
            Assert.NotEmpty(actions);
            advertised.AddRange(actions);
            foreach (string action in actions)
                Assert.Contains(action, group["description"]!.GetValue<string>());
            Assert.Equal(new[] { "action", "arguments" }, schema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
            Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        }

        Assert.Equal(78, advertised.Count);
        Assert.Equal(78, advertised.Distinct().Count());
        var toolInfo = tools.Single(t => t["name"]!.GetValue<string>() == "pixelcanvas_tool_info");
        var lookup = toolInfo["inputSchema"]!["properties"]!["action"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(advertised.OrderBy(n => n), lookup.OrderBy(n => n));
    }

    [Theory]
    [InlineData("canvas_export_svg")]
    [InlineData("canvas_export_frame_sequence")]
    public async Task GroupedExportActions_DispatchToCanvas(string action)
    {
        var node = await McpTestHost.InvokeAsync("pixelcanvas_files", new { action, arguments = new { path = "invalid" } });
        Assert.DoesNotContain("不属于此工具", McpTestHost.Text(node));
        Assert.DoesNotContain("未知工具", McpTestHost.Text(node));
    }

    [Fact]
    public async Task GroupedAndLegacyRouteIdentically()
    {
        var grouped = await McpTestHost.InvokeAsync("pixelcanvas_project", new { action = "project_info", arguments = new { path = "invalid" } });
        var legacy = await McpTestHost.InvokeAsync("project_info", new { path = "invalid" });
        Assert.Equal(grouped.ToJsonString(), legacy.ToJsonString());
    }
}
