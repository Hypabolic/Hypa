using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Application cursor and bracketed paste travel on snapshot modes and on
/// every cells body. The host TTY stays in normal cursor mode.
/// </summary>
[Collection("GhosttyPtyTests")]
public sealed class CursorPasteModeTests
{
    private readonly string? _lib = GhosttyTestRequire.TryResolveNativeLibraryPath();

    [SkippableFact]
    public void Snapshot_modes_follow_application_cursor_and_bracketed_paste()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        using var vt = new GhosttyVtEngine(8, 3, libraryPathOverride: _lib);
        vt.Feed("\u001b[?1h"u8);
        Assert.True(vt.CaptureSnapshot().Modes.ApplicationCursor);
        var on = Pack(vt.CaptureSnapshot());
        Assert.Contains("\"application_cursor\":true", on, StringComparison.Ordinal);

        vt.Feed("\u001b[?1l"u8);
        Assert.False(vt.CaptureSnapshot().Modes.ApplicationCursor);
        var off = Pack(vt.CaptureSnapshot());
        Assert.DoesNotContain("application_cursor", off, StringComparison.Ordinal);

        vt.Feed("\u001b[?1h\u001b[?2004h"u8);
        var both = vt.CaptureSnapshot();
        Assert.True(both.Modes.ApplicationCursor);
        Assert.True(both.Modes.BracketedPaste);
        var json = JsonSerializer.Serialize(both, VtSnapshotWireJsonContext.Default.VtStructuredSnapshot);
        var parsed = AttachSnapshotPacker.ParseSnapshotJson("pane_1", json);
        Assert.True(parsed.Frame.Modes.ApplicationCursor);
        Assert.True(parsed.Frame.Modes.BracketedPaste);
        var packed = Pack(both);
        Assert.Contains("\"application_cursor\":true", packed, StringComparison.Ordinal);
        Assert.Contains("\"bracketed_paste\":true", packed, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("\u001b[?1h", "\u001b[?1l", "application_cursor")]
    [InlineData("\u001b[?2004h", "\u001b[?2004l", "bracketed_paste")]
    public async Task Mode_only_delta_states_the_current_flag(string on, string off, string field)
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        var mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests(on);
        await session.Cp.FlushCoalescedPaintForTestsAsync(session.PaneId);
        var delta = Assert.Single(session.CellsSince(mark));
        Assert.False(delta.GetProperty("full").GetBoolean());
        Assert.Equal(0, delta.GetProperty("rows").GetArrayLength());
        Assert.True(delta.GetProperty(field).GetBoolean());

        mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests(off);
        await session.Cp.FlushCoalescedPaintForTestsAsync(session.PaneId);
        var cleared = Assert.Single(session.CellsSince(mark));
        Assert.False(cleared.TryGetProperty(field, out var flag) && flag.ValueKind == JsonValueKind.True);
    }

    [SkippableTheory]
    [InlineData("\u001b[?1h", "application_cursor")]
    [InlineData("\u001b[?2004h", "bracketed_paste")]
    public async Task Text_and_mode_share_one_delta(string on, string field)
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        var mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("Q" + on);
        await session.Cp.FlushCoalescedPaintForTestsAsync(session.PaneId);
        var delta = Assert.Single(session.CellsSince(mark));
        Assert.False(delta.GetProperty("full").GetBoolean());
        Assert.True(delta.GetProperty("rows").GetArrayLength() > 0);
        Assert.True(delta.GetProperty(field).GetBoolean());
        Assert.Contains("Q", delta.GetRawText(), StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("\u001b[?1h", "application_cursor")]
    [InlineData("\u001b[?2004h", "bracketed_paste")]
    public async Task Dropped_mode_next_body_is_full_reanchor(string on, string field)
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        var mark = session.Sink.Count;
        session.Sink.MaxLineBytes = 256;
        session.Runtime.PublishFedOutputForTests(on);
        await session.Cp.FlushCoalescedPaintForTestsAsync(session.PaneId);
        Assert.Empty(session.CellsSince(mark));

        session.Sink.MaxLineBytes = 1_048_576;
        session.Runtime.PublishFedOutputForTests(on);
        await session.Cp.FlushCoalescedPaintForTestsAsync(session.PaneId);
        var body = Assert.Single(session.CellsSince(mark));
        Assert.True(body.GetProperty("full").GetBoolean());
        Assert.True(body.GetProperty("reanchor").GetBoolean());
        Assert.True(body.GetProperty(field).GetBoolean());
    }

    [SkippableTheory]
    [InlineData("\u001b[?1h", "application_cursor")]
    [InlineData("\u001b[?2004h", "bracketed_paste")]
    public async Task Dropped_text_then_mode_sends_the_current_grid(string on, string field)
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        await session.Runtime.ResizeAsync(220, 3, CancellationToken.None);
        var mark = session.Sink.Count;
        session.Sink.MaxLineBytes = 256;
        session.Runtime.PublishFedOutputForTests("GRIDMARK");
        await session.Cp.FlushCoalescedPaintForTestsAsync(session.PaneId);
        Assert.Empty(session.CellsSince(mark));

        session.Sink.MaxLineBytes = 1_048_576;
        session.Runtime.PublishFedOutputForTests(on);
        await session.Cp.FlushCoalescedPaintForTestsAsync(session.PaneId);
        var body = Assert.Single(session.CellsSince(mark));
        Assert.True(body.GetProperty("full").GetBoolean());
        Assert.True(body.GetProperty("reanchor").GetBoolean());
        Assert.True(body.GetProperty(field).GetBoolean());
        Assert.Contains("GRIDMARK", body.GetRawText(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Reattach_first_render_carries_application_cursor()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        await session.Cp.DispatchAsync(
            ProtocolMethods.EventsUnsubscribe,
            Json(new JsonObject { ["subscription_id"] = session.SubId }),
            session.Sink,
            CancellationToken.None);
        session.Runtime.FeedVtForTests("\u001b[?1h");

        var again = await session.AttachAgainAsync();
        var first = again.RendersSince(again.ObserveMark).FirstOrDefault();
        Assert.True(first.ValueKind == JsonValueKind.Object, again.DumpSince(again.ObserveMark));
        Assert.True(HasFlag(first, "application_cursor"), first.GetRawText());
    }

    [SkippableFact]
    public async Task Reanchor_marked_after_the_first_sample_encodes_a_full_body()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        session.Runtime.PublishFedOutputForTests("Z");
        await session.Cp.FlushCoalescedPaintForTestsAsync(session.PaneId);

        session.Cp.BeforeCleanReanchorDecisionForTests = paneId =>
            session.Cp.MarkLiveObserversReanchorForTests(paneId);
        var mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("\u001b[?1h");
        await session.Cp.FlushCoalescedPaintForTestsAsync(session.PaneId);
        var body = Assert.Single(session.CellsSince(mark));
        Assert.True(body.GetProperty("full").GetBoolean());
        Assert.True(body.GetProperty("reanchor").GetBoolean());
        Assert.True(body.GetProperty("application_cursor").GetBoolean());
        Assert.Contains("Z", body.GetRawText(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Reanchor_mark_during_admission_survives_the_delta_commit()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        session.Cp.BeforeLiveCellsAdmitForTests = paneId =>
        {
            session.Cp.BeforeLiveCellsAdmitForTests = null;
            session.Cp.MarkLiveObserversReanchorForTests(paneId);
        };
        var mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("Q");
        await session.Cp.FlushCoalescedPaintForTestsAsync(session.PaneId);
        var delta = Assert.Single(session.CellsSince(mark));
        Assert.False(delta.GetProperty("full").GetBoolean());
        Assert.Contains("Q", delta.GetRawText(), StringComparison.Ordinal);
        Assert.True(session.Cp.ObserverReanchorPendingForTests(session.SubId, session.PaneId));

        mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("\u001b[?1h");
        await session.Cp.FlushCoalescedPaintForTestsAsync(session.PaneId);
        var body = Assert.Single(session.CellsSince(mark));
        Assert.True(body.GetProperty("full").GetBoolean());
        Assert.True(body.GetProperty("reanchor").GetBoolean());
        Assert.True(body.GetProperty("application_cursor").GetBoolean());
        Assert.Contains("Q", body.GetRawText(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Pending_observer_receives_a_full_frame_when_another_has_a_mode_change()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        session.Runtime.PublishFedOutputForTests("Z");
        await session.Cp.FlushCoalescedPaintForTestsAsync(session.PaneId);
        var other = await session.AddObserverAsync("cursor-mode-b");

        other.Sink.FailNextRenders = 1;
        var held = session.Sink.Count;
        var otherHeld = other.Sink.Count;
        session.Runtime.PublishFedOutputForTests("\u001b[?1h");
        await session.Cp.FlushCoalescedPaintForTestsAsync(session.PaneId);
        var admitted = Assert.Single(session.CellsSince(held));
        Assert.False(admitted.GetProperty("full").GetBoolean());
        Assert.True(admitted.GetProperty("application_cursor").GetBoolean());
        Assert.Empty(CellsOn(other.Sink, otherHeld));

        session.Cp.MarkObserverReanchorForTests(session.SubId, session.PaneId);
        held = session.Sink.Count;
        otherHeld = other.Sink.Count;
        session.Runtime.PublishFedOutputForTests("\u001b[?1h");
        await session.Cp.FlushCoalescedPaintForTestsAsync(session.PaneId);

        var full = Assert.Single(session.CellsSince(held));
        Assert.True(full.GetProperty("full").GetBoolean());
        Assert.True(full.GetProperty("reanchor").GetBoolean());
        Assert.True(full.GetProperty("application_cursor").GetBoolean());
        Assert.Contains("Z", full.GetRawText(), StringComparison.Ordinal);

        var delta = Assert.Single(CellsOn(other.Sink, otherHeld));
        Assert.False(delta.GetProperty("full").GetBoolean());
        Assert.True(delta.GetProperty("application_cursor").GetBoolean());
    }

    [SkippableFact]
    public async Task Sync_pair_emits_no_mode_only_body()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync();
        var mark = session.Sink.Count;
        session.Runtime.PublishFedOutputForTests("\u001b[?2026h");
        await session.Cp.FlushCoalescedPaintForTestsAsync(session.PaneId);
        session.Runtime.PublishFedOutputForTests("\u001b[?2026l");
        await session.Cp.FlushCoalescedPaintForTestsAsync(session.PaneId);
        Assert.Empty(session.CellsSince(mark));
    }

    private async Task<Session> OpenAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hypa-cursor-mode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var paths = new RuntimeStatePaths { StateDirectory = dir };
        var migrator = new SqliteRuntimeSchemaMigrator(paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        var state = new AppState(SessionId.New("cursor-mode"));
        state.UpdateSession(s => s with { Name = "cursor-mode", LifecycleState = SessionLifecycle.Ready });
        state.CreateWorkspace(dir, label: "default");
        var store = new SqliteRuntimeSessionStore(paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
        var manifests = new SqliteJournalManifestStore(paths);
        var journal = new FileRuntimeEventJournal(paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);
        var factory = new PaneRuntimeFactory(
            new PaneIntelligencePipeline(),
            ptyFactory: TestPaneFactories.ProcessIo(),
            vtEngineFactory: new VtEngineFactory(new VtProviderSelection
            {
                LibraryPathOverride = _lib,
            }));
        var cp = new ControlPlaneService(
            state,
            factory,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: new EventSubscriptionHub());
        var sink = new LaneSink("cursor-mode");
        var session = new Session(dir, paths, cp, sink);
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
                sink,
                CancellationToken.None);
            session.SubId = sub.GetProperty("subscription_id").GetString()!;
            await cp.CompleteEventsSubscribeAsync(session.SubId, [], sink, CancellationToken.None);
            var create = await cp.DispatchAsync(
                ProtocolMethods.PaneCreate,
                Json(new JsonObject
                {
                    ["command"] = "/bin/cat",
                    ["cwd"] = dir,
                }),
                CancellationToken.None);
            session.PaneId = create.GetProperty("pane_id").GetString()!;
            session.Runtime = Assert.IsType<PaneRuntime>(cp.PeekRuntime(session.PaneId));
            await cp.DispatchAsync(
                ProtocolMethods.TerminalVisibleSet,
                Json(new JsonObject
                {
                    ["subscription_id"] = session.SubId,
                    ["pane_ids"] = new JsonArray(session.PaneId),
                }),
                sink,
                CancellationToken.None);
            await cp.DispatchAsync(
                ProtocolMethods.TerminalObserve,
                Json(new JsonObject
                {
                    ["pane_id"] = session.PaneId,
                    ["subscription_id"] = session.SubId,
                }),
                sink,
                CancellationToken.None);
            Assert.NotEmpty(session.CellsSince(0));
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private static string Pack(VtStructuredSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, VtSnapshotWireJsonContext.Default.VtStructuredSnapshot);
        var parsed = AttachSnapshotPacker.ParseSnapshotJson("pane_1", json);
        var parts = AttachSnapshotPacker.Pack(
            "pane_1",
            parsed,
            new DefaultEventPayloadRedactor(),
            generation: 1);
        return string.Concat(parts.Select(part => Encoding.UTF8.GetString(part)));
    }

    private static bool HasFlag(JsonElement payload, string name)
    {
        if (payload.TryGetProperty(name, out var direct) && direct.ValueKind == JsonValueKind.True)
            return true;
        return payload.TryGetProperty("modes", out var modes)
            && modes.ValueKind == JsonValueKind.Object
            && modes.TryGetProperty(name, out var nested)
            && nested.ValueKind == JsonValueKind.True;
    }

    private static JsonElement Json(JsonObject obj) =>
        JsonDocument.Parse(obj.ToJsonString()).RootElement;

    private static List<JsonElement> CellsOn(LaneSink sink, int lineMark) =>
        PayloadsOn(sink, lineMark, TerminalRenderCellsPayload.KindCells);

    private static List<JsonElement> PayloadsOn(LaneSink sink, int lineMark, string? kind)
    {
        var list = new List<JsonElement>();
        foreach (var line in sink.Lines.Skip(lineMark))
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("params", out var parms))
                continue;
            if (!parms.TryGetProperty("type", out var type)
                || type.GetString() != ProtocolEventTypes.TerminalRender)
                continue;
            if (!parms.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object)
                continue;
            if (kind is not null
                && (!payload.TryGetProperty("kind", out var payloadKind)
                    || payloadKind.GetString() != kind))
                continue;
            list.Add(payload.Clone());
        }

        return list;
    }

    private sealed record Observer(LaneSink Sink, string SubId);

    private sealed class Session : IAsyncDisposable
    {
        private readonly string _dir;
        private readonly RuntimeStatePaths _paths;

        public Session(string dir, RuntimeStatePaths paths, ControlPlaneService cp, LaneSink sink)
        {
            _dir = dir;
            _paths = paths;
            Cp = cp;
            Sink = sink;
        }

        public ControlPlaneService Cp { get; }

        public LaneSink Sink { get; private set; }

        public PaneRuntime Runtime { get; set; } = null!;

        public string PaneId { get; set; } = "";

        public string SubId { get; set; } = "";

        public int ObserveMark { get; private set; }

        public List<JsonElement> RendersSince(int lineMark) =>
            PayloadsSince(lineMark, kind: null);

        public string DumpSince(int lineMark) =>
            string.Join('\n', Sink.Lines.Skip(lineMark));

        public List<JsonElement> CellsSince(int lineMark) =>
            CellsOn(Sink, lineMark);

        public async Task<Observer> AddObserverAsync(string connectionId)
        {
            var sink = new LaneSink(connectionId);
            var sub = await Cp.DispatchAsync(
                ProtocolMethods.EventsSubscribe,
                Json(new JsonObject
                {
                    ["from_seq"] = 0,
                    ["types"] = new JsonArray("output", "render"),
                    ["live"] = true,
                }),
                sink,
                CancellationToken.None);
            var subId = sub.GetProperty("subscription_id").GetString()!;
            await Cp.CompleteEventsSubscribeAsync(subId, [], sink, CancellationToken.None);
            await Cp.DispatchAsync(
                ProtocolMethods.TerminalVisibleSet,
                Json(new JsonObject
                {
                    ["subscription_id"] = subId,
                    ["pane_ids"] = new JsonArray(PaneId),
                }),
                sink,
                CancellationToken.None);
            await Cp.DispatchAsync(
                ProtocolMethods.TerminalObserve,
                Json(new JsonObject
                {
                    ["pane_id"] = PaneId,
                    ["subscription_id"] = subId,
                }),
                sink,
                CancellationToken.None);
            return new Observer(sink, subId);
        }

        private List<JsonElement> PayloadsSince(int lineMark, string? kind) =>
            PayloadsOn(Sink, lineMark, kind);

        public async Task<Session> AttachAgainAsync()
        {
            var sink = new LaneSink("cursor-mode-2");
            Sink = sink;
            var sub = await Cp.DispatchAsync(
                ProtocolMethods.EventsSubscribe,
                Json(new JsonObject
                {
                    ["from_seq"] = 0,
                    ["types"] = new JsonArray("output", "render"),
                    ["live"] = true,
                }),
                sink,
                CancellationToken.None);
            var subId = sub.GetProperty("subscription_id").GetString()!;
            await Cp.CompleteEventsSubscribeAsync(subId, [], sink, CancellationToken.None);
            await Cp.DispatchAsync(
                ProtocolMethods.TerminalVisibleSet,
                Json(new JsonObject
                {
                    ["subscription_id"] = subId,
                    ["pane_ids"] = new JsonArray(PaneId),
                }),
                sink,
                CancellationToken.None);
            ObserveMark = Sink.Count;
            await Cp.DispatchAsync(
                ProtocolMethods.TerminalObserve,
                Json(new JsonObject
                {
                    ["pane_id"] = PaneId,
                    ["subscription_id"] = subId,
                }),
                sink,
                CancellationToken.None);
            SubId = subId;
            return this;
        }

        public async ValueTask DisposeAsync()
        {
            await Cp.ShutdownAsync(CancellationToken.None);
            SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);
        }
    }

    private sealed class LaneSink(string id) : IClientConnection
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];

        public string ConnectionId { get; } = id;

        public bool UsesWriterLanes => true;

        public int MaxLineBytes { get; set; } = 1_048_576;

        public int FailNextRenders { get; set; }

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_gate)
                    return [.. _lines];
            }
        }

        public int Count
        {
            get
            {
                lock (_gate)
                    return _lines.Count;
            }
        }

        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default)
        {
            Note(jsonLine);
            return Task.CompletedTask;
        }

        public WriterLaneResult TryEnqueueRender(string jsonLine) => Admit(jsonLine);

        public WriterLaneResult TryEnqueueRender(byte[] utf8Json) =>
            Admit(Encoding.UTF8.GetString(utf8Json));

        public WriterLaneResult EnqueueOrderedRender(string jsonLine) => Admit(jsonLine);

        public WriterLaneResult EnqueueOrderedRender(byte[] utf8Json) =>
            Admit(Encoding.UTF8.GetString(utf8Json));

        private WriterLaneResult Admit(string json)
        {
            lock (_gate)
            {
                if (FailNextRenders > 0)
                {
                    FailNextRenders--;
                    return WriterLaneResult.TooLarge;
                }

                _lines.Add(json);
                return WriterLaneResult.Ok;
            }
        }

        private void Note(string json)
        {
            lock (_gate)
                _lines.Add(json);
        }
    }
}
