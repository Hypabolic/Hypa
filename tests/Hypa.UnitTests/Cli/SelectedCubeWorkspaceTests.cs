using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.AgentRuntime.Infrastructure.Config;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Attach.Sidebar;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class SelectedCubeWorkspaceTests
{
    [Fact]
    public void Compose_highlights_connected_cube_and_marks_pending_remote()
    {
        var view = new CubesChromeSectionStrategy().Compose(
            new SidebarComposeInput
            {
                Ui = AttachUiConfig.Default,
                Expanded = true,
                RequestedWidth = 26,
                FocusedCubeId = "plc_docker",
                ConnectedCubeId = "plc_local",
                Cubes =
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
                        Id = "plc_docker",
                        Name = "Docker",
                        Kind = SidebarCubeKind.Peer,
                        Reachability = SidebarCubeReachability.Reachable,
                    },
                ],
            },
            new ResolvedSidebarSection
            {
                Id = SidebarTokenGrammar.CubesId,
                Title = "Cubes",
                Order = 0,
                Collapsed = false,
                Config = AttachSidebarSectionConfig.CubesDefault,
            },
            collapsed: false,
            visible: true,
            width: 26,
            SidebarCollapseDisplay.Expanded);
        var local = view.Rows.First(row => row.Id == "plc_local" && row.CardRowIndex == 0);
        var docker = view.Rows.First(row => row.Id == "plc_docker" && row.CardRowIndex == 0);
        Assert.True(local.Selected);
        Assert.True(local.CardFocused);
        Assert.False(docker.Selected);
        Assert.Equal("◐", docker.TrailingStatus);
        Assert.Equal(CubeTrailingStatusKind.Connecting, docker.TrailingStatusKind);
    }

    [Fact]
    public void RebuildSidebar_keeps_selected_cube_workspaces_when_local_snapshot_arrives()
    {
        var live = Live();
        live.Cubes =
        [
            LocalCube(),
            DockerCube(),
        ];
        live.ConnectedPlacementId = "plc_local";
        live.SelectedCubeId = "plc_docker";

        AttachSession.RebuildSidebar(live, LocalSnapshot(), requestGit: false);
        Assert.Contains(live.SidebarInput!.Workspaces, ws => ws.Id == "ws-local");
        Assert.DoesNotContain(live.SidebarInput.Workspaces, ws => ws.Id == "ws-docker");

        AttachSession.RememberCubeSnapshot(live, "plc_docker", DockerSnapshot());
        AttachSession.RebuildSidebar(live, LocalSnapshot(), requestGit: false);
        Assert.Contains(live.SidebarInput!.Workspaces, ws => ws.Id == "ws-local");
        Assert.DoesNotContain(live.SidebarInput.Workspaces, ws => ws.Id == "ws-docker");
        Assert.Equal("plc_docker", live.SidebarInput.FocusedCubeId);
        Assert.Equal("plc_local", live.SidebarInput.ConnectedCubeId);
        Assert.Equal("ws-local", live.LastSnapshot?.GetProperty("workspaces")[0].GetProperty("workspace_id").GetString());

        AttachSession.SelectCube(live, DockerCube());
        Assert.Equal("plc_docker", live.SelectedCubeId);
        Assert.Equal("plc_local", live.ConnectedPlacementId);
        var status = CubesChromeSectionStrategy.TrailingStatus(
            DockerCube(),
            live.SelectedCubeId,
            live.ConnectedPlacementId);
        Assert.Equal("◐", status.Glyph);
        Assert.Equal(CubeTrailingStatusKind.Connecting, status.Kind);
        Assert.Contains(live.SidebarInput!.Workspaces, ws => ws.Id == "ws-local");
    }

    [Fact]
    public void BindSelectedCube_defaults_to_sole_local_cube()
    {
        var live = Live();
        live.Cubes = [LocalCube(), DockerCube()];
        AttachSession.BindConnectedPlacementIdentity(live);
        AttachSession.BindSelectedCubeIdentity(live);
        Assert.Equal("plc_local", live.ConnectedPlacementId);
        Assert.Equal("plc_local", live.SelectedCubeId);
    }

    [Fact]
    public void Restore_last_placement_keeps_remote_cube_selected()
    {
        var root = Path.Combine(Path.GetTempPath(), "hypa-cube-pref-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new FileClientViewPreferencesStore(
                new MapEnv(new Dictionary<string, string>
                {
                    [FileAttachConfigLoader.XdgConfigHomeVariable] = root,
                }, home: "/unused-home"));
            var live = Live();
            live.ClientViewPreferences = store;
            live.Cubes = [LocalCube(), DockerCube()];
            live.SelectedCubeId = "plc_docker";
            AttachSession.PersistClientViewPreferences(live);

            var reattached = Live();
            reattached.ClientViewPreferences = store;
            reattached.Cubes = [LocalCube(), DockerCube()];
            AttachSession.RestoreSidebarSectionSplit(reattached);
            AttachSession.BindConnectedPlacementIdentity(reattached);
            AttachSession.BindSelectedCubeIdentity(reattached);
            Assert.Equal("plc_docker", reattached.SelectedCubeId);
            Assert.Equal("plc_local", reattached.ConnectedPlacementId);
            Assert.True(AttachSession.TryResolveRestoredCubeConnect(reattached, out var cube));
            Assert.Equal("plc_docker", cube!.Id);
            Assert.False(AttachSession.TryResolveRestoredCubeConnect(reattached, out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Restore_connect_failure_keeps_local_attach_unfrozen()
    {
        var live = Live();
        live.Cubes = [LocalCube(), DockerCube()];
        live.SelectedCubeId = "plc_docker";
        live.ConnectedPlacementId = "plc_local";
        live.CubesConnect = new FailRetargeter();
        live.SourceTransportEnvelope.StampServerGeneration(1);
        live.SourceEndpointBootId = "local-boot";
        live.LastSnapshotConnectionGeneration = 1;

        await AttachSession.MaybeRestoreSelectedCubeConnectAsync(
            new RecordingPort(), live, tty: null, CancellationToken.None);

        Assert.False(live.PresentationFrozen);
        Assert.False(live.DetachRequested);
        Assert.Null(live.StatusError);
        Assert.True(live.RestoredCubeConnectAttempted);
        Assert.Equal("plc_local", live.ConnectedPlacementId);
    }

    [Fact]
    public async Task Restore_connect_cancel_does_not_abort_local_attach()
    {
        var live = Live();
        live.Cubes = [LocalCube(), DockerCube()];
        live.SelectedCubeId = "plc_docker";
        live.ConnectedPlacementId = "plc_local";
        live.CubesConnect = new CancelRetargeter();
        live.SourceTransportEnvelope.StampServerGeneration(1);
        live.SourceEndpointBootId = "local-boot";
        live.LastSnapshotConnectionGeneration = 1;

        await AttachSession.MaybeRestoreSelectedCubeConnectAsync(
            new RecordingPort(), live, tty: null, CancellationToken.None);

        Assert.False(live.PresentationFrozen);
        Assert.False(live.DetachRequested);
        Assert.Null(live.StatusError);
        Assert.True(live.RestoredCubeConnectAttempted);
        Assert.Equal("plc_local", live.ConnectedPlacementId);
    }

    [Fact]
    public async Task Restore_without_source_identity_does_not_connect()
    {
        var live = Live();
        live.Cubes = [LocalCube(), DockerCube()];
        live.SelectedCubeId = "plc_docker";
        live.ConnectedPlacementId = "plc_local";
        live.CubesConnect = new FailRetargeter();

        await AttachSession.MaybeRestoreSelectedCubeConnectAsync(
            new RecordingPort(), live, tty: null, CancellationToken.None);

        Assert.False(live.PresentationFrozen);
        Assert.False(live.DetachRequested);
        Assert.Null(live.StatusError);
        Assert.True(live.RestoredCubeConnectAttempted);
        Assert.Equal("plc_local", live.ConnectedPlacementId);
    }

    [Fact]
    public void Restore_local_cube_does_not_start_connect()
    {
        var live = Live();
        live.Cubes = [LocalCube(), DockerCube()];
        live.SelectedCubeId = "plc_local";
        Assert.False(AttachSession.TryResolveRestoredCubeConnect(live, out var cube));
        Assert.Null(cube);
        Assert.False(live.RestoredCubeConnectAttempted);
    }

    [Fact]
    public void SelectCube_without_remembered_snapshot_does_not_store_last_snapshot()
    {
        var live = Live();
        live.Cubes = [LocalCube(), DockerCube()];
        live.ConnectedPlacementId = "plc_docker";
        live.SelectedCubeId = "plc_docker";
        live.LastSnapshot = DockerSnapshot();
        AttachSession.RememberCubeSnapshot(live, "plc_docker", DockerSnapshot());

        AttachSession.SelectCube(live, LocalCube());

        Assert.Equal("plc_local", live.SelectedCubeId);
        Assert.False(live.CubeSnapshots.ContainsKey("plc_local"));
        Assert.True(live.CubeSnapshots.ContainsKey("plc_docker"));
    }

    [Fact]
    public async Task Local_click_on_frozen_session_unfreezes_and_selects_local()
    {
        var live = Live();
        live.Cubes = [LocalCube(), DockerCube()];
        live.ConnectedPlacementId = "plc_docker";
        live.SelectedCubeId = "plc_docker";
        live.PresentationFrozen = true;
        live.PaneId = "p1";
        live.InputLease = "dest_input";
        live.ResizeLease = "dest_resize";
        live.Renew = new LeaseRenewLoop((_, _, _) => Task.CompletedTask);
        live.Renew.Track("src_input");
        live.Renew.Track("src_resize");
        live.SetPaneFrame(MouseTestGeom.Frame("local-cells", "p1", 80));
        AttachSession.RememberCubeSnapshot(live, "plc_local", LocalSnapshot());
        AttachSession.RememberCubeSnapshot(live, "plc_docker", DockerSnapshot());
        var sourcePort = new MouseRecordingPort
        {
            Handler = (method, parameters) =>
            {
                if (method == ProtocolMethods.RuntimeLeaseClaim)
                {
                    var scope = parameters?["scope"]?.GetValue<string>();
                    return Parse($"{{\"outcome\":\"granted\",\"lease_id\":\"src_{scope}_fresh\"}}");
                }

                return Parse("{}");
            },
        };
        var source = new ControlPlaneClient("/tmp/hypa-local-click-source.sock");
        live.SourceBackup = new AttachRetargetPresentationBackup
        {
            SourceClient = source,
            SourcePort = sourcePort,
            ConnectedPlacementId = "plc_local",
            PlacementKind = SidebarCubeKind.Local,
            PlacementDisplayName = "Local",
            PaneId = "p1",
            WorkspaceId = "ws-local",
            TabId = "t1",
            InputLease = "src_input",
            ResizeLease = "src_resize",
        };

        await AttachSession.ApplyCubesConnectAsync(
            new MouseEngineResult(MouseCommandKind.ApplyMenu, PlacementId: "plc_local"),
            live,
            sourcePort,
            tty: null,
            CancellationToken.None);

        Assert.False(live.PresentationFrozen);
        Assert.False(live.InputFrozen);
        Assert.Equal("plc_local", live.SelectedCubeId);
        Assert.Equal("plc_local", live.ConnectedPlacementId);
        Assert.Equal("", live.InputLease);
        Assert.Equal("", live.ResizeLease);
        Assert.False(live.Renew.IsTracked("src_input"));
        Assert.False(live.Renew.IsTracked("src_resize"));
        Assert.DoesNotContain(
            sourcePort.Calls,
            call => call.Method == ProtocolMethods.RuntimeLeaseClaim);
        Assert.True(live.TryGetPaneFrame("p1", out _));
        Assert.Contains(live.SidebarInput!.Workspaces, ws => ws.Id == "ws-local");
        Assert.DoesNotContain(live.SidebarInput.Workspaces, ws => ws.Id == "ws-docker");
        Assert.False(live.DetachRequested);
        Assert.Equal("plc_local", live.ActiveProjectionEndpointId);
    }

    [Fact]
    public void Selected_cube_snapshot_replaces_last_snapshot_and_clears_outgoing_layout()
    {
        var live = Live();
        live.Cubes = [LocalCube(), DockerCube()];
        live.ConnectedPlacementId = "plc_local";
        live.SelectedCubeId = "plc_local";
        live.ActiveProjectionEndpointId = "plc_local";
        live.LastSnapshot = LocalSnapshot();
        live.ChromeSeed = new ChromeComputeSeed(
            new LayoutNodeDto { Type = "pane", PaneId = "local-pane" },
            Zoomed: false,
            ZoomedPaneId: null,
            FocusedPaneId: "local-pane",
            Cols: 80,
            Rows: 24);
        live.TabHits = [new TabBarTabSpec("tab-local", "local", true)];
        live.SetPaneFrame(MouseTestGeom.Frame("local-cells", "local-pane", 80));

        AttachSession.ApplySelectedCubeSnapshot(live, "plc_docker", DockerSnapshot());

        Assert.Equal("plc_docker", live.ActiveProjectionEndpointId);
        Assert.Equal("ws-docker", live.LastSnapshot?.GetProperty("workspaces")[0].GetProperty("workspace_id").GetString());
        Assert.Null(live.ChromeSeed);
        Assert.Empty(live.TabHits);
        Assert.False(live.TryGetPaneFrame("local-pane", out _));
        Assert.True(live.PendingChromeRefresh);
    }

    [Fact]
    public void Dest_health_after_local_restore_does_not_freeze_local()
    {
        var live = Live();
        live.Cubes = [LocalCube(), DockerCube()];
        live.ConnectedPlacementId = "plc_local";
        live.SelectedCubeId = "plc_local";
        live.PresentationFrozen = false;

        AttachSession.HandleEndpointDisconnect(
            live,
            "plc_docker",
            1,
            "health timed out",
            tty: null);

        Assert.False(live.PresentationFrozen);
        Assert.False(live.DetachRequested);
        Assert.Null(live.StatusError);
    }

    [Fact]
    public void Spaces_footer_new_action_does_not_name_the_cube()
    {
        var strategy = new SpacesChromeSectionStrategy();
        var resolved = new ResolvedSidebarSection
        {
            Id = SidebarTokenGrammar.SpacesId,
            Title = "workspaces",
            Order = 0,
            Collapsed = false,
            Config = AttachSidebarSectionConfig.SpacesDefault,
        };
        var view = strategy.Compose(
            new SidebarComposeInput
            {
                Ui = AttachUiConfig.Default,
                Expanded = true,
                MouseCapture = true,
                RequestedWidth = 26,
                FocusedCubeId = "plc_docker",
                Cubes = [LocalCube(), DockerCube()],
            },
            resolved,
            collapsed: false,
            visible: true,
            width: 26,
            SidebarCollapseDisplay.Expanded);
        var neu = Assert.Single(view.Actions, action => action.Id == "new");
        Assert.Equal(SpacesChromeSectionStrategy.NewActionLabel, neu.Label);
        Assert.DoesNotContain("Docker", neu.Label, StringComparison.Ordinal);
        Assert.DoesNotContain("Local", neu.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void Trailing_status_differs_for_selected_connecting_versus_connected()
    {
        var connecting = CubesChromeSectionStrategy.TrailingStatus(
            DockerCube(),
            focusedCubeId: "plc_docker",
            connectedCubeId: "plc_local");
        var connected = CubesChromeSectionStrategy.TrailingStatus(
            DockerCube(),
            focusedCubeId: "plc_docker",
            connectedCubeId: "plc_docker");
        Assert.Equal("◐", connecting.Glyph);
        Assert.Equal(CubeTrailingStatusKind.Connecting, connecting.Kind);
        Assert.Equal("●", connected.Glyph);
        Assert.Equal(CubeTrailingStatusKind.Online, connected.Kind);
        Assert.Null(CubesChromeSectionStrategy.TrailingStatus(LocalCube(), "plc_local", "plc_local").Glyph);
    }

    [Fact]
    public void FromSnapshot_stamps_focused_cube_id()
    {
        var input = SidebarLiveModel.FromSnapshot(
            LocalSnapshot(),
            AttachUiConfig.Default,
            expanded: true,
            requestedWidth: 26,
            cubes: [LocalCube(), DockerCube()],
            focusedCubeId: "plc_docker",
            connectedCubeId: "plc_local");
        Assert.Equal("plc_docker", input.FocusedCubeId);
        Assert.Equal("plc_local", input.ConnectedCubeId);
    }

    [Fact]
    public void Paint_selected_cube_uses_visible_marker_and_selection_bg()
    {
        var frame = CubeFrame();
        var geo = CubeGeometry(frame);
        var host = Paint(geo);
        var docker = geo.SidebarRows.First(hit =>
            hit.Kind is SidebarStubKind.Cube
            && hit.Id == "plc_docker"
            && hit.CardRowIndex == 0);
        var local = geo.SidebarRows.First(hit =>
            hit.Kind is SidebarStubKind.Cube
            && hit.Id == "plc_local"
            && hit.CardRowIndex == 0);
        var dockerText = ReadRow(host, docker.Rect.Col, docker.Rect.Row, docker.Rect.Cols);
        var localText = ReadRow(host, local.Rect.Col, local.Rect.Row, local.Rect.Cols);
        Assert.Contains('>', localText);
        Assert.DoesNotContain('>', dockerText);
        Assert.Contains('◐', dockerText);
        Assert.Equal(
            HostFrameCellSink.Encode(ThemePalette.Catppuccin.SelectionBg),
            host.CellAt(local.Rect.Col, local.Rect.Row).Style.Bg);
        Assert.NotEqual(
            host.CellAt(docker.Rect.Col, docker.Rect.Row).Style.Bg,
            host.CellAt(local.Rect.Col, local.Rect.Row).Style.Bg);
        var spaces = geo.SidebarFrame!.Panes.Single(pane => pane.Slot is SidebarPaneSlot.Spaces);
        // The spaces footer carries New and Menu. It no longer names the cube.
        Assert.Contains(spaces.Actions, action => action.Id == "new");
        Assert.Contains(spaces.Actions, action => action.Id == "menu");
    }

    [Fact]
    public void Paint_cube_scroll_matches_hit_rects()
    {
        var frame = CubeFrame();
        var geo = CubeGeometry(
            frame,
            spacesScroll: 1,
            resourceScrolls: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [SidebarTokenGrammar.CubesId] = 0,
            });
        var body = geo.ResourceSectionBodies
            .First(section => string.Equals(section.SectionId, SidebarTokenGrammar.CubesId, StringComparison.Ordinal))
            .Body;
        var painted = LayoutChromePainter.SelectionMarker(
            frame.Panes.Single(pane => pane.Id == SidebarTokenGrammar.CubesId).Rows[0],
            AttachUiConfig.Default.Glyphs);
        var host = Paint(geo);
        var firstPainted = ReadRow(host, body.Col, body.Row, body.Cols);
        Assert.Contains("Local", firstPainted, StringComparison.Ordinal);
        var hit = geo.SidebarRows.First(row =>
            row.Kind is SidebarStubKind.Cube && row.Rect.Row == body.Row);
        Assert.Equal("plc_local", hit.Id);
        Assert.True(string.IsNullOrEmpty(painted) || painted == ">");
    }

    [Fact]
    public void Compact_selected_cube_keeps_marker()
    {
        var frame = SidebarSectionComposer.Compose(
            CubeInput() with { Expanded = false },
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
            sidebarCollapsed: true,
            sidebarWidth: 26,
            sidebarFrame: frame);
        var host = Paint(geo);
        var docker = geo.SidebarRows.First(hit =>
            hit.Kind is SidebarStubKind.Cube && hit.Id == "plc_docker");
        var local = geo.SidebarRows.First(hit =>
            hit.Kind is SidebarStubKind.Cube && hit.Id == "plc_local");
        var dockerText = ReadRow(host, docker.Rect.Col, docker.Rect.Row, docker.Rect.Cols);
        var localText = ReadRow(host, local.Rect.Col, local.Rect.Row, local.Rect.Cols);
        Assert.StartsWith(">", localText.TrimStart());
        Assert.False(dockerText.TrimStart().StartsWith('>'));
    }

    private static AttachLiveState Live()
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        return new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(new RecordingPort(), "ws-local", "t1", "p1", "lease-r"),
            ChromeEnabled = true,
            SidebarOpen = true,
            SidebarRequestedWidth = 26,
            SidebarWidth = 26,
        };
    }

    private static SidebarCubeItem LocalCube() => new()
    {
        Id = "plc_local",
        Name = "Local",
        Kind = SidebarCubeKind.Local,
        Reachability = SidebarCubeReachability.Local,
    };

    private static SidebarCubeItem DockerCube() => new()
    {
        Id = "plc_docker",
        Name = "Docker",
        Kind = SidebarCubeKind.Peer,
        Reachability = SidebarCubeReachability.Reachable,
    };

    private static JsonElement LocalSnapshot() => Parse(
        """
        {
          "focused_workspace_id":"ws-local",
          "workspaces":[
            {"workspace_id":"ws-local","label":"mac","cwd":"/Users/matthew"}
          ]
        }
        """);

    private static JsonElement DockerSnapshot() => Parse(
        """
        {
          "focused_workspace_id":"ws-docker",
          "workspaces":[
            {"workspace_id":"ws-docker","label":"work","cwd":"/work"}
          ]
        }
        """);

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static SidebarComposeInput CubeInput() =>
        new()
        {
            Ui = AttachUiConfig.Default,
            Expanded = true,
            MouseCapture = true,
            RequestedWidth = 26,
            FocusedCubeId = "plc_docker",
            ConnectedCubeId = "plc_local",
            FocusedWorkspaceId = "ws-local",
            Cubes = [LocalCube(), DockerCube()],
            Workspaces = [new SidebarWorkspaceItem { Id = "ws-local", Label = "mac", Order = 0 }],
        };

    private static SidebarFrame CubeFrame() =>
        SidebarSectionComposer.Compose(CubeInput(), ChromeSectionRegistry.MuxRelease());

    private static LayoutChromeGeometry CubeGeometry(
        SidebarFrame frame,
        int spacesScroll = 0,
        IReadOnlyDictionary<string, int>? resourceScrolls = null) =>
        LayoutChromeGeometry.Compute(
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
            sidebarFrame: frame,
            spacesScroll: spacesScroll,
            resourceScrolls: resourceScrolls);

    private static HostFrame Paint(LayoutChromeGeometry geo)
    {
        var host = new HostFrame();
        host.Resize(geo.Cols, geo.Rows);
        LayoutChromePainter.Stamp(
            new HostFrameCellSink(host),
            geo,
            AttachUiConfig.Default,
            theme: ThemePalette.Catppuccin);
        return host;
    }

    private static string ReadRow(HostFrame host, int col, int row, int cols)
    {
        var chars = new char[Math.Max(0, cols)];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = host.CellAt(col + i, row).Text is { Length: > 0 } text ? text[0] : ' ';
        return new string(chars);
    }

    private sealed class FailRetargeter : ICubesConnectRetargeter
    {
        public Task<CubesConnectRetargetOutcome> ConnectAsync(
            CubesConnectRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CubesConnectRetargetOutcome
            {
                Ok = false,
                Reason = CubesConnectReasons.DestConnectFailed,
                Detail = "destination did not accept the join",
                Action = CubesConnectActions.Noop,
                DestinationKind = request.Destination.Kind,
                TransportKind = AttachEndpointKinds.Connectivity,
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
            });
    }

    private sealed class CancelRetargeter : ICubesConnectRetargeter
    {
        public Task<CubesConnectRetargetOutcome> ConnectAsync(
            CubesConnectRequest request,
            CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException();
    }

    private sealed class RecordingPort : IAttachCommandPort
    {
        public Task<JsonElement> CallAsync(string method, System.Text.Json.Nodes.JsonObject? parameters, CancellationToken ct) =>
            Task.FromResult(Parse("{}"));
    }

    private sealed class MapEnv(
        Dictionary<string, string> vars,
        string home) : IAttachConfigEnvironment
    {
        public string? GetVariable(string name) =>
            vars.TryGetValue(name, out var value) ? value : null;

        public string UserHome { get; } = home;
        public string? AppData => null;
        public bool IsWindows => false;
        public bool IsMacOs => false;
    }
}
