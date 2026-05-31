using Hypa.Infrastructure.Mcp.Auth;
using Hypa.Runtime.Application.Ports;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Mcp.Auth;

[Trait("Category", "HypaBrowserOAuthDelegate")]
public sealed class HypaBrowserOAuthDelegateTests
{
    private static readonly Uri AuthUri = new("https://auth.example.com/authorize?response_type=code");
    private static readonly Uri RedirectUri = new("http://localhost:9876/callback");

    private static (IBrowserLauncher Browser, IOAuthCallbackListener Listener) MakeMocks(bool browserSucceeds = true)
    {
        var browser = Substitute.For<IBrowserLauncher>();
        browser.TryOpen(Arg.Any<string>()).Returns(browserSucceeds);

        var listener = Substitute.For<IOAuthCallbackListener>();
        listener.StartAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        listener.StopAsync().Returns(Task.CompletedTask);
        listener.GetRedirectUri().Returns(RedirectUri);

        return (browser, listener);
    }

    [Fact]
    public async Task HandleAsync_NoBrowserNonInteractive_ThrowsBeforeWaiting()
    {
        var (browser, listener) = MakeMocks();
        var sut = new HypaBrowserOAuthDelegate(
            browser, listener,
            progress: null,
            noBrowser: true,
            interactive: false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.HandleAsync(AuthUri, RedirectUri, CancellationToken.None));

        await listener.DidNotReceive().WaitForCallbackAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_BrowserFailNonInteractive_ThrowsBeforeWaiting()
    {
        // Browser fails to open, noBrowser becomes true internally; non-interactive → should throw
        var (browser, listener) = MakeMocks(browserSucceeds: false);
        var sut = new HypaBrowserOAuthDelegate(
            browser, listener,
            progress: null,
            noBrowser: false,
            interactive: false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.HandleAsync(AuthUri, RedirectUri, CancellationToken.None));

        await listener.DidNotReceive().WaitForCallbackAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }
}
