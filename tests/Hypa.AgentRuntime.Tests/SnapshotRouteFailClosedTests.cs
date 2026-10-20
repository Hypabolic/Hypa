using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// The attach snapshot and the JSON snapshot routes fail closed. A failed
/// render-state update or a failed cell lookup posts nothing, counts its
/// live-stamp fallback reason, and the next paint retries.
/// </summary>
[Trait("Category", "Ghostty")]
[Collection("GhosttyPtyTests")]
public sealed class SnapshotRouteFailClosedTests
{
    private const int Cols = 30;
    private const int Rows = 8;

    private static readonly string? LibPath = GhosttyTestRequire.TryResolveNativeLibraryPath();

    [SkippableFact]
    public async Task Json_scroll_branch_posts_nothing_after_a_failed_render_state_update()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        using var vt = new GhosttyVtEngine(Cols, Rows, libraryPathOverride: LibPath);
        await using var pane = NewPane(vt);
        var counters = Assert.IsType<LiveStampCounters>(pane.LiveStampMeter);
        var lines = new StringBuilder();
        for (var i = 0; i < 30; i++)
            lines.Append("scroll line ").Append(i).Append("\r\n");
        pane.FeedVtForTests(lines.ToString());
        Assert.True(pane.TrySetScrollOrigin(2));

        vt.ForceRenderStateUpdateFailure = true;
        var before = counters.Fallbacks(LiveStampFallbackReason.RenderStateUpdate);
        Assert.False(pane.TryCaptureLivePaintJson(out var json, out _, out _, out _));
        Assert.Equal("", json);
        Assert.Equal(before + 1, counters.Fallbacks(LiveStampFallbackReason.RenderStateUpdate));

