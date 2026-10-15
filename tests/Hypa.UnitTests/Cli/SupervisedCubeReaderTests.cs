using System.Net.Sockets;
using Hypa.Cli.Attach;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Xunit;
using Hypa.AgentRuntime.Domain.Cubes.Connections;

namespace Hypa.UnitTests.Cli;

public sealed class SupervisedCubeReaderTests
{
    [Fact]
    public async Task ALostCubeReaderReportsOneFailureAndParksUntilTheReaderSwitches()
    {
        using var cubePeer = ClosablePeer.Listen();
        using var localPeer = ClosablePeer.Listen();
        var local = localPeer.ConnectClient();
        var cube = cubePeer.ConnectClient();
        var next = cubePeer.ConnectClient();
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.ConnectedPlacementId = "remote";
        live.ControlSlot = new AttachControlSlot { Client = cube };
        live.Connections.Bind(cube, new ConnectionId("remote", 4), "remote-boot");
        using var stop = new CancellationTokenSource();
        using var gate = new SemaphoreSlim(1, 1);
        using var tty = new UnixRawTerminal(new MemoryStream());

        var loop = new AttachSession().ReadRenderAsync(
            local, local, gate, new SnapshotAssembler(), tty, live, stop, stop.Token);

        // A close before the first read gives the same IOException as a close during it.
        cubePeer.CloseConnection();

        // The loop reports the loss, then parks. It does no work until the
        // reader switches, so the failure count cannot change after this point.
        Until(() => Volatile.Read(ref live.ReaderParks) == 1, "the render loop did not park");
        lock (live.ActivationGate)
            Assert.Single(live.EndpointFailures);
        Assert.False(loop.IsCompleted);
        Assert.True(cube.IsDisposed);

        // A redial commits: the slot holds the new client and the reader switches.
        live.Connections.Bind(next, new ConnectionId("remote", 5), "remote-boot");
        live.ControlSlot = new AttachControlSlot { Client = next };
        live.SignalReaderSwitch();
        cubePeer.Send("{\"event\":\"noop\",\"params\":{}}\n");
        Until(() => Volatile.Read(ref live.RenderLoopReads) == 1, "the render loop did not read the new client");

        Assert.False(loop.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref live.ReaderParks));

        // Only the cancel ends the loop. A fault fails the test here.
        stop.Cancel();
        try
        {
            await loop.WaitAsync(TestWait.HangGuard);
        }
        catch (OperationCanceledException)
        {
        }

