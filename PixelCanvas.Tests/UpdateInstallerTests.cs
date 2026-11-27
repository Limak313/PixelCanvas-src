using Xunit;

namespace PixelCanvas.Tests;

public class UpdateInstallerTests
{
    [Fact]
    public void VerifySigner_UnsignedPackageRejectedWhenPinConfigured()
    {
        using var dir = new TempDir();
        string file = dir.File("unsigned-setup.exe");
        File.WriteAllText(file, "not an installer");
        string? old = Environment.GetEnvironmentVariable(UpdateInstaller.SignerThumbprintEnv);
        try
        {
            Environment.SetEnvironmentVariable(UpdateInstaller.SignerThumbprintEnv, "46DBE0CC49F653E461A42223697F697B150E58E6");
            Assert.Throws<UpdateException>(() => UpdateInstaller.VerifySigner(file));
        }
        finally
        {
            Environment.SetEnvironmentVariable(UpdateInstaller.SignerThumbprintEnv, old);
        }
    }

    [Fact]
    public void VerifySigner_NoPinDoesNotRequireSignature()
    {
        using var dir = new TempDir();
        string file = dir.File("unsigned-setup.exe");
        File.WriteAllText(file, "not an installer");
        string? old = Environment.GetEnvironmentVariable(UpdateInstaller.SignerThumbprintEnv);
        try
        {
            Environment.SetEnvironmentVariable(UpdateInstaller.SignerThumbprintEnv, null);
            Assert.True(UpdateInstaller.VerifySigner(file));
        }
        finally
        {
            Environment.SetEnvironmentVariable(UpdateInstaller.SignerThumbprintEnv, old);
        }
    }
}