        // The next paint after the failure posts the scrolled view.
        vt.ForceRenderStateUpdateFailure = false;
        Assert.True(pane.TryCaptureLivePaintJson(out json, out _, out var kind, out _));
        Assert.Equal(PaneVtPaintKind.Full, kind);
        Assert.Contains("scroll line", JsonText(json), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Json_clean_branch_posts_nothing_after_a_failed_render_state_update()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        using var vt = new GhosttyVtEngine(Cols, Rows, libraryPathOverride: LibPath);
        await using var pane = NewPane(vt);
        var counters = Assert.IsType<LiveStampCounters>(pane.LiveStampMeter);
        pane.FeedVtForTests("first line\r\n$ ");
        Assert.True(pane.TryCaptureLivePaintJson(out _, out _, out _, out _));
        pane.CommitPostedPaint();
        Assert.True(pane.TryCaptureLivePaintJson(out _, out _, out var kind, out _));
        Assert.Equal(PaneVtPaintKind.Clean, kind);

        // The live capture reads Clean. The update of the viewport capture
        // that follows fails.
        vt.FailRenderStateUpdateAfter = 1;
        var before = counters.Fallbacks(LiveStampFallbackReason.RenderStateUpdate);
        Assert.False(pane.TryCaptureLivePaintJson(out var json, out _, out _, out _));
        Assert.Equal("", json);
        Assert.Equal(0, vt.FailRenderStateUpdateAfter);
        Assert.Equal(before + 1, counters.Fallbacks(LiveStampFallbackReason.RenderStateUpdate));

        vt.FailRenderStateUpdateAfter = -1;
        Assert.True(pane.TryCaptureLivePaintJson(out json, out _, out kind, out _));
        Assert.Equal(PaneVtPaintKind.Clean, kind);
        Assert.Contains("first line", JsonText(json), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Json_partial_branch_posts_nothing_after_a_failed_render_state_update()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        using var vt = new GhosttyVtEngine(Cols, Rows, libraryPathOverride: LibPath);
        await using var pane = NewPane(vt);
        var counters = Assert.IsType<LiveStampCounters>(pane.LiveStampMeter);
        pane.FeedVtForTests("first line\r\n$ ");
        Assert.True(pane.TryCaptureLivePaintJson(out _, out _, out _, out _));
        pane.CommitPostedPaint();
        // One echoed key marks one row dirty.
        pane.FeedVtForTests("x");

        // The live capture reads Partial. The update of the full viewport
        // capture that follows fails.
        vt.FailRenderStateUpdateAfter = 1;
        var before = counters.Fallbacks(LiveStampFallbackReason.RenderStateUpdate);
        Assert.False(pane.TryCaptureLivePaintJson(out var json, out _, out _, out _));
        Assert.Equal("", json);
        Assert.Equal(0, vt.FailRenderStateUpdateAfter);
        Assert.Equal(before + 1, counters.Fallbacks(LiveStampFallbackReason.RenderStateUpdate));

        vt.FailRenderStateUpdateAfter = -1;
        Assert.True(pane.TryCaptureLivePaintJson(out json, out _, out var kind, out _));
        Assert.Equal(PaneVtPaintKind.Full, kind);
        Assert.Contains("$ x", JsonText(json), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Attach_capture_posts_no_blank_row_after_a_failed_cell_lookup()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        using var vt = new GhosttyVtEngine(Cols, Rows, libraryPathOverride: LibPath);
        await using var pane = NewPane(vt);
        var counters = Assert.IsType<LiveStampCounters>(pane.LiveStampMeter);
        pane.FeedVtForTests("first line\r\nsecond line");

        vt.ForceViewportCellLookupFailureRow = 1;
        // The lenient capture reads the failed lookup as a blank row.
        var lenient = vt.CapturePaintSnapshot();
        Assert.Equal("", RowText(lenient, 1).Trim());

        Assert.False(pane.TryCaptureAttachFrame(out var frame, out _, out var fault));
        Assert.Null(frame);
        Assert.True(fault);
        Assert.False(pane.TryCaptureAttachSnapshot(out var snapshot, out _));
        Assert.Null(snapshot);
        Assert.False(pane.TryCaptureSnapshotJson(out var json, out _));
        Assert.Equal("", json);
        Assert.Equal(3, counters.Fallbacks(LiveStampFallbackReason.RowGridRef));

        vt.ForceViewportCellLookupFailureRow = -1;
        Assert.True(pane.TryCaptureAttachFrame(out frame, out _, out fault));
        Assert.False(fault);
        Assert.Contains("second line", RowText(frame!, 1), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Attach_capture_posts_the_lenient_snapshot_at_the_failure_limit()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        using var vt = new GhosttyVtEngine(Cols, Rows, libraryPathOverride: LibPath);
        await using var pane = NewPane(vt);
        var counters = Assert.IsType<LiveStampCounters>(pane.LiveStampMeter);
        pane.FeedVtForTests("first line\r\nsecond line");

        vt.ForceRenderStateUpdateFailure = true;
        for (var attempt = 1; attempt < PaneRuntime.AttachCaptureFailureLimit; attempt++)
        {
            Assert.False(pane.TryCaptureAttachFrame(out _, out _, out var fault));
            Assert.True(fault);
        }

        Assert.Equal(0, counters.Fallbacks(LiveStampFallbackReason.AttachCaptureLimit));

        // The capture at the limit posts, so the attach is not stuck.
        Assert.True(pane.TryCaptureAttachFrame(out var frame, out _, out var limitFault));
        Assert.False(limitFault);
        Assert.Contains("second line", RowText(frame!, 1), StringComparison.Ordinal);
        Assert.Equal(1, counters.Fallbacks(LiveStampFallbackReason.AttachCaptureLimit));
        Assert.Equal(
            PaneRuntime.AttachCaptureFailureLimit,
            counters.Fallbacks(LiveStampFallbackReason.RenderStateUpdate));

        // The count starts again after the limit post.
        Assert.False(pane.TryCaptureAttachFrame(out _, out _, out var nextFault));
        Assert.True(nextFault);

        // A good capture resets the count.
        vt.ForceRenderStateUpdateFailure = false;
        Assert.True(pane.TryCaptureAttachFrame(out _, out _));
        vt.ForceRenderStateUpdateFailure = true;
        for (var attempt = 1; attempt < PaneRuntime.AttachCaptureFailureLimit; attempt++)
            Assert.False(pane.TryCaptureAttachFrame(out _, out _));
        Assert.Equal(1, counters.Fallbacks(LiveStampFallbackReason.AttachCaptureLimit));
    }

    [SkippableFact]
    public async Task Attach_with_one_failed_capture_gets_its_first_frame_on_the_retry()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        await using var server = await MuxSession.OpenAsync(LibPath);
        var vt = Assert.IsType<GhosttyVtEngine>(server.Runtime.VtEngine);
        var counters = Assert.IsType<LiveStampCounters>(server.Runtime.LiveStampMeter);
        server.Runtime.FeedVtForTests("first line\r\nsecond line");

        vt.ForceRenderStateUpdateFailure = true;
        await server.ObserveAsync();
        await server.DeliveredAsync();
        Assert.Empty(server.TakeRenders());
        // One capture on one route: the blit route failed closed, so the
        // JSON route did not capture again.
        Assert.Equal(1, counters.Fallbacks(LiveStampFallbackReason.RenderStateUpdate));

        vt.ForceRenderStateUpdateFailure = false;
        await server.SettleAsync(PaneRenderCoalescer.RenderTick);
        var renders = server.TakeRenders();
        var body = Assert.Single(renders);
        var cells = AssertCells(body);
        Assert.True(cells.Full);
        Assert.Contains("second line", CellsRowText(cells, 1), StringComparison.Ordinal);
        Assert.Equal(0, counters.Fallbacks(LiveStampFallbackReason.AttachCaptureLimit));
    }

    [SkippableFact]
    public async Task Attach_with_a_failed_cell_lookup_posts_no_blank_row()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        await using var server = await MuxSession.OpenAsync(LibPath);
        var vt = Assert.IsType<GhosttyVtEngine>(server.Runtime.VtEngine);
        var counters = Assert.IsType<LiveStampCounters>(server.Runtime.LiveStampMeter);
        server.Runtime.FeedVtForTests("first line\r\nsecond line");

        vt.ForceViewportCellLookupFailureRow = 1;
        await server.ObserveAsync();
        await server.DeliveredAsync();
        Assert.Empty(server.TakeRenders());

        Assert.Equal(1, counters.Fallbacks(LiveStampFallbackReason.RowGridRef));

        // The queued retry fails again while the lookup fails. Nothing posts.
        await server.SettleAsync(TimeSpan.Zero);
        Assert.Empty(server.TakeRenders());
        Assert.Equal(2, counters.Fallbacks(LiveStampFallbackReason.RowGridRef));

        vt.ForceViewportCellLookupFailureRow = -1;
        await server.SettleAsync(PaneRenderCoalescer.RenderTick);
        var body = Assert.Single(server.TakeRenders());
        var cells = AssertCells(body);
        Assert.True(cells.Full);
        Assert.Contains("second line", CellsRowText(cells, 1), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Attach_limit_snapshot_is_replaced_when_the_fault_stops_on_an_idle_pane()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        await using var server = await MuxSession.OpenAsync(LibPath);
        var vt = Assert.IsType<GhosttyVtEngine>(server.Runtime.VtEngine);
        var counters = Assert.IsType<LiveStampCounters>(server.Runtime.LiveStampMeter);
        server.Runtime.FeedVtForTests("first line\r\nsecond line");

        vt.ForceViewportCellLookupFailureRow = 1;
        await server.ObserveAsync();
        await server.DeliveredAsync();
        var renders = server.TakeRenders();
        for (var tick = 0; renders.Count == 0 && tick < 2 * PaneRuntime.AttachCaptureFailureLimit; tick++)
        {
            await server.SettleAsync(PaneRenderCoalescer.RenderTick);
            renders = server.TakeRenders();
        }

        // The capture at the limit posts the lenient snapshot: row 1 is blank.
        var limit = AssertCells(Assert.Single(renders));
        Assert.True(limit.Full);
        Assert.Equal(1, counters.Fallbacks(LiveStampFallbackReason.AttachCaptureLimit));
        Assert.Contains("first line", CellsRowText(limit, 0), StringComparison.Ordinal);
        Assert.DoesNotContain("second", RowTextOrEmpty(limit, 1), StringComparison.Ordinal);
        Assert.True(server.Runtime.AttachFrameNeedsRepaint);

        // The fault stops. The pane is idle, so no output paints. The retry
        // that the limit post queued replaces the blank row.
        vt.ForceViewportCellLookupFailureRow = -1;
        await server.SettleAsync(PaneRenderCoalescer.RenderTick);
        var good = AssertCells(Assert.Single(server.TakeRenders()));
        Assert.Contains("second line", CellsRowText(good, 1), StringComparison.Ordinal);
        Assert.False(server.Runtime.AttachFrameNeedsRepaint);

        // The good frame queues no more retries.
        await server.SettleAsync(PaneRenderCoalescer.RenderTick);
        Assert.Empty(server.TakeRenders());
    }

    [SkippableFact]
    public async Task Popup_with_one_failed_capture_gets_its_first_frame_on_the_retry()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        await using var server = await MuxSession.OpenAsync(LibPath);
        PaneRuntime? popup = null;
        server.Factory.OnNextCreate = runtime =>
        {
            popup = Assert.IsType<PaneRuntime>(runtime);
            Assert.IsType<GhosttyVtEngine>(popup.VtEngine).ForceRenderStateUpdateFailure = true;
        };

        // A popup render goes only to a live attach.
        await server.ObserveAsync();
        await server.OpenPopupAsync();
        Assert.NotNull(popup);
        var vt = Assert.IsType<GhosttyVtEngine>(popup!.VtEngine);
        await server.DeliveredAsync();
        Assert.Empty(server.TakePopupSnapshots());
        var counters = Assert.IsType<LiveStampCounters>(popup.LiveStampMeter);
        Assert.Equal(1, counters.Fallbacks(LiveStampFallbackReason.RenderStateUpdate));

        // The pane is idle: no output asks for a paint. The retry posts.
        popup.FeedVtForTests("popup text");
        vt.ForceRenderStateUpdateFailure = false;
        await server.SettleAsync(PaneRenderCoalescer.RenderTick);
        var snapshots = server.TakePopupSnapshots();
        Assert.NotEmpty(snapshots);
        Assert.Contains("popup text", PopupSnapshotText(snapshots), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Popup_limit_snapshot_is_replaced_when_the_fault_stops()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        await using var server = await MuxSession.OpenAsync(LibPath);
        PaneRuntime? popup = null;
        server.Factory.OnNextCreate = runtime =>
        {
            popup = Assert.IsType<PaneRuntime>(runtime);
            var engine = Assert.IsType<GhosttyVtEngine>(popup.VtEngine);
            popup.FeedVtForTests("first line\r\nsecond line");
            engine.ForceViewportCellLookupFailureRow = 1;
        };

        // A popup render goes only to a live attach.
        await server.ObserveAsync();
        await server.OpenPopupAsync();
        Assert.NotNull(popup);
        var vt = Assert.IsType<GhosttyVtEngine>(popup!.VtEngine);
        await server.DeliveredAsync();
        var snapshots = server.TakePopupSnapshots();
        for (var tick = 0; snapshots.Count == 0 && tick < 2 * PaneRuntime.AttachCaptureFailureLimit; tick++)
        {
            await server.SettleAsync(PaneRenderCoalescer.RenderTick);
            snapshots = server.TakePopupSnapshots();
        }

        // The capture at the limit posts the lenient snapshot: row 1 is blank.
        Assert.NotEmpty(snapshots);
        var lenient = PopupSnapshotText(snapshots);
        Assert.Contains("first line", lenient, StringComparison.Ordinal);
        Assert.DoesNotContain("second line", lenient, StringComparison.Ordinal);
        Assert.True(popup.AttachFrameNeedsRepaint);

        // The fault stops and the popup is idle. The retry posts row 1.
        vt.ForceViewportCellLookupFailureRow = -1;
        await server.SettleAsync(PaneRenderCoalescer.RenderTick);
        snapshots = server.TakePopupSnapshots();
        Assert.NotEmpty(snapshots);
        Assert.Contains("second line", PopupSnapshotText(snapshots), StringComparison.Ordinal);
        Assert.False(popup.AttachFrameNeedsRepaint);
    }


    /// <summary>The text of every row of popup snapshot payloads, one line for each row.</summary>
    private static string PopupSnapshotText(IEnumerable<JsonElement> payloads)
    {
        var text = new StringBuilder();
        foreach (var payload in payloads)
        {
            foreach (var row in payload.GetProperty("snapshot").GetProperty("cells").EnumerateArray())
            {
                foreach (var cell in row.EnumerateArray())
                {
                    if (cell.TryGetProperty("text", out var t))
                        text.Append(t.GetString());
                }

                text.Append('\n');
            }
        }

        return text.ToString();
    }

    private static string RowTextOrEmpty(TerminalRenderCellsPayload cells, int row) =>
        cells.Rows is null ? "" : string.Concat(cells.Rows.Where(r => r.I == row).Select(r => r.T));

    /// <summary>The rows of a JSON snapshot as text, one line for each row.</summary>
    private static string JsonText(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var text = new StringBuilder();
        foreach (var row in doc.RootElement.GetProperty("cells").EnumerateArray())
        {
            foreach (var cell in row.EnumerateArray())
            {
                if (cell.TryGetProperty("text", out var t))
                    text.Append(t.GetString());
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    private static TerminalRenderCellsPayload AssertCells(JsonElement payload)
    {
        Assert.Equal(TerminalRenderCellsPayload.KindCells, payload.GetProperty("kind").GetString());
        return payload.Deserialize(ProtocolJsonContext.Default.TerminalRenderCellsPayload)!;
    }

    private static string CellsRowText(TerminalRenderCellsPayload cells, int row)
    {
        Assert.NotNull(cells.Rows);
        var match = cells.Rows!.Where(r => r.I == row).ToList();
        Assert.NotEmpty(match);
        return string.Concat(match.Select(r => r.T));
    }

    private static string RowText(VtStructuredSnapshot snap, int row)
    {
        var text = new StringBuilder();
        foreach (var cell in snap.Cells![row])
        {
            if (!cell.IsContinuation)
                text.Append(cell.Text);
        }

        return text.ToString();
    }

    private static string RowText(VtFrame frame, int row)
    {
        var text = new StringBuilder();
        for (var c = 0; c < frame.Cols; c++)
            text.Append(frame.CellAt(row, c).Text);
        return text.ToString();
    }

    private static PaneRuntime NewPane(GhosttyVtEngine vt) =>
        new(
            new PaneSpawnOptions
            {
                Id = PaneId.New(),
                Cwd = Path.GetTempPath(),
                Command = "/bin/true",
                Args = [],
                Cols = Cols,
                Rows = Rows,
            },
            spawnProcess: static (_, _) => throw new InvalidOperationException("not started"),
            vt: vt);

    /// <summary>
    /// A live <see cref="ControlPlaneService"/> with a /bin/cat pane and one
    /// live subscriber that has not yet attached. The clock moves only in
    /// <see cref="SettleAsync"/>.
    /// </summary>
    private sealed class MuxSession : IAsyncDisposable
    {
        private readonly string _dir;
        private readonly RuntimeStatePaths _paths;
        private readonly RenderSink _sink = new("snapshot-fail-closed");
        private readonly ManualTimeProvider _time;
        private int _mark;
        private int _popupMark;

        private MuxSession(string dir, RuntimeStatePaths paths, ControlPlaneService cp, ManualTimeProvider time)
        {
            _dir = dir;
            _paths = paths;
            _time = time;
            Cp = cp;
        }

        public ControlPlaneService Cp { get; }

        public HookFactory Factory { get; private init; } = null!;

        public EventSubscriptionHub Hub { get; private init; } = null!;

        public PaneRuntime Runtime { get; private set; } = null!;

        public string PaneId { get; private set; } = "";

        public string SubscriptionId { get; private set; } = "";

        public static async Task<MuxSession> OpenAsync(string? lib)
        {
            var dir = Path.Combine(Path.GetTempPath(), "hypa-snapfail-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var paths = new RuntimeStatePaths { StateDirectory = dir };
            try
            {
                return await OpenInAsync(dir, paths, lib);
            }
            catch
            {
                SqliteTestCleanup.ReleaseAndDelete(dir, paths.DatabasePath);
                throw;
            }
        }

        private static async Task<MuxSession> OpenInAsync(string dir, RuntimeStatePaths paths, string? lib)
        {
            Assert.True((await new SqliteRuntimeSchemaMigrator(paths).MigrateAsync()).IsOk);
            var state = new AppState(SessionId.New("snapfail"));
            state.UpdateSession(s => s with { Name = "snapfail", LifecycleState = SessionLifecycle.Ready });
            state.CreateWorkspace(dir, label: "default");
            var store = new SqliteRuntimeSessionStore(paths);
            Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
            var journal = new FileRuntimeEventJournal(paths, new SqliteJournalManifestStore(paths), state.SessionId.Value);
            Assert.True((await journal.RecoverAsync()).IsOk);
            var factory = new HookFactory(new PaneRuntimeFactory(
                new PaneIntelligencePipeline(),
                ptyFactory: TestPaneFactories.ProcessIo(),
                vtEngineFactory: new VtEngineFactory(new VtProviderSelection { LibraryPathOverride = lib })));
            var time = new ManualTimeProvider();
            var hub = new EventSubscriptionHub();
            var cp = new ControlPlaneService(
                state,
                factory,
                new PaneIntelligencePipeline(),
                new HeuristicAgentDetector(),
                store: store,
                journal: journal,
                subscriptions: hub,
                timeProvider: time);
            var session = new MuxSession(dir, paths, cp, time) { Factory = factory, Hub = hub };
            try
            {
                var sub = await cp.DispatchAsync(
                    ProtocolMethods.EventsSubscribe,
                    Json(new JsonObject
                    {
                        ["from_seq"] = 0,
                        ["types"] = new JsonArray("output", "render"),
                        ["live"] = true,
                    }),
                    session._sink,
                    CancellationToken.None);
                session.SubscriptionId = sub.GetProperty("subscription_id").GetString()!;
                await cp.CompleteEventsSubscribeAsync(session.SubscriptionId, [], session._sink, CancellationToken.None);
                var create = await cp.DispatchAsync(
                    ProtocolMethods.PaneCreate,
                    Json(new JsonObject { ["command"] = "/bin/cat", ["cwd"] = dir }),
                    CancellationToken.None);
                session.PaneId = create.GetProperty("pane_id").GetString()!;
                session.Runtime = Assert.IsType<PaneRuntime>(cp.PeekRuntime(session.PaneId));
                await session.Runtime.ResizeAsync(Cols, Rows, CancellationToken.None);
                await cp.DispatchAsync(
                    ProtocolMethods.TerminalVisibleSet,
                    Json(new JsonObject
                    {
                        ["subscription_id"] = session.SubscriptionId,
                        ["pane_ids"] = new JsonArray(session.PaneId),
                    }),
                    session._sink,
                    CancellationToken.None);
                return session;
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
        }

        /// <summary>Attach the subscriber to the pane. The attach posts the first frame.</summary>
        public async Task ObserveAsync()
        {
            await DeliveredAsync();
            _mark = _sink.Lines.Count;
            await Cp.DispatchAsync(
                ProtocolMethods.TerminalObserve,
                Json(new JsonObject { ["pane_id"] = PaneId, ["subscription_id"] = SubscriptionId }),
                _sink,
                CancellationToken.None);
        }

        /// <summary>Open a /bin/cat popup. The open posts the first popup frame.</summary>
        public async Task OpenPopupAsync()
        {
            await DeliveredAsync();
            _popupMark = _sink.Lines.Count;
            await Cp.DispatchAsync(
                ProtocolMethods.PopupOpen,
                Json(new JsonObject
                {
                    ["command"] = "/bin/cat",
                    ["cwd"] = _dir,
                    ["area_cols"] = 80,
                    ["area_rows"] = 24,
                }),
                _sink,
                CancellationToken.None);
        }

        /// <summary>Every popup snapshot terminal.render payload posted since the last call, in order.</summary>
        public List<JsonElement> TakePopupSnapshots()
        {
            var lines = _sink.Lines;
            var payloads = new List<JsonElement>();
            for (; _popupMark < lines.Count; _popupMark++)
            {
                using var doc = JsonDocument.Parse(lines[_popupMark]);
                if (!doc.RootElement.TryGetProperty("params", out var parms)
                    || !parms.TryGetProperty("type", out var type)
                    || type.GetString() != ProtocolEventTypes.TerminalRender
                    || !parms.TryGetProperty("payload", out var payload)
                    || !payload.TryGetProperty("target", out var target)
                    || target.GetString() != ProtocolEventTypes.TerminalRenderTargetPopup
                    || !payload.TryGetProperty("kind", out var kind)
                    || kind.GetString() != "snapshot")
                {
                    continue;
                }

                payloads.Add(payload.Clone());
            }

            return payloads;
        }

        /// <summary>
        /// Move the clock. A coalescer tick that falls inside the gap runs and
        /// its paint is delivered before this returns.
        /// </summary>
        public async Task SettleAsync(TimeSpan gap)
        {
            _time.Advance(gap);
            await Poll.GuardAsync(Cp.CoalescerTickFlushedForTestsAsync(), "coalescer tick");
            await DeliveredAsync();
        }

        public async Task DeliveredAsync()
        {
            await Cp.WaitOutputDeliveredForTestsAsync(PaneId);
            await Poll.GuardAsync(Cp.CoalescerTickFlushedForTestsAsync(), "coalescer tick");
            await Cp.WaitOutputDeliveredForTestsAsync(PaneId);
            // A popup posts to the hub with no pane writer. Wait for the fan-out.
            await Hub.WhenQueuedDeliveredAsync().AsTask().WaitAsync(Poll.HangGuard);
        }

        /// <summary>Every terminal.render payload of the pane posted since the last call, in order.</summary>
        public List<JsonElement> TakeRenders()
        {
            var lines = _sink.Lines;
            var payloads = new List<JsonElement>();
            for (; _mark < lines.Count; _mark++)
            {
                using var doc = JsonDocument.Parse(lines[_mark]);
                if (!doc.RootElement.TryGetProperty("params", out var parms)
                    || !parms.TryGetProperty("type", out var type)
                    || type.GetString() != ProtocolEventTypes.TerminalRender
                    || !parms.TryGetProperty("payload", out var payload))
                {
                    continue;
                }

                payloads.Add(payload.Clone());
            }

            return payloads;
        }

        public async ValueTask DisposeAsync()
        {
            await Cp.ShutdownAsync(CancellationToken.None);
            SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);
        }

        private static JsonElement Json(JsonObject obj) =>
            JsonDocument.Parse(obj.ToJsonString()).RootElement;
    }

    /// <summary>
    /// The production factory. <see cref="OnNextCreate"/> runs one time on
    /// the next runtime that the factory creates, before the mux uses it.
    /// </summary>
    private sealed class HookFactory(IPaneRuntimeFactory inner) : IPaneRuntimeFactory
    {
        public Action<IPaneRuntime>? OnNextCreate { get; set; }

        public string PtyProvider => inner.PtyProvider;

        public bool PtyInteractive => inner.PtyInteractive;

        public IPaneRuntime Create(PaneSpawnOptions options)
        {
            var runtime = inner.Create(options);
            var hook = OnNextCreate;
            OnNextCreate = null;
            hook?.Invoke(runtime);
            return runtime;
        }
    }

    private sealed class RenderSink(string id) : IClientConnection
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];

        public string ConnectionId { get; } = id;

        public bool UsesWriterLanes => true;

        public int MaxLineBytes { get; set; } = 4 * 1_048_576;

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_gate)
                    return [.. _lines];
            }
        }

        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default)
        {
            Add(jsonLine);
            return Task.CompletedTask;
        }

        public WriterLaneResult TryEnqueueRender(string jsonLine) => Add(jsonLine);

        public WriterLaneResult TryEnqueueRender(byte[] utf8Json) => Add(Encoding.UTF8.GetString(utf8Json));

        public WriterLaneResult EnqueueOrderedRender(string jsonLine) => Add(jsonLine);

        public WriterLaneResult EnqueueOrderedRender(byte[] utf8Json) => Add(Encoding.UTF8.GetString(utf8Json));

        private WriterLaneResult Add(string json)
        {
            lock (_gate)
                _lines.Add(json);
            return WriterLaneResult.Ok;
        }
    }
}
