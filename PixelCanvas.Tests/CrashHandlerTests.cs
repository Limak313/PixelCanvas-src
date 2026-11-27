using Xunit;

namespace PixelCanvas.Tests;

public class CrashHandlerTests
{
    [Fact]
    public void Handle_WritesLocalReportWithoutShowingDialog()
    {
        var paths = CrashHandler.Handle(new InvalidOperationException("handler-test"),
            "CrashHandlerTests", terminate: false, showDialog: false);
        Assert.NotNull(paths);
        try
        {
            Assert.True(File.Exists(paths!.JsonPath));
            Assert.True(File.Exists(paths.TextPath));
            Assert.Contains("handler-test", File.ReadAllText(paths.TextPath));
        }
        finally
        {
            try { File.Delete(paths!.JsonPath); } catch { }
            try { File.Delete(paths.TextPath); } catch { }
        }
    }
}
