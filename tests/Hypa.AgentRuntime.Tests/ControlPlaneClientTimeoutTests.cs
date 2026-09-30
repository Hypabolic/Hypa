using System.Net.Sockets;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class ControlPlaneClientTimeoutTests
{
    [Fact]
    public void Defaults_are_five_and_thirty_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), ControlPlaneClient.DefaultConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), ControlPlaneClient.DefaultCallTimeout);
        var client = new ControlPlaneClient("/tmp/unused.sock");
        Assert.Equal(ControlPlaneClient.DefaultConnectTimeout, client.ConnectTimeout);
        Assert.Equal(ControlPlaneClient.DefaultCallTimeout, client.CallTimeout);
    }

    [SkippableFact]
    public async Task CallAsync_cancels_at_override_timeout()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        var dir = Path.Combine(Path.GetTempPath(), "hypa-cto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var sock = Path.Combine(dir, "s.sock");
        Socket? listener = null;
        try
        {
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(sock));
            listener.Listen(8);

            await using var client = new ControlPlaneClient(
                sock,
                connectTimeout: TimeSpan.FromSeconds(2),
                callTimeout: TimeSpan.FromMilliseconds(150));
            await client.ConnectAsync();
            await Assert.ThrowsAsync<ControlPlaneClientTimeoutException>(
                () => client.CallAsync("ping"));
        }
        finally
        {
            listener?.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch { /* teardown */ }
        }
    }
}
