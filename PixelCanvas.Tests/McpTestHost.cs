using System.Text.Json;
using System.Text.Json.Nodes;
using PixelCanvas;

namespace PixelCanvas.Tests;

/// <summary>MCP 桥接调用辅助：直接走进程内 McpBridge，不启动独立进程与管道。</summary>
internal static class McpTestHost
{
    public static async Task<JsonNode> InvokeAsync(string name, object? arguments)
    {
        var args = arguments is null
            ? new JsonObject()
            : JsonSerializer.SerializeToNode(arguments) as JsonObject ?? new JsonObject();
        var request = new JsonObject { ["name"] = name, ["arguments"] = args };
        var result = await McpBridge.CallToolAsync(request);
        return JsonSerializer.SerializeToNode(result)!;
    }

    public static bool IsError(JsonNode node) => node["isError"]?.GetValue<bool>() ?? false;

    public static string Text(JsonNode node) => node["content"]![0]!["text"]!.GetValue<string>();

    public static async Task<JsonNode> ProjectActionAsync(string action, object arguments)
    {
        var node = await InvokeAsync("pixelcanvas_project", new { action, arguments });
        if (IsError(node)) throw new Exception(Text(node));
        return JsonNode.Parse(Text(node))!;
    }
}
