using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Attach.Sidebar;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;
using Xunit;
using PlacementId = Hypa.Placement.Domain.PlacementId;

namespace Hypa.UnitTests.Cli;

public sealed class CubesConnectProductProfileTests
{
    [Fact]
    public async Task ProductProfile_Connect_reaches_retarget_without_continuity()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        Assert.False(live.Release.ContinuityEnabled);
        var retargeter = new RecordingRetargeter();
        live.CubesConnect = retargeter;
        live.Cubes =
        [
            new SidebarCubeItem
            {
                Id = "plc_unix001",
                Name = "Local mux",
                Kind = SidebarCubeKind.Cube,
                Reachability = SidebarCubeReachability.Reachable,
            },
        ];

        await AttachSession.ApplyCubesConnectAsync(
            new MouseEngineResult(
                MouseCommandKind.ApplyMenu,
                PlacementId: "plc_unix001"),
            live,
            port,
            tty: null,
            CancellationToken.None);
        await AttachSession.FlushDestConnectPrepForTests(live);

        Assert.True(retargeter.Called);
    }

    [Fact]
    public async Task ProductProfile_resolver_returns_unix_endpoint_for_owner_placement()
    {
        var probed = false;
        var resolver = new DirectoryCubesConnectEndpointResolver(
            directory: null,
            unixSocketForMux: _ => "/tmp/hypa-product-unix.sock",
            probe: (_, _) =>
            {
                probed = true;
                return ValueTask.FromResult<string?>("ok");
            });
        var destination = new SidebarCubeItem
        {
            Id = "plc_unix001",
            Name = "Local mux",
            Kind = SidebarCubeKind.Cube,
            Reachability = SidebarCubeReachability.Reachable,
        };
        var directory = new MapPlacementDirectory(new PlacementRecord
        {
            Id = PlacementId.Parse("plc_unix001"),
            Owner = DirectoryIdentity.Parse("local:test"),
            DisplayName = "Local mux",
            Kind = PlacementDirectoryKind.Cube,
            MuxIdentity = MuxIdentity.Parse("mux_agents"),
            Reachability = PlacementReachability.Reachable,
            LastSeen = DateTimeOffset.UtcNow,
        });
        var resolved = resolver.WithDirectory(directory);
        var endpoint = await resolved.ResolveAsync(destination);

        Assert.True(probed);
        Assert.IsType<UnixAttachEndpoint>(endpoint);
    }

    [Fact]
    public async Task ProductProfile_resolver_returns_rendezvous_connectivity_for_peer()
    {
        var joined = false;
        var material = CreateJoinMaterial("plc_peer001", "rel001aa");
        var resolver = new DirectoryCubesConnectEndpointResolver(
            directory: null,
            joinMaterial: new MapCubesConnectJoinMaterialSource(material),
            connect: (_, _) =>
            {
                joined = true;
                return Task.FromResult<IFramedSession?>(ChannelFramedSession.Pair("plc_peer001").Client);
            });
        var destination = new SidebarCubeItem
        {
            Id = "plc_peer001",
            Name = "Peer",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Reachable,
        };
        var directory = new MapPlacementDirectory(new PlacementRecord
        {
            Id = PlacementId.Parse("plc_peer001"),
            Owner = DirectoryIdentity.Parse("peer:dev"),
            DisplayName = "Peer",
            Kind = PlacementDirectoryKind.Peer,
            MuxIdentity = MuxIdentity.Parse("mux_agents"),
            Reachability = PlacementReachability.Reachable,
            LastSeen = DateTimeOffset.UtcNow,
        });
        var resolved = resolver.WithDirectory(directory);
        var endpoint = await resolved.ResolveAsync(destination);

        Assert.True(joined);
        Assert.IsType<ConnectivityAttachEndpoint>(endpoint);
    }

    [Fact]
    public void ProductProfile_sidebar_includes_cubes_section()
    {
        var registry = ChromeSectionRegistry.MuxRelease();
        Assert.Contains(registry.PaneOrder(), section => section.Id == SidebarTokenGrammar.CubesId);
    }

    [Fact]
    public void ProductProfile_sidebar_frame_cube_row_hit_tests_as_cube()
    {
        var frame = SidebarSectionComposer.Compose(
            new SidebarComposeInput
            {
                Ui = AttachUiConfig.Default,
                Expanded = true,
                RequestedWidth = 26,
                Cubes =
                [
                    new SidebarCubeItem
                    {
                        Id = "plc_peer001",
                        Name = "Peer",
                        Kind = SidebarCubeKind.Peer,
                        Reachability = SidebarCubeReachability.Reachable,
                    },
                ],
            },
            ChromeSectionRegistry.MuxRelease());
        var geo = LayoutChromeGeometry.Compute(
            80,
            24,
            new LayoutNodeDto { Type = "pane", PaneId = "p1" },
            zoomed: false,
            zoomedPaneId: null,
            focusedPaneId: "p1",
            AttachUiConfig.Default,
            tabCount: 1,
            AttachClientMode.Terminal,
            sidebarOpen: true,
            sidebarWidth: 26,
            sidebarFrame: frame);
        Assert.True(geo.ResourcePane?.Rows > 0);
        var cubeHits = geo.SidebarRows.Where(hit => hit.Id == "plc_peer001").ToArray();
        Assert.NotEmpty(cubeHits);
        Assert.All(cubeHits, hit => Assert.Equal(SidebarStubKind.Cube, hit.Kind));
        var cube = cubeHits[0];
        var chromeHit = ChromeHitTest.Hit(geo, cube.Rect.Col + 1, cube.Rect.Row);
        Assert.NotNull(chromeHit);
        Assert.Equal(ChromeHitKind.SidebarCube, chromeHit!.Kind);
        Assert.Equal("plc_peer001", chromeHit.PlacementId);
    }

    [Fact]
    public async Task ProductProfile_mobile_switcher_cube_select_opens_connect()
    {
        var frame = SidebarSectionComposer.Compose(
            new SidebarComposeInput
            {
                Ui = AttachUiConfig.Default,
                Expanded = true,
                RequestedWidth = 26,
                Cubes =
                [
                    new SidebarCubeItem
                    {
                        Id = "plc_peer001",
                        Name = "Peer",
                        Kind = SidebarCubeKind.Peer,
                        Reachability = SidebarCubeReachability.Reachable,
                    },
                ],
            },
            ChromeSectionRegistry.MuxRelease());
        var switcher = MobileSwitcherModel.Build(
            64,
            24,
            sidebarRows: null,
            tabs: [("t1", "one", true)],
            sidebarFrame: frame);
        var hitRows = switcher.Rows
            .Select((row, index) => (row, index))
            .Where(pair => pair.row.Hit)
            .ToArray();
        var cubeHitIndex = Array.FindIndex(
            hitRows,
            pair => pair.row.Target is MobileSwitcherTarget.Cube);
        Assert.True(cubeHitIndex >= 0);
        var marked = switcher.WithSelectedIndex(cubeHitIndex);
        var selected = marked.SelectedRow();
        Assert.NotNull(selected);
        Assert.Equal(MobileSwitcherTarget.Cube, selected!.Target);
        var chromeHit = ChromeHitTest.FromSwitcher(selected);

        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        Assert.False(live.Release.ContinuityEnabled);
        var retargeter = new RecordingRetargeter();
        live.CubesConnect = retargeter;
        live.Cubes = frame.Panes
            .Single(pane => pane.Slot is SidebarPaneSlot.Cubes)
            .Rows
            .Select(row => new SidebarCubeItem
            {
                Id = row.Id,
                Name = row.Label,
                Kind = SidebarCubeKind.Peer,
                Reachability = SidebarCubeReachability.Reachable,
            })
            .ToArray();

        await AttachSession.ApplyChromeHitAsync(
            chromeHit,
            live,
            port,
            tty: null,
            CancellationToken.None);
        await AttachSession.FlushDestConnectPrepForTests(live);

        Assert.True(retargeter.Called);
    }

    [Fact]
    public void ProductProfile_dest_commit_swaps_control_slot_without_source_lease_release()
    {
        var control = new DelayedReleasePort(TimeSpan.FromSeconds(5));
        var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
        live.InputLease = "src_input";
        live.ResizeLease = "src_resize";
        var cube = new SidebarCubeItem
        {
            Id = "plc_a",
            Name = "Build",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Reachable,
        };
        var dest = new ControlPlaneClient("/tmp/hypa-nonblock-dest.sock");
        var started = Environment.TickCount64;
        AttachSession.ApplyCubesConnectOutcome(live, control, cube, ConnectRetargetOutcome(dest));
        var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
        Assert.True(elapsed < TimeSpan.FromSeconds(1));
        Assert.Same(dest, live.ControlSlot!.Client);
        Assert.Equal("sub_dest", live.ControlSub);
    }

    [Fact]
    public void ProductProfile_cube_right_click_opens_connect_without_continuity()
    {
        var rows = new SidebarStubRow[] { new(SidebarStubKind.Cube, "plc_peer001", "Peer", 0) };
        var geo = MouseTestGeom.Split(sidebarOpen: true, rows: rows);
        var cube = geo.SidebarRows.Single(row => row.Kind is SidebarStubKind.Cube);
        var engine = new MouseEngine();
        var results = engine.Feed(
            new MouseEvent(MouseButton.Right, MouseAction.Press, cube.Rect.Col, cube.Rect.Row),
            new MouseFeedContext(geo) { ContinuityEnabled = false });
        var open = Assert.Single(results, result => result.Kind is MouseCommandKind.OpenMenu);
        Assert.Equal(ContextMenuModel.Connect, open.Menu!.Items[0].Id);
        Assert.DoesNotContain(open.Menu.Items, item => item.Id == ContextMenuModel.MoveWork);
    }

    [Fact]
    public void ProductProfile_source_release_timeout_does_not_abort_retarget()
    {
        var source = new ControlPlaneClient("/tmp/hypa-timeout-source.sock");
        var control = new TimeoutOnReleasePort(source);
        var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
        live.InputLease = "src_input";
        live.ResizeLease = "src_resize";
        live.ControlSlot = new AttachControlSlot { Client = source };
        var cube = new SidebarCubeItem
        {
            Id = "plc_a",
            Name = "Build",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Reachable,
        };
        var dest = new ControlPlaneClient("/tmp/hypa-timeout-dest.sock");
        var ex = Record.Exception(() =>
            AttachSession.ApplyCubesConnectOutcome(live, control, cube, ConnectRetargetOutcome(dest)));
        Assert.Null(ex);
        Assert.Same(dest, live.ControlSlot!.Client);
    }

    [Fact]
    public async Task ProductProfile_context_menu_connect_is_not_continuity_gated()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        Assert.False(live.Release.ContinuityEnabled);
        var retargeter = new RecordingRetargeter();
        live.CubesConnect = retargeter;
        live.Cubes =
        [
            new SidebarCubeItem
            {
                Id = "plc_peer001",
                Name = "Peer",
                Kind = SidebarCubeKind.Peer,
                Reachability = SidebarCubeReachability.Reachable,
            },
        ];
        var connect = new ContextMenuItem(ContextMenuModel.Connect, "Connect");

        await AttachSession.ApplyContextMenuItemAsync(
            connect,
            new MouseEngineResult(
                MouseCommandKind.ApplyMenu,
                MenuItem: connect,
                PlacementId: "plc_peer001"),
            live,
            port,
            tty: null,
            CancellationToken.None);
        await AttachSession.FlushDestConnectPrepForTests(live);

        Assert.True(retargeter.Called);
    }

    private sealed class TimeoutOnReleasePort(ControlPlaneClient source) : IAttachCommandPort
    {
        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            if (string.Equals(method, ProtocolMethods.RuntimeLeaseRelease, StringComparison.Ordinal))
                throw new ControlPlaneClientTimeoutException("timed out");
            return new ControlPlaneAttachCommandPort(source).CallAsync(method, parameters, ct);
        }
    }

    private sealed class DelayedReleasePort(TimeSpan delay) : IAttachCommandPort
    {
        public async Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            if (string.Equals(method, ProtocolMethods.RuntimeLeaseRelease, StringComparison.Ordinal))
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                return default;
            }

            return default;
        }
    }

    private sealed class RecordingRetargeter : ICubesConnectRetargeter
    {
        public bool Called { get; private set; }

        public Task<CubesConnectRetargetOutcome> ConnectAsync(
            CubesConnectRequest request,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(CubesConnectRetargetOutcome.LocalNoop(
                request.Destination,
                sourceMuxAlive: true,
                paneProcessCommands: [],
                processStartCount: 0,
                processStartCommands: []));
        }
    }

    private static CubesConnectRetargetOutcome ConnectRetargetOutcome(ControlPlaneClient destClient) =>
        new()
        {
            Ok = true,
            Action = CubesConnectActions.Retargeted,
            DestinationKind = SidebarCubeKind.Peer,
            TransportKind = AttachEndpointKinds.Unix,
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
            DestPaneId = "pane_d",
            DestInputLease = "dest_input",
            DestResizeLease = "dest_resize",
            DestSubscribeId = "sub_dest",
            DestClient = destClient,
        };

    private static CubesConnectJoinMaterial CreateJoinMaterial(string placementId, string nonceValue)
    {
        Assert.True(JoinNonce.TryParse(nonceValue, out var nonce));
        Assert.True(DeviceId.TryParse("dev_cli001", out var deviceId));
        Assert.True(Hypa.Connectivity.Domain.PlacementId.TryParse(placementId, out var joinPlacement));
        Assert.True(RendezvousUrl.TryParse("wss://127.0.0.1:9", out var url));
        var now = DateTimeOffset.UtcNow;
        var issued = new DeveloperJoinCapabilityIssuer().Issue(
            joinPlacement,
            deviceId,
            JoinRole.Client,
            nonce,
            now.AddMinutes(1),
            now);
        Assert.True(issued.Ok, issued.Detail);
        return new CubesConnectJoinMaterial
        {
            Url = url,
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
}
