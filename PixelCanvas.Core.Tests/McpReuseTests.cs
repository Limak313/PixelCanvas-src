using System.Text.Json.Nodes;
using PixelCanvas;
using Xunit;

namespace PixelCanvas.Tests;

public class McpReuseTests
{
    private static JsonObject Reply(bool ok, bool? reusable, int? width, int? height)
    {
        var data = new JsonObject();
        if (reusable is not null) data["reusable"] = reusable;
        if (width is not null) data["width"] = width;
        if (height is not null) data["height"] = height;
        return new JsonObject { ["ok"] = ok, ["data"] = data };
    }

    [Fact]
    public void Reusable_MatchingSize_ReturnsTrue()
    {
        var reply = Reply(true, true, 32, 32);
        Assert.True(McpBridge.MatchesReusable(reply, 32, 32));
    }

    [Fact]
    public void ModifiedCanvas_ReusableFalse_ReturnsFalse()
    {
        var reply = Reply(true, false, 32, 32);
        Assert.False(McpBridge.MatchesReusable(reply, 32, 32));
    }

    [Fact]
    public void ReplyNotOk_ReturnsFalse()
    {
        var reply = Reply(false, true, 32, 32);
        Assert.False(McpBridge.MatchesReusable(reply, 32, 32));
    }

    [Fact]
    public void SizeMismatch_ReturnsFalse()
    {
        var reply = Reply(true, true, 64, 32);
        Assert.False(McpBridge.MatchesReusable(reply, 32, 32));
    }

    [Fact]
    public void NullReply_ReturnsFalse()
    {
        Assert.False(McpBridge.MatchesReusable(null, 32, 32));
    }

    [Fact]
    public void MissingData_ReturnsFalse()
    {
        var reply = new JsonObject { ["ok"] = true };
        Assert.False(McpBridge.MatchesReusable(reply, 32, 32));
    }
}
