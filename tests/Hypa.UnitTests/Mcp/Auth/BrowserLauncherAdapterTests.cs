using Hypa.Infrastructure.Mcp.Auth;
using Xunit;

namespace Hypa.UnitTests.Mcp.Auth;

public sealed class BrowserLauncherAdapterTests
{
    [Fact]
    public void GetCommand_Linux_ReturnsXdgOpen()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var command = BrowserLauncherAdapter.GetBrowserCommand(isWsl: false);

        Assert.Equal("xdg-open", command);
    }

    [Fact]
    public void GetCommand_Wsl_ReturnsWslview()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var command = BrowserLauncherAdapter.GetBrowserCommand(isWsl: true);

        Assert.Equal("wslview", command);
    }

    [Fact]
    public void GetCommand_MacOs_ReturnsOpen()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var command = BrowserLauncherAdapter.GetBrowserCommand(isWsl: false);

        Assert.Equal("open", command);
    }

    [Fact]
    public void TryOpen_CommandNotFound_ReturnsFalse_NoThrow()
    {
        // Use a non-existent command to test the false-return behavior
        var launcher = new BrowserLauncherAdapter(overrideCommand: "hypa-nonexistent-browser-9999");

        var result = launcher.TryOpen("https://example.com");

        Assert.False(result);
    }

    [Fact]
    public void TryOpen_ValidCommand_ReturnsTrue()
    {
        // Use 'true' (Unix) or 'cmd /c exit 0' (Windows) as a no-op process
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var launcher = new BrowserLauncherAdapter(overrideCommand: "true");
        var result = launcher.TryOpen("https://example.com");

        Assert.True(result);
    }
}
