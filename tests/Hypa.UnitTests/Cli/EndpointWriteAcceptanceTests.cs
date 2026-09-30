using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class EndpointWriteAcceptanceTests
{
    private const string SourceId = "plc_local";
    private const string DestId = "plc_peer";

    [SkippableFact]
    public async Task SurfaceActivationAcceptsThreeFramesBeforeResizeReply()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");

        await using var sourcePeer = HoldingPeer.Listen();
        await using var destPeer = HoldingPeer.Listen();
        await using var source = Client(sourcePeer);
        await using var dest = Client(destPeer);
        await ConnectAsync(sourcePeer, source, destPeer, dest);

        var live = Live(source, dest, new ActivationPhase.ReleasingSource("client-shell-surface:9:off"));
        dest.Outbound.HoldWrites();
        AttachSession.RecordEndpointTransportFailure(live, SourceId, 1, "source transport fault");
        AttachSession.DrainQueuedEndpointFailures(live, tty: null);

        var phase = Assert.IsType<ActivationPhase.ActivatingTarget>(live.PendingActivation!.Phase);
        Assert.Equal("client-shell-surface:9:on", phase.RequestId);
        Assert.Equal(3, dest.Outbound.PendingWrites);
        Assert.Equal(0, destPeer.Count);
        Assert.Equal(0, destPeer.RepliesSent);
        Assert.False(dest.IsDisposed);

        dest.Outbound.ReleaseWrites();
        Assert.True(SpinWait.SpinUntil(() => destPeer.Count >= 3, TimeSpan.FromSeconds(3)));
        Assert.Equal(
            [AttachEndpointProtocol.Resize, AttachEndpointProtocol.SurfaceInterest, AttachEndpointProtocol.Focus],
            destPeer.Methods().Take(3).ToArray());
        Assert.Contains(":on", destPeer.Line(1), StringComparison.Ordinal);
        Assert.Contains(":baseline", destPeer.Line(2), StringComparison.Ordinal);
        Assert.Equal(0, destPeer.RepliesSent);

        var resizeId = destPeer.RequestId(0);
        destPeer.ReplySuccess(0);
        Assert.True(SpinWait.SpinUntil(() => CompletionCount(live) >= 1, TimeSpan.FromSeconds(3)));
        Assert.Equal(resizeId, LastControlRequestId(live));
        AttachSession.ProcessActivationCompletionsForPump(live, tty: null);

        Assert.IsType<ActivationPhase.ActivatingTarget>(live.PendingActivation.Phase);
        Assert.False(dest.IsDisposed);
        Assert.Equal(0, FailureCount(live));
    }

    [SkippableFact]
    public async Task HostResizeStaysBehindAcceptedSurfaceOn()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");

        await using var sourcePeer = HoldingPeer.Listen();
        await using var destPeer = HoldingPeer.Listen();
        await using var source = Client(sourcePeer);
        await using var dest = Client(destPeer);
        await ConnectAsync(sourcePeer, source, destPeer, dest);

        var live = Live(source, dest, new ActivationPhase.ReleasingSource("client-shell-surface:9:off"));
        dest.Outbound.HoldWrites();
        AttachSession.RecordEndpointTransportFailure(live, SourceId, 1, "source transport fault");
        AttachSession.DrainQueuedEndpointFailures(live, tty: null);
        AttachSession.NotifyActivationResize(live, 100, 40, tty: null);

        Assert.Equal(4, dest.Outbound.PendingWrites);
        Assert.Equal(0, destPeer.Count);

        dest.Outbound.ReleaseWrites();
        Assert.True(SpinWait.SpinUntil(() => destPeer.Count >= 4, TimeSpan.FromSeconds(3)));
        Assert.Equal(AttachEndpointProtocol.Resize, destPeer.MethodAt(0));
        Assert.Equal(AttachEndpointProtocol.SurfaceInterest, destPeer.MethodAt(1));
        Assert.Equal(AttachEndpointProtocol.Focus, destPeer.MethodAt(2));
        Assert.Equal(AttachEndpointProtocol.Resize, destPeer.MethodAt(3));
        Assert.Contains("\"columns\":100", destPeer.Line(3), StringComparison.Ordinal);
        Assert.True(IndexOfMethod(destPeer, AttachEndpointProtocol.SurfaceInterest) < 3);
    }

    [SkippableFact]
    public async Task FocusBaselineApplicationErrorKeepsDestination()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");

        await using var destPeer = HoldingPeer.Listen(failFocus: true);
        await using var dest = Client(destPeer);
        var accept = destPeer.AcceptAsync();
        await dest.ConnectAsync();
        await accept;

        var live = LiveWithoutSource(dest, new ActivationPhase.ActivatingTarget(
            "client-shell-surface:9:on",
            null,
            null,
            null,
            true,
            new EndpointActivationEvidence()));
        var port = Port(live, dest);
        const string focusId = "client-shell-focus:9:baseline";
        var outcome = port.SendTo(
            DestId,
            new EndpointActivationMessage.HostFocusBaseline(true, focusId));

        Assert.Equal(EndpointSendOutcome.Sent, outcome);
        Assert.True(SpinWait.SpinUntil(() => destPeer.Count >= 1, TimeSpan.FromSeconds(3)));
        Assert.Equal(focusId, destPeer.RequestId(0));
        Assert.True(SpinWait.SpinUntil(() => CompletionCount(live) >= 1, TimeSpan.FromSeconds(3)));
        Assert.Equal(focusId, LastControlRequestId(live));
        AttachSession.ProcessActivationCompletionsForPump(live, tty: null);

        Assert.False(dest.IsDisposed);
        Assert.Equal(0, FailureCount(live));
        Assert.IsType<ActivationPhase.ActivatingTarget>(live.PendingActivation!.Phase);
    }

    [SkippableFact]
    public async Task RejectedResizeIsNotSentBeforeTimeout()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");

        await using var sourcePeer = HoldingPeer.Listen();
        await using var destPeer = HoldingPeer.Listen();
        await using var source = Client(sourcePeer);
        await using var dest = Client(destPeer);
        await ConnectAsync(sourcePeer, source, destPeer, dest);

        var live = Live(source, dest, new ActivationPhase.ActivatingTarget(
            "client-shell-surface:9:on",
            null,
            null,
            null,
            true,
            new EndpointActivationEvidence()));
        dest.Outbound.Stop();
        var port = Port(live, dest);
        var started = Stopwatch.StartNew();
        var outcome = port.SendTo(
            DestId,
            new EndpointActivationMessage.Resize(Geometry(), "client-shell-resize:9:1"));
        started.Stop();

        Assert.Equal(EndpointSendOutcome.NotSent, outcome);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(0, destPeer.Count);
        Assert.Equal(0, destPeer.RepliesSent);
        string error;
        lock (live.ActivationGate)
            error = live.EndpointFailures.Peek().Error;
        Assert.Contains("endpoint writer stopped", error, StringComparison.Ordinal);
        Assert.True(dest.IsDisposed);
        Assert.False(source.IsDisposed);
    }

    [SkippableFact]
    public async Task TwoPortsShareOneEndpointWriter()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");

        await using var destPeer = HoldingPeer.Listen();
        await using var dest = Client(destPeer);
        var accept = destPeer.AcceptAsync();
        await dest.ConnectAsync();
        await accept;

        var live = LiveWithoutSource(dest, new ActivationPhase.ActivatingTarget(
            "client-shell-surface:9:on",
            null,
            null,
            null,
            true,
            new EndpointActivationEvidence()));
        var first = Port(live, dest);
        var second = Port(live, dest);
        dest.Outbound.HoldWrites();
        Assert.Equal(
            EndpointSendOutcome.Sent,
            first.SendTo(DestId, new EndpointActivationMessage.Resize(Geometry(), "resize-a")));
        Assert.Equal(
            EndpointSendOutcome.Sent,
            second.SendTo(DestId, new EndpointActivationMessage.Resize(Geometry(), "resize-b")));

        Assert.Equal(2, dest.Outbound.PendingWrites);
        Assert.Equal(0, destPeer.Count);
        dest.Outbound.ReleaseWrites();
        Assert.True(SpinWait.SpinUntil(() => destPeer.Count >= 2, TimeSpan.FromSeconds(3)));
        Assert.Equal("resize-a", destPeer.RequestId(0));
        Assert.Equal("resize-b", destPeer.RequestId(1));
    }

    private static int CompletionCount(AttachLiveState live)
    {
        lock (live.ActivationGate)
            return live.ActivationCompletions.Count;
    }

    private static int FailureCount(AttachLiveState live)
    {
        lock (live.ActivationGate)
            return live.EndpointFailures.Count;
    }

    private static string LastControlRequestId(AttachLiveState live)
    {
        lock (live.ActivationGate)
        {
            foreach (var completion in live.ActivationCompletions)
            {
                if (completion is EndpointActivationRpcCompletion.Control control)
                    return control.Result.RequestId;
            }
        }

        return string.Empty;
    }

    private static int IndexOfMethod(HoldingPeer peer, string method)
    {
        var methods = peer.Methods();
        for (var i = 0; i < methods.Count; i++)
        {
            if (methods[i] == method)
                return i;
        }

        return -1;
    }

    private static AttachEndpointRegistryPort Port(AttachLiveState live, ControlPlaneClient dest)
    {
        var lease = Lease(DestId, 2);
        return new AttachEndpointRegistryPort(
            live,
            sourceRpc: null,
            new AttachEndpointRpcClient(dest),
            sourceLease: null,
            lease,
            completion => AttachSession.EnqueueActivationCompletion(live, completion),
            (endpointId, generation, error) =>
                AttachSession.RecordEndpointTransportFailure(live, endpointId, generation, error, dest));
    }

    private static ControlPlaneClient Client(HoldingPeer peer) =>
        new(peer.Path, connectTimeout: TimeSpan.FromSeconds(2), callTimeout: TimeSpan.FromSeconds(5));

    private static async Task ConnectAsync(
        HoldingPeer sourcePeer,
        ControlPlaneClient source,
        HoldingPeer destPeer,
        ControlPlaneClient dest)
    {
        var sourceAccept = sourcePeer.AcceptAsync();
        var destAccept = destPeer.AcceptAsync();
        await source.ConnectAsync();
        await dest.ConnectAsync();
        await sourceAccept;
        await destAccept;
    }

    private static AttachLiveState Live(ControlPlaneClient source, ControlPlaneClient dest, ActivationPhase phase)
    {
        var live = LiveWithoutSource(dest, phase);
        var port = MouseTestGeom.ApplyPort();
        live.ConnectedPlacementId = SourceId;
        live.ControlSlot = new AttachControlSlot { Client = source };
        live.SourceBackup = new AttachRetargetPresentationBackup
        {
            SourceClient = source,
            SourcePort = port,
            ConnectedPlacementId = SourceId,
        };
        return live;
    }

    private static AttachLiveState LiveWithoutSource(ControlPlaneClient dest, ActivationPhase phase)
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.Cubes =
        [
            new SidebarCubeItem
            {
                Id = SourceId,
                Name = "Local",
                Kind = SidebarCubeKind.Local,
                Reachability = SidebarCubeReachability.Local,
            },
            new SidebarCubeItem
            {
                Id = DestId,
                Name = "Peer",
                Kind = SidebarCubeKind.Peer,
                Reachability = SidebarCubeReachability.Reachable,
            },
        ];
        live.PendingConnectCube = live.Cubes[1];
        live.PendingConnectControl = port;
        live.PendingConnectOutcome = new CubesConnectRetargetOutcome
        {
            Ok = true,
            Action = CubesConnectActions.Noop,
            DestinationKind = SidebarCubeKind.Peer,
            TransportKind = "unix",
            SourceMuxAlive = true,
            NestedAttachBlocked = false,
            NestedAttachEnabled = false,
            SpawnsHypaAttach = false,
            WorkMoved = false,
            AllowNestedMutated = false,
            CalledServerStop = false,
            ProcessStartCount = 0,
            ProcessStartCommands = [],
            PaneProcessCommands = [],
            DestClient = dest,
            TargetBootId = "dest-boot",
            TargetConnectionGeneration = 2,
        };
        live.PendingActivation = PendingEndpointActivation.ForTests(
            phase,
            Lease(SourceId, 1),
            Lease(DestId, 2));
        return live;
    }

    private static EndpointActivationLease Lease(string endpointId, ulong generation) =>
        new()
        {
            EndpointId = endpointId,
            ConnectionGeneration = generation,
            BootId = endpointId + "-boot",
            MinimumProjectionRevision = 0,
            ClientId = "client",
            LeaseId = endpointId + "-lease",
        };

    private static AttachGeometry Geometry() =>
        new()
        {
            Columns = 80,
            Rows = 24,
            CellWidthPx = 8,
            CellHeightPx = 16,
            GeometryRevision = 1,
        };

    private sealed class HoldingPeer : IAsyncDisposable
    {
        private readonly Socket _listener;
        private readonly object _gate = new();
        private readonly List<string> _lines = [];
        private readonly bool _failFocus;
        private NetworkStream? _stream;
        private Task? _reader;
        private int _replies;

        private HoldingPeer(Socket listener, string path, bool failFocus)
        {
            _listener = listener;
            Path = path;
            _failFocus = failFocus;
        }

        internal string Path { get; }

        internal int Count
        {
            get
            {
                lock (_gate)
                    return _lines.Count;
            }
        }

        internal int RepliesSent => Volatile.Read(ref _replies);

        internal static HoldingPeer Listen(bool failFocus = false)
        {
            var path = "/tmp/hypa-accept-" + Guid.NewGuid().ToString("N")[..8] + ".sock";
            if (File.Exists(path))
                File.Delete(path);
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                listener.Bind(new UnixDomainSocketEndPoint(path));
                listener.Listen(1);
                return new HoldingPeer(listener, path, failFocus);
            }
            catch
            {
                listener.Dispose();
                throw;
            }
        }

        internal async Task AcceptAsync()
        {
            var accepted = await _listener.AcceptAsync();
            _stream = new NetworkStream(accepted, ownsSocket: true);
            _reader = ReadAsync();
        }

        internal List<string> Methods()
        {
            lock (_gate)
                return _lines.Select(MethodOf).ToList();
        }

        internal string MethodAt(int index) => MethodOf(Line(index));

        internal string Line(int index)
        {
            lock (_gate)
                return _lines[index];
        }

        internal string RequestId(int index)
        {
            using var doc = JsonDocument.Parse(Line(index));
            return doc.RootElement.GetProperty("params").GetProperty("request_id").GetString() ?? string.Empty;
        }

        internal void ReplySuccess(int index)
        {
            var line = Line(index);
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idEl) ? IdJson(idEl) : null;
            var requestId = root.GetProperty("params").GetProperty("request_id").GetString() ?? string.Empty;
            if (id is null)
                return;
            var reply = "{\"id\":" + id
                + ",\"result\":{\"request_id\":\"" + requestId
                + "\",\"boot_id\":\"dest-boot\",\"projection_revision\":1,\"geometry_revision\":1,\"active\":true,\"lease_id\":\"lease\",\"minimum_projection_revision\":0,\"connection_generation\":2}}";
            WriteReply(reply);
        }

        public async ValueTask DisposeAsync()
        {
            _stream?.Dispose();
            _listener.Dispose();
            if (_reader is not null)
            {
                try
                {
                    await _reader.WaitAsync(TimeSpan.FromSeconds(1));
                }
                catch (Exception)
                {
                }
            }

            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }

        private async Task ReadAsync()
        {
            var stream = _stream ?? throw new InvalidOperationException("peer is not connected");
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            try
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync();
                    if (line is null)
                        return;
                    if (line.Length == 0)
                        continue;
                    lock (_gate)
                        _lines.Add(line);
                    if (!_failFocus)
                        continue;
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var id = root.TryGetProperty("id", out var idEl) ? IdJson(idEl) : null;
                    if (id is null)
                        continue;
                    var method = root.TryGetProperty("method", out var methodEl) ? methodEl.GetString() : null;
                    if (method == AttachEndpointProtocol.Focus)
                    {
                        WriteReply("{\"id\":" + id + ",\"error\":{\"code\":-32000,\"message\":\"focus rejected\"}}");
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void WriteReply(string reply)
        {
            var stream = _stream;
            if (stream is null)
                return;
            var bytes = Encoding.UTF8.GetBytes(reply + "\n");
            stream.Write(bytes);
            stream.Flush();
            Interlocked.Increment(ref _replies);
        }

        private static string MethodOf(string line)
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("method", out var method)
                ? method.GetString() ?? string.Empty
                : string.Empty;
        }

        private static string? IdJson(JsonElement id) =>
            id.ValueKind switch
            {
                JsonValueKind.String => JsonSerializer.Serialize(id.GetString()),
                JsonValueKind.Number => id.GetRawText(),
                _ => null,
            };
    }
}
