using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Overlay;
using Hypa.Cli.Attach.Worktrees;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

public class AttachOverlayReceiveTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public AttachOverlayReceiveTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-ov-rx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // tmp
        }
    }

    [Fact]
    public async Task Owned_hidden_render_stamps_host_foreign_does_not()
    {
        var seed = await SeedAsync(createTiled: true);
        try
        {
            using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
            var live = Live(seed, seed.Tiled!);
            live.Chrome = MouseTestGeom.Split(sidebarOpen: true);
            Assert.True(live.Chrome.Content.Col > 0);
            var assembler = new SnapshotAssembler();
            var port = new DispatchPort(seed.Cp, seed.Owner);

            live.SetPaneFrame(Fill("p-stale", 8, 4, "O") with { PaneId = seed.Hidden });
            var show = await ShowOverlayAsync(seed);
            var ev = Event(
                ProtocolEventTypes.PanePlacementChanged,
                new JsonObject
                {
                    ["mode"] = "overlay",
                    ["pane_id"] = seed.Hidden,
                    ["attach_client_id"] = seed.Owner.ConnectionId,
                    ["overlay_generation"] = show.GetProperty("overlay_generation").GetInt64(),
                });
            await AttachSession.HandleRenderEventAsync(
                ev, port, assembler, tty, live, CancellationToken.None);
            Assert.True(live.Overlay.OwnsModal);
            Assert.True(live.Chrome!.Content.Col >= 0);
            Assert.True(live.Overlay.Geometry!.OuterCol >= live.Chrome.Content.Col);
            Assert.True(live.Overlay.ResizeBeforeFull);
            Assert.NotNull(live.OverlayLeases);
            Assert.Equal(seed.Hidden, live.OverlayLeases!.PaneId);
            Assert.NotEqual(live.ResizeLease, live.OverlayLeases.ResizeLease);
            Assert.NotEqual(live.InputLease, live.OverlayLeases.InputLease);

            var stale = Event(
                ProtocolEventTypes.TerminalRender,
                SnapshotPayload(seed.Hidden, 8, 4, "O", generation: 1));
            await AttachSession.HandleRenderEventAsync(
                stale, port, assembler, tty, live, CancellationToken.None);
            Assert.False(live.Overlay.RevealAccepted);
            Assert.NotEqual("O", live.Host.CellAt(
                live.Overlay.Geometry.InnerCol,
                live.Overlay.Geometry.InnerRow).Text);

            var full = Event(
                ProtocolEventTypes.TerminalRender,
                SnapshotPayload(
                    seed.Hidden,
                    live.Overlay.Geometry.InnerCols,
                    live.Overlay.Geometry.InnerRows,
                    "N",
                    generation: 2));
            await AttachSession.HandleRenderEventAsync(
                full, port, assembler, tty, live, CancellationToken.None);
            Assert.True(live.Overlay.RevealAccepted);
            Assert.Equal("N", live.Host.CellAt(
                live.Overlay.Geometry.InnerCol,
                live.Overlay.Geometry.InnerRow).Text);
            Assert.True(AttachSession.SnapshotContentWasPainted(live, seed.Hidden));
            Assert.True(AttachSession.IsVisiblePane(live, seed.Hidden));
            Assert.Equal(seed.Hidden, live.Engine.OverlayPaneId);
            Assert.Equal(
                live.Overlay.Geometry.InnerCol,
                live.Host.Cursor.Col);
            Assert.Equal(
                live.Overlay.Geometry.InnerRow,
                live.Host.Cursor.Row);

            var foreign = Live(seed, seed.Tiled!, attachClientId: "conn_other");
            foreign.Chrome = live.Chrome;
            await AttachSession.HandleRenderEventAsync(
                full, port, new SnapshotAssembler(), tty, foreign, CancellationToken.None);
            Assert.False(foreign.Overlay.OwnsModal);
            Assert.False(AttachSession.IsVisiblePane(foreign, seed.Hidden));
            Assert.NotEqual("N", foreign.Host.CellAt(
                live.Overlay.Geometry.InnerCol,
                live.Overlay.Geometry.InnerRow).Text);
        }
        finally
        {
            await seed.Cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Same_size_cache_does_not_stamp_until_new_full()
    {
        var seed = await SeedAsync(createTiled: true);
        try
        {
            using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
            var live = Live(seed, seed.Tiled!);
            live.Chrome = MouseTestGeom.Split();
            var assembler = new SnapshotAssembler();
            var port = new DispatchPort(seed.Cp, seed.Owner);
            var show = await ShowOverlayAsync(seed);
            var gen = show.GetProperty("overlay_generation").GetInt64();
            Assert.True(AttachSession.ApplyOverlayPlacement(
                live, Placement("overlay", seed.Hidden, seed.Owner.ConnectionId, gen)));
            await AttachSession.PrepareOverlayAsync(port, live, CancellationToken.None);
            var cols = live.Overlay.Geometry!.InnerCols;
            var rows = live.Overlay.Geometry.InnerRows;
            await AttachSession.HandleRenderEventAsync(
                Event(ProtocolEventTypes.TerminalRender, SnapshotPayload(seed.Hidden, cols, rows, "O", 2)),
                port,
                assembler,
                tty,
                live,
                CancellationToken.None);
            Assert.Equal("O", live.Host.CellAt(
                live.Overlay.Geometry.InnerCol,
                live.Overlay.Geometry.InnerRow).Text);

            Assert.True(AttachSession.ApplyOverlayPlacement(
                live, Placement("hidden", seed.Hidden, seed.Owner.ConnectionId, gen + 1)));
            Assert.True(AttachSession.ApplyOverlayPlacement(
                live, Placement("overlay", seed.Hidden, seed.Owner.ConnectionId, gen + 2)));
            live.SetPaneFrame(Fill(seed.Hidden, cols, rows, "O") with { Generation = 2 });
            await AttachSession.PrepareOverlayAsync(port, live, CancellationToken.None);
            Assert.True(live.Overlay.ResizeBeforeFull);
            Assert.False(live.Overlay.RevealAccepted);
            AttachSession.PaintChrome(tty, live);
            Assert.False(live.Overlay.RevealAccepted);
            Assert.NotEqual("O", live.Host.CellAt(
                live.Overlay.Geometry!.InnerCol,
                live.Overlay.Geometry.InnerRow).Text);
            AttachSession.PaintChrome(tty, live, live.TryGetPaneFrame(seed.Hidden, out var cached) ? cached : null);
            Assert.False(live.Overlay.RevealAccepted);
            Assert.NotEqual("O", live.Host.CellAt(
                live.Overlay.Geometry.InnerCol,
                live.Overlay.Geometry.InnerRow).Text);

            await AttachSession.HandleRenderEventAsync(
                Event(ProtocolEventTypes.TerminalRender, SnapshotPayload(seed.Hidden, cols, rows, "N", 3)),
                port,
                assembler,
                tty,
                live,
                CancellationToken.None);
            Assert.True(live.Overlay.RevealAccepted);
            Assert.Equal("N", live.Host.CellAt(
                live.Overlay.Geometry.InnerCol,
                live.Overlay.Geometry.InnerRow).Text);
        }
        finally
        {
            await seed.Cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Process_exit_event_clears_modal_through_handler()
    {
        var seed = await SeedAsync(createTiled: true);
        try
        {
            using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
            var live = Live(seed, seed.Tiled!);
            live.Chrome = MouseTestGeom.Split();
            var show = await ShowOverlayAsync(seed);
            Assert.True(AttachSession.ApplyOverlayPlacement(
                live,
                Placement("overlay", seed.Hidden, seed.Owner.ConnectionId, show.GetProperty("overlay_generation").GetInt64())));
            Assert.True(live.Overlay.OwnsModal);

            var exit = Event(
                ProtocolEventTypes.PaneLifecycle,
                new JsonObject
                {
                    ["pane_id"] = seed.Hidden,
                    ["state"] = PaneLifecycle.Exited,
                });
            await AttachSession.HandleRenderEventAsync(
                exit,
                new DispatchPort(seed.Cp, seed.Owner),
                new SnapshotAssembler(),
                tty,
                live,
                CancellationToken.None);
            Assert.False(live.Overlay.OwnsModal);
            Assert.False(live.Engine.OverlayOpen);
            Assert.Null(live.OverlayLeases);
        }
        finally
        {
            await seed.Cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Prepare_hide_and_keys_use_hidden_leases()
    {
        var seed = await SeedAsync(createTiled: true);
        try
        {
            var live = Live(seed, seed.Tiled!);
            live.Chrome = MouseTestGeom.Split(sidebarOpen: true);
            var show = await ShowOverlayAsync(seed);
            Assert.True(AttachSession.ApplyOverlayPlacement(
                live,
                Placement("overlay", seed.Hidden, seed.Owner.ConnectionId, show.GetProperty("overlay_generation").GetInt64())));
            var port = new DispatchPort(seed.Cp, seed.Owner);
            await AttachSession.PrepareOverlayAsync(port, live, CancellationToken.None);
            Assert.NotNull(live.OverlayLeases);
            Assert.Equal(seed.Hidden, live.OverlayLeases!.PaneId);
            Assert.NotEqual(live.InputLease, live.OverlayLeases.InputLease);
            Assert.NotEqual(live.ResizeLease, live.OverlayLeases.ResizeLease);
            Assert.True(live.Overlay.ResizeBeforeFull);

            var keys = await port.CallAsync(
                ProtocolMethods.PaneSendKeys,
                AttachSession.OverlaySendKeysBody(live, "x"u8.ToArray()),
                CancellationToken.None);
            Assert.True(keys.GetProperty("accepted_bytes").GetInt32() > 0);

            var tiledKeys = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                port.CallAsync(
                    ProtocolMethods.PaneSendKeys,
                    new JsonObject
                    {
                        ["pane_id"] = seed.Hidden,
                        ["lease_id"] = live.InputLease,
                        ["encoding"] = "base64",
                        ["data"] = Convert.ToBase64String("y"u8.ToArray()),
                        ["overlay_generation"] = live.Overlay.Generation,
                    },
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.LeaseRequired, tiledKeys.Code);

            await AttachSession.HideOwnedOverlayAsync(port, live, CancellationToken.None);
            Assert.False(live.Overlay.OwnsModal);
            Assert.Null(live.OverlayLeases);
            Assert.Equal(seed.Tiled, live.PaneId);
            Assert.True(seed.Cp.PeekRuntime(seed.Hidden)!.IsAlive);
        }
        finally
        {
            await seed.Cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task No_tiled_pane_claims_overlay_leases()
    {
        var seed = await SeedAsync(createTiled: false);
        try
        {
            var live = Live(seed, tiledPane: "");
            live.Chrome = MouseTestGeom.Split(sidebarOpen: true);
            var show = await ShowOverlayAsync(seed);
            Assert.True(AttachSession.ApplyOverlayPlacement(
                live,
                Placement("overlay", seed.Hidden, seed.Owner.ConnectionId, show.GetProperty("overlay_generation").GetInt64())));
            var port = new DispatchPort(seed.Cp, seed.Owner);
            await AttachSession.PrepareOverlayAsync(port, live, CancellationToken.None);
            Assert.NotNull(live.OverlayLeases);
            Assert.Equal(seed.Hidden, live.OverlayLeases!.PaneId);
            Assert.True(live.Overlay.ResizeBeforeFull);
            Assert.True(live.Overlay.Geometry!.OuterCol >= live.Chrome!.Content.Col);

            live.InputLease = "";
            live.PaneId = "";
            Assert.True(AttachSession.OverlayDispatchReady(live, seed.Hidden));
            using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
            using var linked = new CancellationTokenSource();
            var recorder = new RecordingPort(port);
            var events = live.Engine.Feed(KeyChord.Parse("a"));
            await AttachSession.ApplyEngineEventsAsync(
                tty, live, events, recorder, linked, CancellationToken.None);
            var sent = Assert.Single(recorder.Calls, c => c.Method == ProtocolMethods.PaneSendKeys);
            Assert.Equal(seed.Hidden, sent.Params?["pane_id"]!.GetValue<string>());
            Assert.Equal(live.OverlayLeases.InputLease, sent.Params?["lease_id"]!.GetValue<string>());
            Assert.True(sent.Result.GetProperty("accepted_bytes").GetInt32() > 0);

            await AttachSession.HideOwnedOverlayAsync(port, live, CancellationToken.None);
            Assert.False(live.Overlay.OwnsModal);
        }
        finally
        {
            await seed.Cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Resize_failure_and_hide_reshow_block_stale_stamp()
    {
        var seed = await SeedAsync(createTiled: true);
        try
        {
            var live = Live(seed, seed.Tiled!);
            live.Chrome = MouseTestGeom.Split();
            var show = await ShowOverlayAsync(seed);
            var gen = show.GetProperty("overlay_generation").GetInt64();
            Assert.True(AttachSession.ApplyOverlayPlacement(
                live, Placement("overlay", seed.Hidden, seed.Owner.ConnectionId, gen)));
            var port = new DispatchPort(seed.Cp, seed.Owner);
            await AttachSession.PrepareOverlayAsync(port, live, CancellationToken.None);
            var ready = Fill(seed.Hidden, live.Overlay.Geometry!.InnerCols, live.Overlay.Geometry.InnerRows, "N");
            Assert.True(live.Overlay.TryAcceptReveal(ready));

            live.OverlayLeases = new TargetPaneLeasePair
            {
                PaneId = seed.Hidden,
                InputLease = "missing-in",
                ResizeLease = "missing-r",
            };
            live.Overlay.UpdateGeometry(
                ClientOverlayInput.ResolveForContent(new CellRect(0, 0, 100, 30))!);
            await AttachSession.PrepareOverlayAsync(port, live, CancellationToken.None);
            Assert.False(live.Overlay.ResizeBeforeFull);
            Assert.False(live.Overlay.RevealAccepted);

            Assert.True(AttachSession.ApplyOverlayPlacement(
                live, Placement("hidden", seed.Hidden, seed.Owner.ConnectionId, gen + 1)));
            Assert.False(live.Overlay.OwnsModal);

            Assert.True(AttachSession.ApplyOverlayPlacement(
                live, Placement("overlay", seed.Hidden, seed.Owner.ConnectionId, gen + 2)));
            Assert.False(live.Overlay.TryAcceptReveal(ready));
            live.SetPaneFrame(ready);
            Assert.False(live.Overlay.CanStamp(ready));
        }
        finally
        {
            await seed.Cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Host_resize_recomputes_origin_and_requires_new_full()
    {
        var seed = await SeedAsync(createTiled: true);
        try
        {
            var live = Live(seed, seed.Tiled!);
            live.Chrome = MouseTestGeom.Split(sidebarOpen: false);
            var show = await ShowOverlayAsync(seed);
            Assert.True(AttachSession.ApplyOverlayPlacement(
                live,
                Placement("overlay", seed.Hidden, seed.Owner.ConnectionId, show.GetProperty("overlay_generation").GetInt64())));
            var port = new DispatchPort(seed.Cp, seed.Owner);
            await AttachSession.PrepareOverlayAsync(port, live, CancellationToken.None);
            Assert.True(live.Overlay.ResizeBeforeFull);
            var first = live.Overlay.Geometry!;

            live.Chrome = MouseTestGeom.Split(sidebarOpen: true);
            Assert.True(live.Chrome.Content.Col > 0);
            Assert.True(AttachSession.SyncOverlayGeometry(live));
            Assert.False(live.Overlay.ResizeBeforeFull);
            Assert.True(live.Overlay.Geometry!.OuterCol >= live.Chrome.Content.Col);
            Assert.True(live.Overlay.Geometry.OuterCol != first.OuterCol
                || live.Overlay.Geometry.InnerCols != first.InnerCols);
            await AttachSession.PrepareOverlayAsync(port, live, CancellationToken.None);
            Assert.True(live.Overlay.ResizeBeforeFull);
        }
        finally
        {
            await seed.Cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Delayed_overlay_event_loses_to_open_worktree_and_releases_owner()
    {
        var seed = await SeedAsync(createTiled: true);
        try
        {
            using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
            var live = Live(seed, seed.Tiled!);
            live.Chrome = MouseTestGeom.Split();
            live.Worktrees.ShowCreate("ws-parent", "repo", "/tmp/hypa-worktrees", seed: 1);
            live.Engine.ExclusiveSurfaceOpen = true;
            var createBranch = live.Worktrees.Create!.Branch;
            var show = await ShowOverlayAsync(seed);
            var generation = show.GetProperty("overlay_generation").GetInt64();
            Assert.True(seed.Cp.Overlay.HasReservation(seed.Owner.ConnectionId));

            await AttachSession.HandleRenderEventAsync(
                Event(
                    ProtocolEventTypes.PanePlacementChanged,
                    new JsonObject
                    {
                        ["mode"] = "overlay",
                        ["pane_id"] = seed.Hidden,
                        ["attach_client_id"] = seed.Owner.ConnectionId,
                        ["overlay_generation"] = generation,
                    }),
                new DispatchPort(seed.Cp, seed.Owner),
                new SnapshotAssembler(),
                tty,
                live,
                CancellationToken.None);

            Assert.True(live.Worktrees.IsOpen);
            Assert.Equal(createBranch, live.Worktrees.Create!.Branch);
            Assert.False(live.Overlay.OwnsModal);
            Assert.False(live.Engine.OverlayOpen);
            Assert.False(seed.Cp.Overlay.HasReservation(seed.Owner.ConnectionId));
        }
        finally
        {
            await seed.Cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Prepare_claim_failures_clear_modal_and_reject_stale_show()
    {
        var seed = await SeedAsync(createTiled: true);
        try
        {
            var show = await ShowOverlayAsync(seed);
            var generation = show.GetProperty("overlay_generation").GetInt64();
            var live = Live(seed, seed.Tiled!);
            live.Chrome = MouseTestGeom.Split();
            Assert.True(AttachSession.ApplyOverlayPlacement(
                live,
                Placement("overlay", seed.Hidden, seed.Owner.ConnectionId, generation)));

            var dispatch = new DispatchPort(seed.Cp, seed.Owner);
            await AttachSession.PrepareOverlayAsync(null, live, CancellationToken.None);
            Assert.False(live.Overlay.OwnsModal);
            Assert.False(live.Engine.OverlayOpen);
            await AttachSession.CancelOwnedOverlayReservationAsync(
                dispatch,
                live,
                seed.Hidden,
                generation,
                CancellationToken.None);
            Assert.False(seed.Cp.Overlay.HasReservation(seed.Owner.ConnectionId));

            var shownAgain = await ShowOverlayAsync(seed);
            var nextGen = shownAgain.GetProperty("overlay_generation").GetInt64();
            Assert.True(AttachSession.ApplyOverlayPlacement(
                live,
                Placement("overlay", seed.Hidden, seed.Owner.ConnectionId, nextGen)));
            var denied = new ClaimScriptPort(new DispatchPort(seed.Cp, seed.Owner))
            {
                DenyAll = true,
            };
            await AttachSession.PrepareOverlayAsync(denied, live, CancellationToken.None);
            Assert.False(live.Overlay.OwnsModal);
            Assert.False(seed.Cp.Overlay.HasReservation(seed.Owner.ConnectionId));
            Assert.Contains(denied.Calls, c => c == ProtocolMethods.PaneHide);

            var third = await ShowOverlayAsync(seed);
            var thirdGen = third.GetProperty("overlay_generation").GetInt64();
            Assert.True(AttachSession.ApplyOverlayPlacement(
                live,
                Placement("overlay", seed.Hidden, seed.Owner.ConnectionId, thirdGen)));
            var emptyClaim = new ClaimScriptPort(new DispatchPort(seed.Cp, seed.Owner))
            {
                EmptyLease = true,
            };
            await AttachSession.PrepareOverlayAsync(emptyClaim, live, CancellationToken.None);
            Assert.False(live.Overlay.OwnsModal);
            Assert.False(seed.Cp.Overlay.HasReservation(seed.Owner.ConnectionId));

            var fourth = await ShowOverlayAsync(seed);
            var fourthGen = fourth.GetProperty("overlay_generation").GetInt64();
            Assert.True(AttachSession.ApplyOverlayPlacement(
                live,
                Placement("overlay", seed.Hidden, seed.Owner.ConnectionId, fourthGen)));
            var partial = new ClaimScriptPort(new DispatchPort(seed.Cp, seed.Owner))
            {
                FailResizeClaim = true,
            };
            await AttachSession.PrepareOverlayAsync(partial, live, CancellationToken.None);
            Assert.False(live.Overlay.OwnsModal);
            Assert.False(seed.Cp.Overlay.HasReservation(seed.Owner.ConnectionId));

            await AttachSession.HandleRenderEventAsync(
                Event(
                    ProtocolEventTypes.PanePlacementChanged,
                    new JsonObject
                    {
                        ["mode"] = "overlay",
                        ["pane_id"] = seed.Hidden,
                        ["attach_client_id"] = seed.Owner.ConnectionId,
                        ["overlay_generation"] = fourthGen,
                    }),
                new DispatchPort(seed.Cp, seed.Owner),
                new SnapshotAssembler(),
                tty: null,
                live,
                CancellationToken.None);
            Assert.False(live.Overlay.OwnsModal);
        }
        finally
        {
            await seed.Cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Rejected_show_fences_generation_so_replay_cannot_own()
    {
        var seed = await SeedAsync(createTiled: true);
        try
        {
            using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
            var live = Live(seed, seed.Tiled!);
            live.Chrome = MouseTestGeom.Split();
            live.Worktrees.ShowCreate("ws-parent", "repo", "/tmp/hypa-worktrees", seed: 1);
            live.Engine.ExclusiveSurfaceOpen = true;
            var show = await ShowOverlayAsync(seed);
            var generation = show.GetProperty("overlay_generation").GetInt64();
            var ev = Event(
                ProtocolEventTypes.PanePlacementChanged,
                new JsonObject
                {
                    ["mode"] = "overlay",
                    ["pane_id"] = seed.Hidden,
                    ["attach_client_id"] = seed.Owner.ConnectionId,
                    ["overlay_generation"] = generation,
                });
            var port = new DispatchPort(seed.Cp, seed.Owner);
            await AttachSession.HandleRenderEventAsync(
                ev, port, new SnapshotAssembler(), tty, live, CancellationToken.None);
            Assert.True(live.Worktrees.IsOpen);
            Assert.False(live.Overlay.OwnsModal);
            Assert.Equal(generation, live.Overlay.AppliedGeneration);
            Assert.False(seed.Cp.Overlay.HasReservation(seed.Owner.ConnectionId));

            live.Worktrees.Cancel();
            live.Engine.ExclusiveSurfaceOpen = false;
            await AttachSession.HandleRenderEventAsync(
                ev, port, new SnapshotAssembler(), tty, live, CancellationToken.None);
            Assert.False(live.Overlay.OwnsModal);
            Assert.Equal(generation, live.Overlay.AppliedGeneration);
            Assert.False(live.Engine.OverlayOpen);

            var next = await ShowOverlayAsync(seed);
            var newer = next.GetProperty("overlay_generation").GetInt64();
            Assert.True(newer > generation);
            Assert.True(AttachSession.ApplyOverlayPlacement(
                live,
                Placement("overlay", seed.Hidden, seed.Owner.ConnectionId, newer)));
            Assert.True(live.Overlay.OwnsModal);
            Assert.Equal(newer, live.Overlay.AppliedGeneration);
        }
        finally
        {
            await seed.Cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Persist_fail_owner_cancel_keeps_local_and_escape_retries()
    {
        var persist = new ArmFailStore(new SqliteRuntimeSessionStore(_paths));
        var seed = await SeedAsync(createTiled: true, persist);
        try
        {
            var show = await ShowOverlayAsync(seed);
            var generation = show.GetProperty("overlay_generation").GetInt64();
            var live = Live(seed, seed.Tiled!);
            live.Chrome = MouseTestGeom.Split();
            Assert.True(AttachSession.ApplyOverlayPlacement(
                live,
                Placement("overlay", seed.Hidden, seed.Owner.ConnectionId, generation)));
            var port = new DispatchPort(seed.Cp, seed.Owner);
            persist.FailNextSave = true;
            await AttachSession.CancelOwnedOverlayReservationAsync(
                port, live, seed.Hidden, generation, CancellationToken.None);
            Assert.True(seed.Cp.Overlay.HasReservation(seed.Owner.ConnectionId));
            Assert.True(live.Overlay.OwnsModal);
            Assert.True(live.Engine.OverlayOpen);
            Assert.Equal(generation, live.Overlay.Generation);

            persist.FailNextSave = false;
            live.Engine.OverlayOpen = true;
            live.Engine.OverlayPaneId = seed.Hidden;
            var hide = live.Engine.Feed(KeyChord.Parse("esc"));
            Assert.Equal(KeyEngineEventKind.HideOverlay, hide[0].Kind);
            await AttachSession.HideOwnedOverlayAsync(port, live, CancellationToken.None);
            Assert.False(live.Overlay.OwnsModal);
            Assert.False(seed.Cp.Overlay.HasReservation(seed.Owner.ConnectionId));
            Assert.Equal(generation, live.Overlay.AppliedGeneration);
        }
        finally
        {
            await seed.Cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Persist_fail_reject_tracks_cancel_and_retries_after_human_close()
    {
        var persist = new ArmFailStore(new SqliteRuntimeSessionStore(_paths));
        var seed = await SeedAsync(createTiled: true, persist);
        try
        {
            using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
            var live = Live(seed, seed.Tiled!);
            live.Chrome = MouseTestGeom.Split();
            live.Worktrees.ShowCreate("ws-parent", "repo", "/tmp/hypa-worktrees", seed: 1);
            live.Engine.ExclusiveSurfaceOpen = true;
            var show = await ShowOverlayAsync(seed);
            var generation = show.GetProperty("overlay_generation").GetInt64();
            var ev = Event(
                ProtocolEventTypes.PanePlacementChanged,
                new JsonObject
                {
                    ["mode"] = "overlay",
                    ["pane_id"] = seed.Hidden,
                    ["attach_client_id"] = seed.Owner.ConnectionId,
                    ["overlay_generation"] = generation,
                });
            var port = new DispatchPort(seed.Cp, seed.Owner);
            persist.FailNextSave = true;
            await AttachSession.HandleRenderEventAsync(
                ev, port, new SnapshotAssembler(), tty, live, CancellationToken.None);
            Assert.True(live.Worktrees.IsOpen);
            Assert.False(live.Overlay.OwnsModal);
            Assert.False(live.Engine.OverlayOpen);
            Assert.True(live.Overlay.HasRecoverableCancel);
            Assert.True(live.Overlay.MatchesRecoverableCancel(seed.Hidden, generation));
            Assert.True(seed.Cp.Overlay.HasReservation(seed.Owner.ConnectionId));
            Assert.Equal(0, live.Overlay.AppliedGeneration);

            live.Worktrees.Cancel();
            live.Engine.ExclusiveSurfaceOpen = false;
            persist.FailNextSave = false;
            await AttachSession.HandleRenderEventAsync(
                ev, port, new SnapshotAssembler(), tty, live, CancellationToken.None);
            Assert.False(live.Overlay.OwnsModal);
            Assert.False(live.Overlay.HasRecoverableCancel);
            Assert.False(seed.Cp.Overlay.HasReservation(seed.Owner.ConnectionId));
            Assert.Equal(generation, live.Overlay.AppliedGeneration);
        }
        finally
        {
            await seed.Cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Escape_after_human_close_releases_rejected_persist_fail()
    {
        var persist = new ArmFailStore(new SqliteRuntimeSessionStore(_paths));
        var seed = await SeedAsync(createTiled: true, persist);
        try
        {
            using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
            var live = Live(seed, seed.Tiled!);
            live.Chrome = MouseTestGeom.Split();
            live.Worktrees.ShowCreate("ws-parent", "repo", "/tmp/hypa-worktrees", seed: 1);
            live.Engine.ExclusiveSurfaceOpen = true;
            var show = await ShowOverlayAsync(seed);
            var generation = show.GetProperty("overlay_generation").GetInt64();
            var ev = Event(
                ProtocolEventTypes.PanePlacementChanged,
                new JsonObject
                {
                    ["mode"] = "overlay",
                    ["pane_id"] = seed.Hidden,
                    ["attach_client_id"] = seed.Owner.ConnectionId,
                    ["overlay_generation"] = generation,
                });
            var port = new DispatchPort(seed.Cp, seed.Owner);
            persist.FailNextSave = true;
            await AttachSession.HandleRenderEventAsync(
                ev, port, new SnapshotAssembler(), tty, live, CancellationToken.None);
            Assert.True(live.Worktrees.IsOpen);
            Assert.False(live.Overlay.OwnsModal);
            Assert.False(live.Engine.OverlayOpen);
            Assert.True(live.Overlay.HasRecoverableCancel);
            Assert.True(seed.Cp.Overlay.HasReservation(seed.Owner.ConnectionId));
            Assert.False(string.IsNullOrWhiteSpace(live.StatusError));
            var failedStatus = live.StatusError;

            live.Worktrees.Cancel();
            live.Engine.ExclusiveSurfaceOpen = false;
            persist.FailNextSave = false;
            Assert.False(AttachClientModePublication.HumanExclusiveSurfaceOpen(live));
            Assert.False(live.Engine.OverlayOpen);
            Assert.True(seed.Cp.Overlay.HasReservation(seed.Owner.ConnectionId));

            using var gate = new SemaphoreSlim(1, 1);
            using var linked = new CancellationTokenSource();
            var recorder = new RecordingPort(port);
            await AttachSession.DispatchKeysUnderGateAsync(
                tty,
                live,
                [0x1b],
                recorder,
                gate,
                linked,
                CancellationToken.None);

            Assert.False(live.Worktrees.IsOpen);
            Assert.False(live.Overlay.OwnsModal);
            Assert.False(live.Engine.OverlayOpen);
            Assert.False(live.Overlay.HasRecoverableCancel);
            Assert.False(seed.Cp.Overlay.HasReservation(seed.Owner.ConnectionId));
            Assert.Null(live.StatusError);
            Assert.NotEqual(failedStatus, live.StatusError);
            Assert.Contains(recorder.Calls, c => c.Method == ProtocolMethods.PaneHide);
            Assert.DoesNotContain(
                recorder.Calls,
                c => c.Method == ProtocolMethods.PaneSendKeys
                    && c.Params?["pane_id"]!.GetValue<string>() == seed.Hidden);

            recorder.Calls.Clear();
            await AttachSession.DispatchKeysUnderGateAsync(
                tty,
                live,
                [(byte)'x'],
                recorder,
                gate,
                linked,
                CancellationToken.None);
            var parentKeys = Assert.Single(recorder.Calls, c => c.Method == ProtocolMethods.PaneSendKeys);
            Assert.Equal(seed.Tiled, parentKeys.Params?["pane_id"]!.GetValue<string>());
            Assert.Equal(live.InputLease, parentKeys.Params?["lease_id"]!.GetValue<string>());
        }
        finally
        {
            await seed.Cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Foreign_and_stale_owner_cancel_do_not_clear_other_generation()
    {
        var seed = await SeedAsync(createTiled: true);
        try
        {
            var show = await ShowOverlayAsync(seed);
            var generation = show.GetProperty("overlay_generation").GetInt64();
            var ownerLive = Live(seed, seed.Tiled!);
            ownerLive.Chrome = MouseTestGeom.Split();
            Assert.True(AttachSession.ApplyOverlayPlacement(
                ownerLive,
                Placement("overlay", seed.Hidden, seed.Owner.ConnectionId, generation)));
            var foreignLive = Live(seed, seed.Tiled!, attachClientId: "conn_foreign");
            foreignLive.Chrome = ownerLive.Chrome;
            var foreignPort = new DispatchPort(seed.Cp, new OwnerConnection("conn_foreign"));
            await AttachSession.CancelOwnedOverlayReservationAsync(
                foreignPort,
                foreignLive,
                seed.Hidden,
                generation,
                CancellationToken.None);
            Assert.True(seed.Cp.Overlay.HasReservation(seed.Owner.ConnectionId));
            Assert.True(ownerLive.Overlay.OwnsModal);
            Assert.Equal(generation, ownerLive.Overlay.Generation);
            Assert.False(foreignLive.Overlay.OwnsModal);
            Assert.Equal(generation, foreignLive.Overlay.AppliedGeneration);

            var ownerPort = new DispatchPort(seed.Cp, seed.Owner);
            await AttachSession.CancelOwnedOverlayReservationAsync(
                ownerPort,
                ownerLive,
                seed.Hidden,
                generation - 1,
                CancellationToken.None);
            Assert.True(seed.Cp.Overlay.HasReservation(seed.Owner.ConnectionId));
            Assert.True(ownerLive.Overlay.OwnsModal);
            Assert.Equal(generation, ownerLive.Overlay.Generation);
            Assert.Equal(generation, ownerLive.Overlay.AppliedGeneration);
        }
        finally
        {
            await seed.Cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private async Task<Seed> SeedAsync(bool createTiled, IRuntimeSessionStore? persistStore = null)
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        var state = new AppState(SessionId.New("pane-ov-rx"));
        state.UpdateSession(s => s with { Name = "pane-ov-rx", LifecycleState = SessionLifecycle.Ready });
        IRuntimeSessionStore store = persistStore ?? new SqliteRuntimeSessionStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
        var manifests = new SqliteJournalManifestStore(_paths);
        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);
        var cp = new ControlPlaneService(
            state,
            new GraphPaneFactory(),
            new NullIntel(),
            new NullDetector(),
            store: store,
            journal: journal,
            subscriptions: new EventSubscriptionHub());
        var ws = await cp.DispatchAsync(
            ProtocolMethods.WorkspaceCreate,
            JsonDocument.Parse(new JsonObject
            {
                ["cwd"] = Path.GetTempPath(),
                ["create_pane"] = createTiled,
                ["command"] = "/bin/echo",
            }.ToJsonString()).RootElement,
            CancellationToken.None);
        var workspaceId = ws.GetProperty("workspace_id").GetString()!;
        string? tiled = null;
        string tabId;
        if (createTiled)
        {
            tiled = ws.GetProperty("pane").GetProperty("pane_id").GetString()!;
            tabId = ws.GetProperty("pane").TryGetProperty("tab_id", out var tab)
                ? tab.GetString()!
                : ws.GetProperty("tab_id").GetString()!;
        }
        else
        {
            tabId = ws.TryGetProperty("tab_id", out var tab)
                ? tab.GetString()!
                : "";
        }

        var hiddenPane = await cp.DispatchAsync(
            ProtocolMethods.PaneCreate,
            JsonDocument.Parse(new JsonObject
            {
                ["workspace_id"] = workspaceId,
                ["command"] = "/bin/echo",
                ["placement"] = "hidden",
            }.ToJsonString()).RootElement,
            CancellationToken.None);
        var hidden = hiddenPane.GetProperty("pane_id").GetString()!;
        if (string.IsNullOrWhiteSpace(tabId)
            && hiddenPane.TryGetProperty("tab_id", out var hiddenTab))
        {
            tabId = hiddenTab.GetString() ?? "";
        }

        var owner = new OwnerConnection("conn_owner");
        var subscribed = await cp.DispatchAsync(
            ProtocolMethods.EventsSubscribe,
            JsonDocument.Parse(new JsonObject
            {
                ["live"] = true,
            }.ToJsonString()).RootElement,
            owner,
            CancellationToken.None);
        var subscriptionId = subscribed.GetProperty("subscription_id").GetString()!;
        await cp.DispatchAsync(
            ProtocolMethods.UiClientMode,
            JsonDocument.Parse("""{"client_mode":"terminal"}""").RootElement,
            owner,
            CancellationToken.None);
        string tiledInput = "tiled-in";
        string tiledResize = "tiled-r";
        if (tiled is not null)
        {
            var inClaim = await cp.DispatchAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = tiled,
                    ["scope"] = "input",
                }.ToJsonString()).RootElement,
                owner,
                CancellationToken.None);
            var resizeClaim = await cp.DispatchAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = tiled,
                    ["scope"] = "resize",
                }.ToJsonString()).RootElement,
                owner,
                CancellationToken.None);
            tiledInput = inClaim.GetProperty("lease_id").GetString()!;
            tiledResize = resizeClaim.GetProperty("lease_id").GetString()!;
        }

        return new Seed(cp, hidden, tiled, workspaceId, tabId, owner, tiledInput, tiledResize, subscriptionId);
    }

    private static async Task<JsonElement> ShowOverlayAsync(Seed seed)
    {
        var claim = await seed.Cp.DispatchAsync(
            ProtocolMethods.RuntimeLeaseClaim,
            JsonDocument.Parse(new JsonObject
            {
                ["pane_id"] = seed.Hidden,
                ["scope"] = "input",
            }.ToJsonString()).RootElement,
            seed.Owner,
            CancellationToken.None);
        return await seed.Cp.DispatchAsync(
            ProtocolMethods.PaneShow,
            JsonDocument.Parse(new JsonObject
            {
                ["pane_id"] = seed.Hidden,
                ["lease_id"] = claim.GetProperty("lease_id").GetString(),
                ["mode"] = "overlay",
                ["attach_client_id"] = seed.Owner.ConnectionId,
                ["area_cols"] = 80,
                ["area_rows"] = 24,
            }.ToJsonString()).RootElement,
            seed.Owner,
            CancellationToken.None);
    }

    private static AttachLiveState Live(Seed seed, string tiledPane, string? attachClientId = null)
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(
                new MouseRecordingPort(),
                seed.WorkspaceId,
                seed.TabId,
                tiledPane,
                seed.TiledResize),
            Renew = new LeaseRenewLoop((_, _, _) => Task.CompletedTask),
            WorkspaceId = seed.WorkspaceId,
            TabId = seed.TabId,
            PaneId = tiledPane,
            AttachClientId = attachClientId ?? seed.Owner.ConnectionId,
            InputLease = seed.TiledInput,
            ResizeLease = seed.TiledResize,
            ChromeEnabled = true,
            RenderSub = seed.SubscriptionId,
        };
        live.Renew.Track(live.InputLease);
        live.Renew.Track(live.ResizeLease);
        return live;
    }

    private static JsonElement Placement(string mode, string paneId, string client, long generation) =>
        JsonDocument.Parse(new JsonObject
        {
            ["mode"] = mode,
            ["pane_id"] = paneId,
            ["attach_client_id"] = client,
            ["overlay_generation"] = generation,
        }.ToJsonString()).RootElement.Clone();

    private static JsonElement Event(string type, JsonObject payload) =>
        JsonDocument.Parse(new JsonObject
        {
            ["params"] = new JsonObject
            {
                ["type"] = type,
                ["payload"] = payload,
            },
        }.ToJsonString()).RootElement.Clone();

    private static JsonObject SnapshotPayload(
        string paneId,
        int cols,
        int rows,
        string glyph,
        long generation)
    {
        var cells = new JsonArray();
        for (var r = 0; r < rows; r++)
        {
            var row = new JsonArray();
            for (var c = 0; c < cols; c++)
                row.Add(new JsonObject { ["text"] = glyph });
            cells.Add(row);
        }

        return new JsonObject
        {
            ["pane_id"] = paneId,
            ["kind"] = "snapshot",
            ["row_start"] = 0,
            ["row_end"] = rows,
            ["complete"] = true,
            ["grid_cols"] = cols,
            ["grid_rows"] = rows,
            ["generation"] = generation,
            ["occupant_generation"] = 1,
            ["snapshot"] = new JsonObject
            {
                ["cells"] = cells,
                ["cursor"] = new JsonObject
                {
                    ["col"] = 0,
                    ["row"] = 0,
                    ["visible"] = true,
                },
            },
        };
    }

    private static AssembledSnapshot Fill(string paneId, int cols, int rows, string glyph)
    {
        var cells = new AssembledCell[rows][];
        for (var r = 0; r < rows; r++)
        {
            cells[r] = new AssembledCell[cols];
            for (var c = 0; c < cols; c++)
                cells[r][c] = new AssembledCell(glyph, 1, false, AssembledStyle.Default);
        }

        return new AssembledSnapshot(
            paneId,
            cols,
            rows,
            "basic",
            "main",
            cells,
            default,
            new AssembledCursor(0, 0, true, HasCursor: true),
            OccupantGeneration: 1,
            Generation: 1,
            IngestFull: true);
    }

    private sealed record Seed(
        ControlPlaneService Cp,
        string Hidden,
        string? Tiled,
        string WorkspaceId,
        string TabId,
        OwnerConnection Owner,
        string TiledInput,
        string TiledResize,
        string SubscriptionId);

    private sealed class RecordingPort(IAttachCommandPort inner) : IAttachCommandPort
    {
        public List<(string Method, JsonObject? Params, JsonElement Result)> Calls { get; } = [];

        public async Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            var result = await inner.CallAsync(method, parameters, ct);
            Calls.Add((method, parameters, result));
            return result;
        }
    }

    private sealed class DispatchPort(ControlPlaneService cp, IClientConnection connection) : IAttachCommandPort
    {
        public async Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(parameters?.ToJsonString() ?? "{}");
            return await cp.DispatchAsync(method, doc.RootElement.Clone(), connection, ct);
        }
    }

    private sealed class ClaimScriptPort(IAttachCommandPort inner) : IAttachCommandPort
    {
        public bool DenyAll { get; set; }
        public bool EmptyLease { get; set; }
        public bool FailResizeClaim { get; set; }
        public List<string> Calls { get; } = [];

        public async Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            Calls.Add(method);
            if (string.Equals(method, ProtocolMethods.RuntimeLeaseClaim, StringComparison.Ordinal))
            {
                if (DenyAll)
                    return JsonDocument.Parse("""{"outcome":"denied"}""").RootElement.Clone();
                if (EmptyLease)
                    return JsonDocument.Parse("{}").RootElement.Clone();
                if (FailResizeClaim
                    && parameters is not null
                    && parameters["scope"]?.GetValue<string>() == "resize")
                {
                    throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "injected resize claim fail");
                }
            }

            return await inner.CallAsync(method, parameters, ct);
        }
    }

    private sealed class ArmFailStore(IRuntimeSessionStore inner) : IRuntimeSessionStore
    {
        public bool FailNextSave { get; set; }

        public Task<RuntimeResult<SessionState?>> TryLoadAsync(string sessionName, CancellationToken ct = default) =>
            inner.TryLoadAsync(sessionName, ct);

        public Task<RuntimeResult<RuntimeUnit>> SaveAsync(
            SessionState state,
            CancellationToken ct = default,
            bool removeMissingPanes = false)
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                return Task.FromResult(RuntimeResult<RuntimeUnit>.Fail(
                    RuntimePersistenceError.Db("injected persist fail")));
            }

            return inner.SaveAsync(state, ct, removeMissingPanes);
        }

        public Task<RuntimeResult<RuntimeUnit>> DeletePaneAsync(PaneId paneId, CancellationToken ct = default) =>
            inner.DeletePaneAsync(paneId, ct);

        public Task<RuntimeResult<string>> GetRuntimeSessionIdAsync(CancellationToken ct = default) =>
            inner.GetRuntimeSessionIdAsync(ct);
    }

    private sealed class OwnerConnection(string id) : IClientConnection
    {
        public string ConnectionId { get; } = id;
        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class GraphPaneFactory : IPaneRuntimeFactory
    {
        public IPaneRuntime Create(PaneSpawnOptions options) => new GraphPaneRuntime(options.Id);
    }

    private sealed class GraphPaneRuntime(PaneId id) : IPaneRuntime
    {
        public PaneId Id { get; } = id;
        public bool IsAlive { get; private set; } = true;
        public int? ExitCode { get; private set; }
        public int? Pid { get; private set; } = 52_139;
#pragma warning disable CS0067
        public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
        public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
        public event Action<IPaneRuntime, int>? Exited;
#pragma warning restore CS0067

        public Task StartAsync(CancellationToken ct)
        {
            IsAlive = true;
            return Task.CompletedTask;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask WriteTextAsync(string text, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct) => ValueTask.CompletedTask;

        public string ReadVisibleText() => "";
        public string ReadRecentText(int maxLines) => "";
        public string ReadRecentUnwrappedText(int maxLines) => ReadRecentText(maxLines);
        public string ReadDetectionText() => "";

        public ValueTask DisposeAsync()
        {
            IsAlive = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NullIntel : IIntelligencePipeline
    {
        public void OnPaneOutput(PaneId paneId, ReadOnlySpan<byte> data)
        {
        }

        public void OnPaneInput(PaneId paneId, string text)
        {
        }

        public string CompressForAgent(PaneId paneId, string rawText, string? commandHint = null) =>
            rawText;

        public void BindAtomic(PaneId paneId, AtomicBinding binding)
        {
        }

        public void RemovePane(PaneId paneId)
        {
        }
    }

    private sealed class NullDetector : IAgentDetector
    {
        public DetectionResult Detect(string snapshotText, string? processName = null) => new();
    }
}
