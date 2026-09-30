using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Mux;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.ControlPlane;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;
using Xunit;
using PlacementId = Hypa.Placement.Domain.PlacementId;

namespace Hypa.UnitTests.Cli;

public sealed class CubesConnectStageTimingTests
{
    private static readonly string[] RemoteStages =
    [
        CubesConnectStages.Resolve,
        CubesConnectStages.Material,
        // The regular direct path opens and joins in one call: join covers the dial.
        CubesConnectStages.Join,
        CubesConnectStages.Hello,
        CubesConnectStages.Snapshot,
        CubesConnectStages.Subscribe,
        CubesConnectStages.Observe,
        CubesConnectStages.Queued,
        CubesConnectStages.Begin,
        CubesConnectStages.SourceReleased,
        CubesConnectStages.TargetStarted,
        CubesConnectStages.Ready,
    ];

    [Fact]
    public async Task Local_cube_writes_no_stage_events()
    {
        var log = new ListProcessLog();
        var port = MouseTestGeom.ApplyPort();
        var live = Live(port, log);

        await AttachSession.ApplyCubesConnectAsync(
                new MouseEngineResult(MouseCommandKind.ApplyMenu, PlacementId: "plc_local"),
                live,
                port,
                tty: null,
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.DoesNotContain(log.Records, record => record.Event == ProcessLogEvents.CubesConnectStage);
        Assert.Contains(log.Records, record => record.Event == ProcessLogEvents.CubesConnectRequested);
    }

    [Fact]
    public async Task Failed_connect_writes_reached_stages_then_error()
    {
        var log = new ListProcessLog();
        var port = MouseTestGeom.ApplyPort();
        var live = Live(port, log);
        live.CubesConnect = new AuthorizeSkip(new CubesConnectRetargetService(new MissingResolver()));

        await AttachSession.ApplyCubesConnectAsync(
                new MouseEngineResult(MouseCommandKind.ApplyMenu, PlacementId: "plc_quic01"),
                live,
                port,
                tty: null,
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        await AttachSession.FlushDestConnectPrepForTests(live);

        var stages = StageNames(log);
        Assert.Equal([CubesConnectStages.Resolve, CubesConnectStages.Queued], stages);
        AssertMonotonic(log);
        var lastStage = log.Records.FindLastIndex(record => record.Event == ProcessLogEvents.CubesConnectStage);
        var error = log.Records.FindIndex(record =>
            record.Event == ProcessLogEvents.CubesConnectOutcome
            && record.Action == "connect"
            && record.Outcome == ProcessLogEvents.OutcomeError);
        Assert.True(error > lastStage);
    }

    [Fact]
    public async Task Remote_connect_records_stage_order_and_monotonic_elapsed()
    {
        var log = new ListProcessLog();
        var port = MouseTestGeom.ApplyPort();
        var live = Live(port, log);
        await using var source = AcceptingPeer.Listen();
        live.ControlSlot = new AttachControlSlot { Client = source.ConnectClient() };
        using var sourceSnap = JsonDocument.Parse(
            """{"boot_id":"source-boot","revision":1,"projection_revision":1}""");
        live.LastSnapshot = sourceSnap.RootElement.Clone();
        live.LastSnapshotConnectionGeneration = 1;
        live.SourceTransportEnvelope.StampServerGeneration(1);
        live.SourceEndpointBootId = "source-boot";

        using var stop = new CancellationTokenSource();
        ChannelFramedSession? mux = null;
        var resolver = new DirectoryCubesConnectEndpointResolver(
            directory: null,
            directMaterial: new StubDirectMaterial(),
            directConnect: (_, _) =>
            {
                var pair = ChannelFramedSession.Pair("plc_quic01");
                mux = pair.Mux;
                _ = ServeMuxAsync(pair.Mux, stop.Token);
                return Task.FromResult<IFramedSession?>(pair.Client);
            });
        var directory = new MapPlacementDirectory(QuicRecord());
        live.CubesConnect = new AuthorizeSkip(
            new CubesConnectRetargetService(resolver.WithDirectory(directory)));

        try
        {
            await AttachSession.ApplyCubesConnectAsync(
                    new MouseEngineResult(MouseCommandKind.ApplyMenu, PlacementId: "plc_quic01"),
                    live,
                    port,
                    tty: null,
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await AttachSession.FlushDestConnectPrepForTests(live).WaitAsync(TimeSpan.FromSeconds(5));

            var pending = live.PendingActivation;
            Assert.True(pending is not null, "activation missing: " + string.Join(",", StageNames(log)) + " " + live.StatusError);
            var releasing = Assert.IsType<ActivationPhase.ReleasingSource>(pending.Phase);
            var registry = new SentRegistry();
            pending.ReceiveResponseForBoot(
                "plc_local",
                1,
                "source-boot",
                releasing.RequestId,
                new EndpointSurfaceControlResult
                {
                    Ok = true,
                    ProjectionRevision = 1,
                    BootId = "source-boot",
                    RequestId = releasing.RequestId,
                    ConnectionGeneration = 1,
                },
                registry);

            var activating = Assert.IsType<ActivationPhase.ActivatingTarget>(pending.Phase);
            var target = pending.Target;
            pending.ReceiveResponseForBoot(
                target.EndpointId,
                target.ConnectionGeneration,
                target.BootId,
                activating.RequestId,
                new EndpointSurfaceControlResult
                {
                    Ok = true,
                    ProjectionRevision = 1,
                    BootId = target.BootId,
                    RequestId = activating.RequestId,
                    ConnectionGeneration = target.ConnectionGeneration,
                },
                registry);
            pending.ReceiveSnapshot(
                target.EndpointId,
                target.ConnectionGeneration,
                new AttachSnapshotEvidence
                {
                    BootId = target.BootId,
                    Revision = 1,
                    WorkspaceId = "w1",
                    TabId = "t1",
                    PaneId = "p1",
                });
            pending.ReceiveSurface(
                target.EndpointId,
                target.ConnectionGeneration,
                new AttachSurfaceEvidence
                {
                    BootId = target.BootId,
                    ProjectionRevision = 1,
                    SurfaceRevision = 1,
                    Columns = pending.Resize.Columns,
                    Rows = pending.Resize.Rows,
                    Focused = true,
                    FocusedPaneId = "p1",
                });

            var successor = AttachSession.CompleteEndpointActivation(live, tty: null);
            Assert.Null(successor);
            Assert.True(live.StatusError is null, live.StatusError);
            Assert.IsType<ActivationPhase.SynchronizingPresentation>(live.PendingActivation!.Phase);

            Assert.Equal(RemoteStages, StageNames(log));
            AssertMonotonic(log);
            Assert.All(
                log.Records.Where(record => record.Event == ProcessLogEvents.CubesConnectStage),
                record => Assert.Equal("plc_quic01", record.EndpointId));
            var ready = log.Records.FindIndex(record => record.Stage == CubesConnectStages.Ready);
            var sync = log.Records.FindIndex(record =>
                record.Event == ProcessLogEvents.CubesConnectOutcome && record.Action == "sync");
            Assert.True(sync > ready);
        }
        finally
        {
            stop.Cancel();
            if (mux is not null)
                await mux.DisposeAsync();
        }
    }

    [Fact]
    public async Task Failed_hello_records_hello_then_the_error_outcome()
    {
        var log = new ListProcessLog();
        var port = MouseTestGeom.ApplyPort();
        var live = Live(port, log);
        using var stop = new CancellationTokenSource();
        ChannelFramedSession? mux = null;
        var resolver = new DirectoryCubesConnectEndpointResolver(
            directory: null,
            directMaterial: new StubDirectMaterial(),
            directConnect: (_, _) =>
            {
                var pair = ChannelFramedSession.Pair("plc_quic01");
                mux = pair.Mux;
                _ = ServeMuxAsync(pair.Mux, stop.Token, failHello: true);
                return Task.FromResult<IFramedSession?>(pair.Client);
            });
        live.CubesConnect = new AuthorizeSkip(
            new CubesConnectRetargetService(resolver.WithDirectory(new MapPlacementDirectory(QuicRecord()))));

        try
        {
            await AttachSession.ApplyCubesConnectAsync(
                    new MouseEngineResult(MouseCommandKind.ApplyMenu, PlacementId: "plc_quic01"),
                    live,
                    port,
                    tty: null,
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await AttachSession.FlushDestConnectPrepForTests(live).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(
                [
                    CubesConnectStages.Resolve,
                    CubesConnectStages.Material,
                    CubesConnectStages.Join,
                    CubesConnectStages.Hello,
                    CubesConnectStages.Queued,
                ],
                StageNames(log));
            Assert.DoesNotContain(log.Records, record => record.Stage == CubesConnectStages.Snapshot);
            var hello = log.Records.FindIndex(record => record.Stage == CubesConnectStages.Hello);
            var error = log.Records.FindIndex(record =>
                record.Event == ProcessLogEvents.CubesConnectOutcome
                && record.Action == "connect"
                && record.Outcome == ProcessLogEvents.OutcomeError);
            Assert.True(error > hello);
            AssertMonotonic(log);
        }
        finally
        {
            stop.Cancel();
            if (mux is not null)
                await mux.DisposeAsync();
        }
    }

    [Fact]
    public void Stage_clock_uses_the_request_stopwatch()
    {
        var log = new ListProcessLog();
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 15)
            Thread.Sleep(1);
        var clock = new CubesConnectStageClock(watch, log, "plc_quic01", "default", "attach");
        clock.Stamp(CubesConnectStages.Resolve);
        var elapsed = Assert.Single(log.Records).ElapsedMs;
        Assert.NotNull(elapsed);
        Assert.True(elapsed >= 15);
    }

    [Fact]
    public async Task Dial_records_tcp_when_the_opened_path_is_tcp()
    {
        var log = new ListProcessLog();
        var clock = new CubesConnectStageClock(Stopwatch.StartNew(), log, "plc_quic01", "default", "attach");
        var material = CreateMaterial() with { StageClock = clock };
        var session = await CubesConnectDirectOutboundJoin.ConnectAsync(
            material,
            new OpenBytePath(BytePathProviders.Tcp),
            CancellationToken.None);

        Assert.Null(session);
        var dial = Assert.Single(log.Records, record => record.Stage == CubesConnectStages.Dial);
        Assert.Equal(BytePathProviders.Tcp, dial.Transport);
        Assert.Equal("plc_quic01", dial.EndpointId);
        Assert.Contains(log.Records, record => record.Stage == CubesConnectStages.Join);
        AssertMonotonic(log);
    }

    [Fact]
    public void Stage_line_writes_elapsed_fields()
    {
        var line = Encoding.UTF8.GetString(ProcessLogJsonWriter.WriteLine(new ProcessLogRecord
        {
            Event = ProcessLogEvents.CubesConnectStage,
            Subsystem = ProcessLogEvents.SubsystemCubes,
            Outcome = ProcessLogEvents.OutcomeOk,
            Stage = CubesConnectStages.Dial,
            ElapsedMs = 0,
            StageMs = 0,
            EndpointId = "plc_quic01",
            Transport = "tcp",
        }));

        Assert.Contains("\"stage\":\"dial\"", line, StringComparison.Ordinal);
        Assert.Contains("\"elapsed_ms\":0", line, StringComparison.Ordinal);
        Assert.Contains("\"stage_ms\":0", line, StringComparison.Ordinal);
        Assert.Contains("\"endpoint_id\":\"plc_quic01\"", line, StringComparison.Ordinal);
        Assert.Contains("\"transport\":\"tcp\"", line, StringComparison.Ordinal);
    }

    private static void AssertMonotonic(ListProcessLog log)
    {
        long previous = -1;
        foreach (var record in log.Records.Where(item => item.Event == ProcessLogEvents.CubesConnectStage))
        {
            Assert.NotNull(record.ElapsedMs);
            Assert.NotNull(record.StageMs);
            Assert.True(record.ElapsedMs >= 0);
            Assert.True(record.StageMs >= 0);
            Assert.True(record.ElapsedMs >= previous);
            previous = record.ElapsedMs.Value;
        }
    }

    private static List<string> StageNames(ListProcessLog log) =>
        log.Records
            .Where(record => record.Event == ProcessLogEvents.CubesConnectStage)
            .Select(record => record.Stage!)
            .ToList();

    private static AttachLiveState Live(MouseRecordingPort port, ListProcessLog log)
    {
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.ProcessLog = log;
        live.ConnectedPlacementId = "plc_local";
        live.PlacementKind = SidebarCubeKind.Local;
        live.AttachConfig = AttachClientConfig.Default with
        {
            Experimental = AttachClientConfig.Default.Experimental with { AllowNested = true },
        };
        live.Cubes =
        [
            new SidebarCubeItem
            {
                Id = "plc_local",
                Name = "Local",
                Kind = SidebarCubeKind.Local,
                Reachability = SidebarCubeReachability.Local,
            },
            new SidebarCubeItem
            {
                Id = "plc_quic01",
                Name = "Edge",
                Kind = SidebarCubeKind.Peer,
                Reachability = SidebarCubeReachability.Reachable,
                ProviderSuffix = "QUIC · Reachable",
                ConnectEnabled = true,
            },
        ];
        return live;
    }

    private static PlacementRecord QuicRecord() =>
        new()
        {
            Id = PlacementId.Parse("plc_quic01"),
            Owner = DirectoryIdentity.Parse("local:test"),
            DisplayName = "Edge",
            Kind = PlacementDirectoryKind.Peer,
            MuxIdentity = MuxIdentity.Parse("mux_agents"),
            Reachability = PlacementReachability.Reachable,
            LastSeen = DateTimeOffset.UtcNow,
            Quic = new QuicPlacementProfile
            {
                Id = "plc_quic01",
                Label = "Edge",
                Target = "192.168.1.10",
                Session = "agents",
                Enabled = true,
            },
        };

    private static CubesConnectDirectMaterial CreateMaterial()
    {
        Assert.True(JoinNonce.TryParse("nonce001", out var nonce));
        Assert.True(DeviceId.TryParse("dev_cli001", out var deviceId));
        Assert.True(Hypa.Connectivity.Domain.PlacementId.TryParse("plc_quic01", out var joinPlacement));
        var secret = JoinSecret.Create();
        var now = DateTimeOffset.UtcNow;
        var issued = new DeveloperJoinCapabilityIssuer().Issue(
            joinPlacement,
            deviceId,
            JoinRole.Client,
            nonce,
            now.AddMinutes(1),
            now,
            secret);
        Assert.True(issued.Ok, issued.Detail);
        return new CubesConnectDirectMaterial
        {
            Endpoint = new BytePathEndpoint
            {
                Host = "192.168.1.10",
                Port = 443,
                Tls = true,
                TlsServerName = "192.168.1.10",
                QuicListening = false,
            },
            Bootstrap = new JoinBootstrap
            {
                ProtocolVersion = ConnectivityProtocolVersion.V0,
                Role = JoinRole.Client,
                PlacementId = joinPlacement,
                Nonce = nonce,
                StreamClass = StreamClass.Control,
                Capability = issued.Value!,
                Audience = RendezvousAudiences.Rendezvous,
                TenantScope = RendezvousTenants.Local,
            },
        };
    }

    private static async Task ServeMuxAsync(
        ChannelFramedSession mux,
        CancellationToken cancellationToken,
        bool failHello = false)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var received = await mux.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (!received.Ok || received.Value is null || !received.Value.IsControl)
                {
                    if (!received.Ok)
                        return;
                    continue;
                }

                var json = Encoding.UTF8.GetString(received.Value.Payload.Span);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("id", out var idEl))
                    continue;
                var id = idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : idEl.GetRawText();
                var method = doc.RootElement.TryGetProperty("method", out var methodEl)
                    ? methodEl.GetString()
                    : null;
                var body = failHello && string.Equals(method, ProtocolMethods.AttachHello, StringComparison.Ordinal)
                    ? "\"error\":{\"code\":-32603,\"message\":\"hello failed\"}"
                    : "\"result\":" + Reply(method);
                var line = "{\"id\":" + JsonSerializer.Serialize(id) + "," + body + "}";
                var sent = await mux.SendAsync(
                        StreamFrame.Control(
                            StreamDirection.MuxToClient,
                            0,
                            Encoding.UTF8.GetBytes(line)),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!sent.Ok)
                    return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
    }

    private static string Reply(string? method)
    {
        if (string.Equals(method, ProtocolMethods.AttachHello, StringComparison.Ordinal))
            return WelcomeJson();
        if (string.Equals(method, ProtocolMethods.SessionSnapshot, StringComparison.Ordinal))
            return SnapshotJson();
        if (string.Equals(method, ProtocolMethods.EventsSubscribe, StringComparison.Ordinal))
            return """{"subscription_id":"sub_dest"}""";
        return "{}";
    }

    private static string WelcomeJson()
    {
        var methods = string.Join(',', AttachEndpointProtocol.Methods.Select(item => "\"" + item + "\""));
        var capabilities = string.Join(
            ',',
            AttachEndpointProtocol.RequiredCapabilities.Select(item => "\"" + item + "\""));
        return "{"
            + "\"endpoint_generation\":" + AttachEndpointProtocol.EndpointGeneration + ","
            + "\"protocol_major\":" + ProtocolVersion.Major + ","
            + "\"protocol_minor\":" + ProtocolVersion.Minor + ","
            + "\"boot_id\":\"dest-boot\","
            + "\"mux_identity\":\"mux_agents\","
            + "\"snapshot_codec\":\"" + AttachEndpointProtocol.SnapshotCodec + "\","
            + "\"surface_codec\":\"" + AttachEndpointProtocol.SurfaceCodec + "\","
            + "\"input_codec\":\"" + AttachEndpointProtocol.InputCodec + "\","
            + "\"methods\":[" + methods + "],"
            + "\"capabilities\":[" + capabilities + "],"
            + "\"connection_generation\":1"
            + "}";
    }

    private static string SnapshotJson() =>
        """
        {"focused_tab_id":"t1","focused_workspace_id":"w1","session_id":"agents","boot_id":"dest-boot","revision":1,"projection_revision":1,"tabs":[{"tab_id":"t1","focused_pane_id":"p1"}],"panes":[{"pane_id":"p1","alive":true}]}
        """;

    private sealed class AuthorizeSkip(ICubesConnectRetargeter inner) : ICubesConnectRetargeter
    {
        public Task<CubesConnectRetargetOutcome> ConnectAsync(
            CubesConnectRequest request,
            CancellationToken cancellationToken = default) =>
            inner.ConnectAsync(request with { AuthorizeConnectAsync = null }, cancellationToken);
    }

    private sealed class MissingResolver : ICubesConnectEndpointResolver
    {
        public ValueTask<IAttachEndpoint?> ResolveAsync(
            SidebarCubeItem destination,
            CancellationToken cancellationToken = default) =>
            new((IAttachEndpoint?)null);
    }

    private sealed class StubDirectMaterial : ICubesConnectDirectMaterialSource
    {
        public ValueTask<CubesConnectDirectMaterial?> GetAsync(
            PlacementRecord placement,
            CancellationToken cancellationToken = default)
        {
            return new ValueTask<CubesConnectDirectMaterial?>(CreateMaterial());
        }
    }

    private sealed class SentRegistry : IEndpointRegistryPort
    {
        public string ActiveId => "plc_local";

        public bool SurfaceActive(string endpointId) => false;

        public bool SupportsSurfaceInterest(string endpointId) => true;

        public EndpointSendOutcome SendTo(string endpointId, EndpointActivationMessage message) =>
            EndpointSendOutcome.Sent;

        public void Fail(string endpointId, string error)
        {
        }

        public void SetSurfaceActive(string endpointId, bool active)
        {
        }

        public bool SetActive(string endpointId) => true;

        public void FreezeInput()
        {
        }

        public void UnfreezeInput()
        {
        }
    }

    private sealed class OpenBytePath(string provider) : IBytePath
    {
        public string Provider => provider;

        public ValueTask<ConnectivityOutcome<BytePathHandle>> OpenAsync(
            BytePathRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            _ = cancellationToken;
            return ValueTask.FromResult(ConnectivityOutcome<BytePathHandle>.Success(new BytePathHandle
            {
                Stream = new MemoryStream(),
                Lifetime = new NoopLifetime(),
                Provider = provider,
            }));
        }

        public ValueTask CloseAsync(BytePathHandle handle, CancellationToken cancellationToken = default)
        {
            _ = handle;
            _ = cancellationToken;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoopLifetime : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ListProcessLog : IProcessLogSink
    {
        public List<ProcessLogRecord> Records { get; } = [];

        public bool IsEnabled(ProcessLogLevel level) => true;

        public bool Disabled => false;

        public string? Path => null;

        public void Write(ProcessLogRecord record) => Records.Add(record);
    }

    private sealed class MapPlacementDirectory(PlacementRecord record) : IPlacementDirectory
    {
        public ValueTask<PlacementOutcome<PlacementRecord>> GetAsync(
            PlacementId placementId,
            CancellationToken cancellationToken = default) =>
            new(PlacementOutcome<PlacementRecord>.Success(record));

        public ValueTask<PlacementOutcome<PlacementRecord>> GetForConnectAsync(
            DirectoryIdentity requester,
            PlacementId placementId,
            CancellationToken cancellationToken = default) =>
            new(PlacementOutcome<PlacementRecord>.Success(record));

        public ValueTask<PlacementOutcome<PlacementRecord>> RegisterAsync(
            PlacementRegistration request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<PlacementOutcome<PlacementRecord>> SetReachabilityAsync(
            DirectoryIdentity actor,
            PlacementId placementId,
            PlacementReachability reachability,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<PlacementOutcome<PlacementRecord>> SetActiveWorkAsync(
            DirectoryIdentity actor,
            PlacementId placementId,
            string? workId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<PlacementOutcome> GrantListAccessAsync(
            DirectoryIdentity actor,
            PlacementId placementId,
            DirectoryIdentity grantee,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<PlacementOutcome> GrantWorkAccessAsync(
            DirectoryIdentity actor,
            DirectoryIdentity identity,
            string workId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<PlacementOutcome<PlacementRecord>> RemoveOwnedAsync(
            DirectoryIdentity owner,
            PlacementId placementId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<PlacementOutcome<IReadOnlyList<PlacementRow>>> ListAsync(
            DirectoryIdentity requester,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class AcceptingPeer : IDisposable, IAsyncDisposable
    {
        private readonly Socket _listener;
        private NetworkStream? _stream;

        private AcceptingPeer(Socket listener, string path)
        {
            _listener = listener;
            Path = path;
        }

        internal string Path { get; }

        internal static AcceptingPeer Listen()
        {
            var path = "/tmp/hypa-stage-" + Guid.NewGuid().ToString("N")[..8] + ".sock";
            if (File.Exists(path))
                File.Delete(path);
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                listener.Bind(new UnixDomainSocketEndPoint(path));
                listener.Listen(1);
                return new AcceptingPeer(listener, path);
            }
            catch
            {
                listener.Dispose();
                throw;
            }
        }

        internal ControlPlaneClient ConnectClient()
        {
            var client = new ControlPlaneClient(Path, connectTimeout: TimeSpan.FromSeconds(2));
            var accept = AcceptAsync();
            client.ConnectAsync().GetAwaiter().GetResult();
            accept.GetAwaiter().GetResult();
            return client;
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _listener.Dispose();
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private async Task AcceptAsync()
        {
            var accepted = await _listener.AcceptAsync();
            _stream = new NetworkStream(accepted, ownsSocket: true);
            _ = ReadAsync();
        }

        private async Task ReadAsync()
        {
            var stream = _stream;
            if (stream is null)
                return;
            var buffer = new byte[4096];
            try
            {
                while (await stream.ReadAsync(buffer).ConfigureAwait(false) > 0)
                {
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
