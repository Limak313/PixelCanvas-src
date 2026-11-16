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
    public async Task ToolInfo_UnknownAction_BatchRejected()
    {
        var node = await McpTestHost.InvokeAsync("pixelcanvas_tool_info", new { actions = new[] { "project_info", "unknown" } });
        Assert.True(McpTestHost.IsError(node));
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
    }

    [Fact]
    public async Task GroupedAndLegacyRouteIdentically()
    {
        var grouped = await McpTestHost.InvokeAsync("pixelcanvas_project", new { action = "project_info", arguments = new { path = "invalid" } });
        var legacy = await McpTestHost.InvokeAsync("project_info", new { path = "invalid" });
        Assert.Equal(grouped.ToJsonString(), legacy.ToJsonString());
    }
}
