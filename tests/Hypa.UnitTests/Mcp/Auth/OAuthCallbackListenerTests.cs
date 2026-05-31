using Hypa.Infrastructure.Mcp.Auth;
using Xunit;

namespace Hypa.UnitTests.Mcp.Auth;

public sealed class OAuthCallbackListenerTests
{
    [Fact]
    public void GetRedirectUri_ThrowsBeforeStartAsync()
    {
        var listener = new OAuthCallbackListener();

        var ex = Assert.Throws<InvalidOperationException>(() => listener.GetRedirectUri());
        Assert.Contains("StartAsync", ex.Message);
    }

    [Fact]
    public async Task Start_BindsTo127_0_0_1()
    {
        var listener = new OAuthCallbackListener();
        await listener.StartAsync(CancellationToken.None);
        try
        {
            var uri = listener.GetRedirectUri();
            Assert.Equal("127.0.0.1", uri.Host);
            Assert.Equal("/callback", uri.AbsolutePath);
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    [Fact]
    public async Task GetRedirectUri_ReturnsPortFromListener()
    {
        var listener = new OAuthCallbackListener();
        await listener.StartAsync(CancellationToken.None);
        try
        {
            var uri = listener.GetRedirectUri();
            Assert.True(uri.Port > 0);
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    [Fact]
    public async Task WaitForCallback_ReturnsCode_OnQueryString()
    {
        var listener = new OAuthCallbackListener();
        await listener.StartAsync(CancellationToken.None);
        try
        {
            var callbackUri = listener.GetRedirectUri();

            using var http = new HttpClient();
            var callbackTask = listener.WaitForCallbackAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

            _ = Task.Run(async () =>
            {
                await Task.Delay(50);
                await http.GetAsync($"{callbackUri}?code=test-code-123&state=abc");
            });

            var result = await callbackTask;

            Assert.Equal("test-code-123", result.Code);
            Assert.Null(result.Error);
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    [Fact]
    public async Task WaitForCallback_ReturnsError_OnErrorQueryString()
    {
        var listener = new OAuthCallbackListener();
        await listener.StartAsync(CancellationToken.None);
        try
        {
            var callbackUri = listener.GetRedirectUri();

            using var http = new HttpClient();
            var callbackTask = listener.WaitForCallbackAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

            _ = Task.Run(async () =>
            {
                await Task.Delay(50);
                await http.GetAsync($"{callbackUri}?error=access_denied");
            });

            var result = await callbackTask;

            Assert.Null(result.Code);
            Assert.Equal("access_denied", result.Error);
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    [Fact]
    public async Task WaitForCallback_ReturnsNull_OnTimeout()
    {
        var listener = new OAuthCallbackListener();
        await listener.StartAsync(CancellationToken.None);
        try
        {
            var result = await listener.WaitForCallbackAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);
            Assert.Null(result.Code);
            Assert.Null(result.Error);
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    [Fact]
    public async Task Stop_DisposesCleanly()
    {
        var listener = new OAuthCallbackListener();
        await listener.StartAsync(CancellationToken.None);
        await listener.StopAsync();
        // No exception = pass
    }
}
