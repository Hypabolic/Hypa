using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Ghostty VT identity through hidden, tiled, and overlay placements.
/// This fixture uses ProcessIO plus Ghostty VT. It does not start
// / hypa-pty-host.
/// the pane VT. The client then composes one host frame.
/// </summary>
[Collection("GhosttyPtyTests")]
public sealed class HiddenPaneLifecycleGhosttyTests : IDisposable
{
    private const int SeedLineCount = 80;
    private const string EarlyScrollLine = "SCROLL-OLD-000";
    private const string HiddenMark = "HIDDEN-MARK";
    private const string HiddenSeedCommand =
        "i=0; while [ \"$i\" -lt 80 ]; do printf 'SCROLL-OLD-%03d\\n' \"$i\"; i=$((i+1)); done; printf 'HIDDEN-MARK\\n'; exec cat";

    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;
    private readonly string? _lib;

    public HiddenPaneLifecycleGhosttyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-life-gt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
        _lib = GhosttyTestRequire.TryResolveNativeLibraryPath();
    }

    public void Dispose() => SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);

    [SkippableFact]
    public async Task Same_pane_vt_content_survives_hidden_tiled_overlay_close()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        var factory = new PaneRuntimeFactory(
            new PaneIntelligencePipeline(),
            ptyFactory: TestPaneFactories.ProcessIo(),
            vtEngineFactory: new VtEngineFactory(new VtProviderSelection
            {
                LibraryPathOverride = _lib,
            }));
        var (cp, state, journal) = await NewAsync(factory);
        try
        {
            var (hidden, tiled, owner, leaseId) = await SeedOccupiedAsync(cp);
            var runtime = Assert.IsType<PaneRuntime>(cp.PeekRuntime(hidden));
            Assert.IsType<GhosttyVtEngine>(runtime.VtEngine);
            var seedCols = state.GetPane(new PaneId(hidden))!.Cols;
            var seedRows = state.GetPane(new PaneId(hidden))!.Rows;
            Assert.True(seedCols > 0);
            Assert.True(seedRows > 0);
            Assert.True(SeedLineCount > seedRows);

            await WaitSeedOverflowAsync(cp, hidden);
            var visibleBeforeMove = await ReadPaneAsync(cp, hidden);
            Assert.DoesNotContain(EarlyScrollLine, visibleBeforeMove, StringComparison.Ordinal);
            Assert.Contains(EarlyScrollLine, await ReadRecentAsync(cp, hidden), StringComparison.Ordinal);
            await Task.Delay(30);
            var outputRange = await journal.ReadRangeAsync(
                0, new HashSet<EventClass> { EventClass.Output }, 50, CancellationToken.None);
            Assert.True(outputRange.IsOk);
            Assert.DoesNotContain(outputRange.Value, r => r.Type == ProtocolEventTypes.TerminalOutput);

            var shown = await ShowTiledAsync(cp, hidden, leaseId, tiled, owner);
            Assert.Equal("tiled", shown.GetProperty("placement").GetString());
            Assert.Same(runtime, cp.PeekRuntime(hidden));
            Assert.True(runtime.IsAlive);
            await AssertHistoryContainsAsync(cp, hidden, EarlyScrollLine);
            Assert.True(state.GetPane(new PaneId(hidden))!.Cols > 0);

            await HideAsync(cp, hidden, leaseId, owner);
            Assert.Same(runtime, cp.PeekRuntime(hidden));
            Assert.Equal(PanePlacement.Hidden, state.GetPane(new PaneId(hidden))!.Placement);
            await AssertHistoryContainsAsync(cp, hidden, EarlyScrollLine);

            var overlay = await ShowOverlayAsync(cp, hidden, leaseId, owner, 80, 24);
            var geometry = PopupGeometry.TryResolve(80, 24)!;
            Assert.Equal(geometry.InnerCols, overlay.GetProperty("cols").GetInt32());
            Assert.Equal(geometry.InnerRows, overlay.GetProperty("rows").GetInt32());
            Assert.Same(runtime, cp.PeekRuntime(hidden));
            await AssertHistoryContainsAsync(cp, hidden, EarlyScrollLine);
            await AssertHistoryContainsAsync(cp, hidden, HiddenMark);

            await HideOverlayAsync(
                cp, hidden, leaseId, owner, overlay.GetProperty("overlay_generation").GetInt64());
            Assert.Same(runtime, cp.PeekRuntime(hidden));
            Assert.True(runtime.IsAlive);
            await AssertHistoryContainsAsync(cp, hidden, EarlyScrollLine);
            await AssertHistoryContainsAsync(cp, hidden, HiddenMark);

            await cp.DispatchAsync(
                ProtocolMethods.PaneClose,
                Json(new JsonObject { ["pane_id"] = hidden }),
                CancellationToken.None);
            Assert.Null(cp.PeekRuntime(hidden));
            Assert.Null(state.GetPane(new PaneId(hidden)));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [SkippableFact]
    public async Task Hidden_keeps_positive_geometry_after_neighbour_resize()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        var factory = new PaneRuntimeFactory(
            new PaneIntelligencePipeline(),
            ptyFactory: TestPaneFactories.ProcessIo(),
            vtEngineFactory: new VtEngineFactory(new VtProviderSelection
            {
                LibraryPathOverride = _lib,
            }));
        var (cp, state, _) = await NewAsync(factory);
        try
        {
            var (hidden, tiled, owner, _) = await SeedOccupiedAsync(cp);
            await WaitSeedOverflowAsync(cp, hidden);
            var hiddenBefore = state.GetPane(new PaneId(hidden))!;
            Assert.True(hiddenBefore.Cols > 0);
            Assert.True(hiddenBefore.Rows > 0);
            Assert.DoesNotContain(
                EarlyScrollLine, await ReadPaneAsync(cp, hidden), StringComparison.Ordinal);

            var claim = await cp.DispatchAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                Json(new JsonObject { ["pane_id"] = tiled, ["scope"] = "resize" }),
                owner,
                CancellationToken.None);
            await cp.DispatchAsync(
                ProtocolMethods.PaneResize,
                Json(new JsonObject
                {
                    ["pane_id"] = tiled,
                    ["lease_id"] = claim.GetProperty("lease_id").GetString(),
                    ["cols"] = 90,
                    ["rows"] = 28,
                }),
                owner,
                CancellationToken.None);

            var hiddenAfter = state.GetPane(new PaneId(hidden))!;
            Assert.Equal(PanePlacement.Hidden, hiddenAfter.Placement);
            Assert.Equal(hiddenBefore.Cols, hiddenAfter.Cols);
            Assert.Equal(hiddenBefore.Rows, hiddenAfter.Rows);
            Assert.False(LayoutTreeOperations.ContainsPane(
                state.GetTab(hiddenAfter.TabId)!.LayoutRoot, new PaneId(hidden)));
            var runtime = Assert.IsType<PaneRuntime>(cp.PeekRuntime(hidden));
            Assert.Same(runtime, cp.PeekRuntime(hidden));
            await AssertHistoryContainsAsync(cp, hidden, EarlyScrollLine);
            await AssertHistoryContainsAsync(cp, hidden, HiddenMark);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private async Task<(ControlPlaneService Cp, AppState State, FileRuntimeEventJournal Journal)>
        NewAsync(IPaneRuntimeFactory factory)
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        var state = new AppState(SessionId.New("pane-life-gt"));
        state.UpdateSession(s => s with { Name = "pane-life-gt", LifecycleState = SessionLifecycle.Ready });
        var store = new SqliteRuntimeSessionStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
        var manifests = new SqliteJournalManifestStore(_paths);
        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);
        var cp = new ControlPlaneService(
            state,
            factory,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: new EventSubscriptionHub());
        return (cp, state, journal);
    }

    private static async Task<(string Hidden, string Tiled, FakeConnection Owner, string LeaseId)>
        SeedOccupiedAsync(ControlPlaneService cp)
    {
        var ws = await cp.DispatchAsync(
            ProtocolMethods.WorkspaceCreate,
            Json(new JsonObject
            {
                ["cwd"] = Path.GetTempPath(),
                ["create_pane"] = true,
                ["command"] = "/bin/sh",
                ["args"] = new JsonArray("-c", "printf 'TILED-MARK\\n'; exec cat"),
            }),
            CancellationToken.None);
        var tiled = ws.GetProperty("pane").GetProperty("pane_id").GetString()!;
        var hiddenPane = await cp.DispatchAsync(
            ProtocolMethods.PaneCreate,
            Json(new JsonObject
            {
                ["workspace_id"] = ws.GetProperty("workspace_id").GetString(),
                ["command"] = "/bin/sh",
                ["args"] = new JsonArray("-c", HiddenSeedCommand),
                ["placement"] = "hidden",
            }),
            CancellationToken.None);
        var hidden = hiddenPane.GetProperty("pane_id").GetString()!;
        var owner = new FakeConnection("conn_gt");
        await cp.DispatchAsync(
            ProtocolMethods.EventsSubscribe,
            Json(new JsonObject { ["from_seq"] = 0, ["live"] = true }),
            owner,
            CancellationToken.None);
        var claim = await cp.DispatchAsync(
            ProtocolMethods.RuntimeLeaseClaim,
            Json(new JsonObject { ["pane_id"] = hidden, ["scope"] = "input" }),
            owner,
            CancellationToken.None);
        await cp.DispatchAsync(
            ProtocolMethods.UiClientMode,
            Json(new JsonObject { ["client_mode"] = "terminal" }),
            owner,
            CancellationToken.None);
        return (hidden, tiled, owner, claim.GetProperty("lease_id").GetString()!);
    }

    private static Task<JsonElement> ShowTiledAsync(
        ControlPlaneService cp,
        string paneId,
        string leaseId,
        string target,
        FakeConnection owner) =>
        cp.DispatchAsync(
            ProtocolMethods.PaneShow,
            Json(new JsonObject
            {
                ["pane_id"] = paneId,
                ["lease_id"] = leaseId,
                ["mode"] = "tiled",
                ["direction"] = "right",
                ["target_pane_id"] = target,
            }),
            owner,
            CancellationToken.None);

    private static Task<JsonElement> ShowOverlayAsync(
        ControlPlaneService cp,
        string paneId,
        string leaseId,
        FakeConnection owner,
        int areaCols,
        int areaRows) =>
        cp.DispatchAsync(
            ProtocolMethods.PaneShow,
            Json(new JsonObject
            {
                ["pane_id"] = paneId,
                ["lease_id"] = leaseId,
                ["mode"] = "overlay",
                ["attach_client_id"] = owner.ConnectionId,
                ["area_cols"] = areaCols,
                ["area_rows"] = areaRows,
            }),
            owner,
            CancellationToken.None);

    private static Task<JsonElement> HideAsync(
        ControlPlaneService cp,
        string paneId,
        string leaseId,
        FakeConnection owner) =>
        cp.DispatchAsync(
            ProtocolMethods.PaneHide,
            Json(new JsonObject { ["pane_id"] = paneId, ["lease_id"] = leaseId }),
            owner,
            CancellationToken.None);

    private static Task<JsonElement> HideOverlayAsync(
        ControlPlaneService cp,
        string paneId,
        string leaseId,
        FakeConnection owner,
        long generation) =>
        cp.DispatchAsync(
            ProtocolMethods.PaneHide,
            Json(new JsonObject
            {
                ["pane_id"] = paneId,
                ["lease_id"] = leaseId,
                ["attach_client_id"] = owner.ConnectionId,
                ["overlay_generation"] = generation,
            }),
            owner,
            CancellationToken.None);

    private static async Task<string> ReadPaneAsync(ControlPlaneService cp, string paneId)
    {
        var read = await cp.DispatchAsync(
            ProtocolMethods.PaneRead,
            Json(new JsonObject
            {
                ["pane_id"] = paneId,
                ["source"] = ProtocolPaneReadSources.Visible,
            }),
            CancellationToken.None);
        return read.GetProperty("text").GetString() ?? "";
    }

    private static async Task<string> ReadRecentAsync(ControlPlaneService cp, string paneId)
    {
        var read = await cp.DispatchAsync(
            ProtocolMethods.PaneRead,
            Json(new JsonObject
            {
                ["pane_id"] = paneId,
                ["source"] = ProtocolPaneReadSources.Recent,
                ["lines"] = 800,
            }),
            CancellationToken.None);
        return read.GetProperty("text").GetString() ?? "";
    }

    private static async Task<string> ReadRecentUnwrappedAsync(ControlPlaneService cp, string paneId)
    {
        var read = await cp.DispatchAsync(
            ProtocolMethods.PaneRead,
            Json(new JsonObject
            {
                ["pane_id"] = paneId,
                ["source"] = ProtocolPaneReadSources.RecentUnwrapped,
                ["lines"] = 800,
            }),
            CancellationToken.None);
        return read.GetProperty("text").GetString() ?? "";
    }

    private static async Task AssertHistoryContainsAsync(
        ControlPlaneService cp,
        string paneId,
        string marker)
    {
        var recent = await ReadRecentAsync(cp, paneId);
        var unwrapped = await ReadRecentUnwrappedAsync(cp, paneId);
        var compactRecent = CompactHistory(recent);
        var compactUnwrapped = CompactHistory(unwrapped);
        Assert.True(
            recent.Contains(marker, StringComparison.Ordinal)
            || unwrapped.Contains(marker, StringComparison.Ordinal)
            || compactRecent.Contains(marker, StringComparison.Ordinal)
            || compactUnwrapped.Contains(marker, StringComparison.Ordinal),
            "recent/scrollback missing " + marker);
    }

    private static string CompactHistory(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch))
                sb.Append(ch);
        }

        return sb.ToString();
    }

    private static JsonElement Json(JsonObject obj) =>
        JsonDocument.Parse(obj.ToJsonString()).RootElement.Clone();

    private static async Task WaitUntilAsync(Func<Task<bool>> pred, int timeoutMs = 10_000)
    {
        var start = DateTime.UtcNow;
        while ((DateTime.UtcNow - start).TotalMilliseconds < timeoutMs)
        {
            if (await pred())
                return;
            await Task.Delay(20);
        }

        Assert.Fail("condition not met within " + timeoutMs + "ms");
    }

    private static Task WaitSeedOverflowAsync(ControlPlaneService cp, string paneId) =>
        WaitUntilAsync(async () =>
        {
            var recent = await ReadRecentAsync(cp, paneId);
            var visible = await ReadPaneAsync(cp, paneId);
            return recent.Contains(EarlyScrollLine, StringComparison.Ordinal)
                && recent.Contains(HiddenMark, StringComparison.Ordinal)
                && !visible.Contains(EarlyScrollLine, StringComparison.Ordinal);
        });

    private static async Task<string> WaitJournalTerminalOutputAsync(
        FileRuntimeEventJournal journal,
        long fromSeqExclusive,
        string paneId,
        string marker)
    {
        var start = DateTime.UtcNow;
        var decoded = "";
        while (DateTime.UtcNow - start < TimeSpan.FromSeconds(10))
        {
            var range = await journal.ReadRangeAsync(
                fromSeqExclusive,
                new HashSet<EventClass> { EventClass.Output },
                budget: 80,
                CancellationToken.None);
            Assert.True(range.IsOk);
            decoded = DecodeTerminalOutput(range.Value, paneId);
            if (decoded.Contains(marker, StringComparison.Ordinal))
                return decoded;
            await Task.Delay(20);
        }

        throw new TimeoutException(
            "journal has no terminal.output record with marker " + marker);
    }

    private static string DecodeTerminalOutput(
        IReadOnlyList<RuntimeEventRecord> records,
        string paneId)
    {
        var sb = new StringBuilder();
        foreach (var rec in records)
        {
            if (rec.Type != ProtocolEventTypes.TerminalOutput)
                continue;
            using var doc = JsonDocument.Parse(rec.PayloadJson);
            var payload = doc.RootElement;
            if (!payload.TryGetProperty("pane_id", out var id)
                || id.GetString() != paneId)
                continue;
            var dataB64 = payload.TryGetProperty("data", out var data)
                ? data.GetString()
                : null;
            if (string.IsNullOrEmpty(dataB64))
                continue;
            sb.Append(Encoding.UTF8.GetString(Convert.FromBase64String(dataB64)));
        }

        return sb.ToString();
    }

    private sealed class FakeConnection(string id) : IClientConnection
    {
        public string ConnectionId { get; } = id;
        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default) => Task.CompletedTask;
    }
}