        Assert.True(loop.IsCompleted);
        await local.DisposeAsync();
        await next.DisposeAsync();
    }

    [Fact]
    public async Task ALostLocalReaderEndsTheRenderLoop()
    {
        using var peer = ClosablePeer.Listen();
        var local = peer.ConnectClient();
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.ConnectedPlacementId = "local";
        live.ControlSlot = new AttachControlSlot { Client = local };
        live.Connections.Bind(local, new ConnectionId("local", 1), "local-boot");
        using var stop = new CancellationTokenSource();
        using var gate = new SemaphoreSlim(1, 1);
        using var tty = new UnixRawTerminal(new MemoryStream());

        var loop = new AttachSession().ReadRenderAsync(
            local, local, gate, new SnapshotAssembler(), tty, live, stop, stop.Token);
        // A close before the first read gives the same IOException as a close during it.
        peer.CloseConnection();

        var error = await Assert.ThrowsAnyAsync<Exception>(() => loop.WaitAsync(TestWait.HangGuard));
        Assert.IsNotType<TimeoutException>(error);
        Assert.True(loop.IsFaulted);
        Assert.Empty(live.EndpointFailures);
        await local.DisposeAsync();
    }

    [Fact]
    public async Task OnlyTheCommittedCubeClientIsASupervisedReader()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        await using var local = new ControlPlaneClient("/tmp/hypa-supervised-local.sock");
        await using var cube = new ControlPlaneClient("/tmp/hypa-supervised-cube.sock");
        live.ControlSlot = new AttachControlSlot { Client = cube };
        live.ConnectedPlacementId = "remote";

        Assert.True(AttachSession.IsSupervisedCubeReader(live, cube, local, local));
        Assert.False(AttachSession.IsSupervisedCubeReader(live, local, local, local));

        live.ControlSlot.Client = local;
        live.ConnectedPlacementId = "local";
        Assert.False(AttachSession.IsSupervisedCubeReader(live, local, local, local));
    }

    [Fact]
    public async Task HeartbeatTickOfALostCubeSendsOnlyThePingToTheLocalMux()
    {
        using var peer = ClosablePeer.Listen();
        var local = peer.ConnectClient();
        peer.Respond();
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.LocalRoot = AttachSession.CaptureRetargetPresentationBackup(live, MouseTestGeom.ApplyPort());
        live.ConnectedPlacementId = "remote";
        live.ControlSlot = null;
        live.ControlSub = "sub_cube";
        live.RenderSub = "sub_cube";
        live.AttachClientId = "conn_cube";
        live.LastPublishedVisiblePaneIds = ["pane-cube"];
        using var linked = new CancellationTokenSource();

        await AttachSession.HeartbeatTickAsync(local, live, tty: null, linked, CancellationToken.None);

        Assert.Equal(["ping"], await MethodsBeforeBarrierAsync(local, peer));
        await local.DisposeAsync();
    }

    [Fact]
    public async Task HeartbeatTickWhileLocalIsActiveRenewsTheVisibleSetOnTheLocalMux()
    {
        using var peer = ClosablePeer.Listen();
        var local = peer.ConnectClient();
        peer.Respond();
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.ConnectedPlacementId = "local";
        live.ControlSlot = new AttachControlSlot { Client = local };
        live.ControlSub = "sub_local";
        live.RenderSub = "sub_local";
        live.AttachClientId = "conn_local";
        live.LastPublishedVisiblePaneIds = ["pane-local"];
        using var linked = new CancellationTokenSource();

        await AttachSession.HeartbeatTickAsync(local, live, tty: null, linked, CancellationToken.None);

        Assert.Equal(["ping", "terminal.visible_set"], await MethodsBeforeBarrierAsync(local, peer));
        await local.DisposeAsync();
    }

    // A poll on a test counter. The limit only stops a hung run.
    private static void Until(Func<bool> condition, string message) =>
        Assert.True(SpinWait.SpinUntil(condition, TestWait.HangGuard), message);

    // The peer reads one connection in order. When it answers the barrier, it
    // has recorded every request that the client wrote before it.
    private static async Task<List<string>> MethodsBeforeBarrierAsync(ControlPlaneClient client, ClosablePeer peer)
    {
        await client.CallAsync("test.barrier");
        var methods = peer.MethodsSnapshot();
        Assert.Equal("test.barrier", methods[^1]);
        methods.RemoveAt(methods.Count - 1);
        return methods;
    }

    /// <summary>A listening peer that accepts connections and can close the last one.</summary>
    private sealed class ClosablePeer : IDisposable
    {
        private readonly Socket _listener;
        private readonly List<NetworkStream> _streams = [];

        private ClosablePeer(Socket listener, string path)
        {
            _listener = listener;
            Path = path;
        }

        internal string Path { get; }

        internal static ClosablePeer Listen()
        {
            var path = "/tmp/hypa-supervised-" + Guid.NewGuid().ToString("N")[..8] + ".sock";
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(4);
            return new ClosablePeer(listener, path);
        }

        internal ControlPlaneClient ConnectClient()
        {
            var client = new ControlPlaneClient(Path, connectTimeout: TestWait.HangGuard, callTimeout: TestWait.HangGuard);
            var accept = _listener.AcceptAsync();
            client.ConnectAsync().GetAwaiter().GetResult();
            _streams.Add(new NetworkStream(accept.GetAwaiter().GetResult(), ownsSocket: true));
            return client;
        }

        private readonly List<string> _methods = [];

        internal List<string> MethodsSnapshot()
        {
            lock (_methods)
                return [.. _methods];
        }

        /// <summary>Answers each request on the oldest connection and records its method.</summary>
        internal void Respond()
        {
            var stream = _streams[0];
            _ = Task.Run(async () =>
            {
                using var reader = new StreamReader(stream);
                try
                {
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(line);
                        if (!doc.RootElement.TryGetProperty("id", out var id))
                            continue;
                        lock (_methods)
                            _methods.Add(doc.RootElement.GetProperty("method").GetString()!);
                        var reply = "{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"result\":{}}\n";
                        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(reply));
                    }
                }
                catch (IOException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            });
        }

        internal void Send(string line)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(line);
            _streams[0].Write(bytes);
            _streams[0].Flush();
        }

        /// <summary>Closes the oldest open connection.</summary>
        internal void CloseConnection()
        {
            _streams[0].Dispose();
            _streams.RemoveAt(0);
        }

        public void Dispose()
        {
            foreach (var stream in _streams)
                stream.Dispose();
            _listener.Dispose();
            try { File.Delete(Path); }
            catch (IOException) { }
        }
    }
}
