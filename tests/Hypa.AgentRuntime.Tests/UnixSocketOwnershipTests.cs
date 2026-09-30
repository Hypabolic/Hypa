using System.Net.Sockets;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.ControlPlane;
using Hypa.ControlPlane.Unix;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class UnixSocketOwnershipTests
{

    [SkippableFact]
    public void RemoveIfOwned_leaves_a_replacement_inode()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        var dir = Path.Combine(Path.GetTempPath(), "hypa-sock-own-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var sock = Path.Combine(dir, "s.sock");
        Socket? first = null;
        Socket? second = null;
        try
        {
            first = BindListen(sock);
            Assert.True(UnixSocketPathIdentity.TryRead(sock, out var owned));

            File.Delete(sock);
            Assert.False(File.Exists(sock));

            second = BindListen(sock);
            Assert.True(File.Exists(sock));
            Assert.True(UnixSocketPathIdentity.TryRead(sock, out var replacement));
            Assert.NotEqual(owned, replacement);

            UnixSocketPathIdentity.RemoveIfOwned(sock, owned);
            Assert.True(File.Exists(sock));
            Assert.True(UnixSocketPathIdentity.TryRead(sock, out var after));
            Assert.Equal(replacement, after);

            UnixSocketPathIdentity.RemoveIfOwned(sock, replacement);
            Assert.False(File.Exists(sock));
        }
        finally
        {
            first?.Dispose();
            second?.Dispose();
            try { File.Delete(sock); } catch { /* teardown */ }
            try { Directory.Delete(dir, recursive: true); } catch { /* teardown */ }
        }
    }

    [SkippableFact]
    public async Task Failed_start_does_not_delete_a_live_peer_socket()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        var (dir, sock) = NewPrivateSocketPath("own-fail");
        UnixSocketServer? live = null;
        try
        {
            live = new UnixSocketServer(CreateControlPlane(), sock);
            await live.StartAsync(CancellationToken.None);
            Assert.True(File.Exists(sock));

            var colliding = new UnixSocketServer(CreateControlPlane(), sock);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => colliding.StartAsync(CancellationToken.None));
            await colliding.DisposeAsync();
            Assert.True(File.Exists(sock));

            await using var client = new ControlPlaneClient(sock);
            await client.ConnectAsync();
            var ping = await client.CallAsync("ping");
            Assert.True(ping.GetProperty("ok").GetBoolean());
        }
        finally
        {
            if (live is not null)
            {
                try { await live.DisposeAsync(); } catch { /* teardown */ }
            }

            TryDeleteTree(dir);
        }
    }

    [SkippableFact]
    public async Task Dispose_removes_the_owned_socket()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        var (dir, sock) = NewPrivateSocketPath("own-del");
        try
        {
            var server = new UnixSocketServer(CreateControlPlane(), sock);
            await server.StartAsync(CancellationToken.None);
            Assert.True(File.Exists(sock));
            await server.DisposeAsync();
            Assert.False(File.Exists(sock));
        }
        finally
        {
            TryDeleteTree(dir);
        }
    }

    private static Socket BindListen(string sock)
    {
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(sock));
        listener.Listen(1);
        return listener;
    }

    private static ControlPlaneService CreateControlPlane()
    {
        var state = new AppState(SessionId.New("own"));
        var intel = new PaneIntelligencePipeline();
        return new ControlPlaneService(
            state,
            TestPaneFactories.Create(intel),
            intel,
            new HeuristicAgentDetector());
    }

    private static (string Dir, string Sock) NewPrivateSocketPath(string prefix)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine("/tmp", prefix + "-" + id);
        Directory.CreateDirectory(dir);
#pragma warning disable CA1416
        File.SetUnixFileMode(
            dir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        return (dir, Path.Combine(dir, "s.sock"));
    }

    private static void TryDeleteTree(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }

}
