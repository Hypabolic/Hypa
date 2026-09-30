using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.ControlPlane;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class HostThemeRpcTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public HostThemeRpcTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-host-theme-t-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose() => SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);

    [Fact]
    public async Task Set_merges_and_applies_to_existing_panes_without_restart()
    {
        var factory = new ThemeRecordingFactory();
        var (cp, sink, subId) = await NewSubscribedAsync(factory, ["output", "render"]);
        try
        {
            var ws = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["create_pane"] = true,
                    ["command"] = "/bin/echo",
                }.ToJsonString()).RootElement,
                CancellationToken.None);
            var paneId = ws.GetProperty("pane").GetProperty("pane_id").GetString()!;
            var runtime = factory.Created.Single(r => r.Id.Value == paneId);
            Assert.Contains("apply", runtime.Order);
            Assert.Contains("start", runtime.Order);
            Assert.True(runtime.Order.IndexOf("apply") < runtime.Order.IndexOf("start"));
            var starts = runtime.StartCount;
            _ = await cp.DispatchAsync(
                ProtocolMethods.TerminalObserve,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["subscription_id"] = subId,
                }.ToJsonString()).RootElement,
                sink,
                CancellationToken.None);
            runtime.Order.Clear();
            sink.Lines.Clear();

            var set = await cp.DispatchAsync(
                ProtocolMethods.ClientHostThemeSet,
                JsonDocument.Parse("""{"fg":{"r":1,"g":2,"b":3},"bg":{"r":4,"g":5,"b":6},"appearance":"dark"}""").RootElement,
                CancellationToken.None);
            Assert.Equal(1, set.GetProperty("fg").GetProperty("r").GetInt32());
            Assert.Equal(4, set.GetProperty("bg").GetProperty("r").GetInt32());
            Assert.Equal("dark", set.GetProperty("appearance").GetString());
            Assert.Equal(starts, runtime.StartCount);
            Assert.Equal(["apply"], runtime.Order);
            Assert.Equal(new HostRgb(1, 2, 3), runtime.LastTheme.Foreground);
            Assert.Equal(new HostRgb(4, 5, 6), runtime.LastTheme.Background);
            Assert.True(cp.TryPeekRenderPending(paneId, out var generation));
            Assert.True(generation >= 0);
            await cp.FlushCoalescedPaintForTestsAsync(paneId);
            // Continuity skips coalesced paint when the pane has no live
            // attach observer. Host-theme apply must still not emit
            Assert.DoesNotContain(
                sink.Parsed,
                ev => ev.TryGetProperty("params", out var p)
                    && p.TryGetProperty("type", out var type)
                    && type.GetString() == ProtocolEventTypes.TerminalOutput);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Set_merges_palette_and_applies_to_existing_panes_without_restart()
    {
        var factory = new ThemeRecordingFactory();
        var (cp, _) = await NewPlaneAsync(factory);
        try
        {
            var ws = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["create_pane"] = true,
                    ["command"] = "/bin/echo",
                }.ToJsonString()).RootElement,
                CancellationToken.None);
            var paneId = ws.GetProperty("pane").GetProperty("pane_id").GetString()!;
            var runtime = factory.Created.Single(r => r.Id.Value == paneId);
            var starts = runtime.StartCount;
            runtime.Order.Clear();

            var set = await cp.DispatchAsync(
                ProtocolMethods.ClientHostThemeSet,
                JsonDocument.Parse(
                    """{"palette":[{"i":0,"r":1,"g":2,"b":3},{"i":255,"r":4,"g":5,"b":6},{"i":0,"r":9,"g":8,"b":7}]}""").RootElement,
                CancellationToken.None);
            var palette = set.GetProperty("palette");
            Assert.Equal(2, palette.GetArrayLength());
            Assert.Equal(0, palette[0].GetProperty("i").GetInt32());
            Assert.Equal(9, palette[0].GetProperty("r").GetInt32());
            Assert.Equal(255, palette[1].GetProperty("i").GetInt32());
            Assert.Equal(starts, runtime.StartCount);
            Assert.Equal(["apply"], runtime.Order);
            Assert.Equal(new HostRgb(9, 8, 7), runtime.LastTheme.Palette[0]);
            Assert.Equal(new HostRgb(4, 5, 6), runtime.LastTheme.Palette[255]);
            Assert.Null(runtime.LastTheme.Palette[1]);
            Assert.Equal(HostPalette.Size * 2, HostThemeSetParams.MaxPaletteEntries);

            var merged = await cp.DispatchAsync(
                ProtocolMethods.ClientHostThemeSet,
                JsonDocument.Parse("""{"palette":[{"i":1,"r":10,"g":20,"b":30}]}""").RootElement,
                CancellationToken.None);
            Assert.Equal(3, merged.GetProperty("palette").GetArrayLength());
            Assert.Equal(new HostRgb(9, 8, 7), runtime.LastTheme.Palette[0]);
            Assert.Equal(new HostRgb(10, 20, 30), runtime.LastTheme.Palette[1]);
            Assert.Equal(new HostRgb(4, 5, 6), runtime.LastTheme.Palette[255]);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Set_rejects_palette_longer_than_bound()
    {
        var factory = new ThemeRecordingFactory();
        var (cp, _) = await NewPlaneAsync(factory);
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"palette\":[");
            for (var i = 0; i <= HostThemeSetParams.MaxPaletteEntries; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append("{\"i\":0,\"r\":1,\"g\":2,\"b\":3}");
            }

            sb.Append("]}");
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.ClientHostThemeSet,
                    JsonDocument.Parse(sb.ToString()).RootElement,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidParams, ex.Code);
            Assert.Contains(
                HostThemeSetParams.MaxPaletteEntries.ToString(),
                ex.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Set_skips_disposed_pane_and_applies_to_remaining()
    {
        var factory = new ThemeRecordingFactory();
        var (cp, _) = await NewPlaneAsync(factory);
        try
        {
            var ws = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["create_pane"] = true,
                    ["command"] = "/bin/echo",
                }.ToJsonString()).RootElement,
                CancellationToken.None);
            var workspaceId = ws.GetProperty("workspace_id").GetString()!;
            _ = await cp.DispatchAsync(
                ProtocolMethods.PaneCreate,
                JsonDocument.Parse(new JsonObject
                {
                    ["workspace_id"] = workspaceId,
                    ["command"] = "/bin/echo",
                    ["placement"] = "hidden",
                }.ToJsonString()).RootElement,
                CancellationToken.None);
            Assert.Equal(2, factory.Created.Count);
            var first = factory.Created[0];
            var second = factory.Created[1];
            first.Order.Clear();
            second.Order.Clear();
            first.ThrowDisposedOnApply = true;

            var set = await cp.DispatchAsync(
                ProtocolMethods.ClientHostThemeSet,
                JsonDocument.Parse("""{"bg":{"r":9,"g":8,"b":7}}""").RootElement,
                CancellationToken.None);
            Assert.Equal(9, set.GetProperty("bg").GetProperty("r").GetInt32());
            Assert.Empty(first.Order);
            Assert.Equal(["apply"], second.Order);
            Assert.Equal(new HostRgb(9, 8, 7), second.LastTheme.Background);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Set_retries_after_failed_fanout()
    {
        var factory = new ThemeRecordingFactory();
        var (cp, _) = await NewPlaneAsync(factory);
        try
        {
            _ = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["create_pane"] = true,
                    ["command"] = "/bin/echo",
                }.ToJsonString()).RootElement,
                CancellationToken.None);
            var runtime = factory.Created.Single();
            runtime.Order.Clear();
            runtime.ThrowOnApply = new InvalidOperationException("apply failed");

            var first = await cp.DispatchAsync(
                ProtocolMethods.ClientHostThemeSet,
                JsonDocument.Parse("""{"bg":{"r":1,"g":2,"b":3}}""").RootElement,
                CancellationToken.None);
            Assert.Equal(1, first.GetProperty("bg").GetProperty("r").GetInt32());
            Assert.Empty(runtime.Order);

            runtime.ThrowOnApply = null;
            var retry = await cp.DispatchAsync(
                ProtocolMethods.ClientHostThemeSet,
                JsonDocument.Parse("""{"bg":{"r":1,"g":2,"b":3}}""").RootElement,
                CancellationToken.None);
            Assert.Equal(1, retry.GetProperty("bg").GetProperty("r").GetInt32());
            Assert.Equal(["apply"], runtime.Order);
            Assert.Equal(new HostRgb(1, 2, 3), runtime.LastTheme.Background);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Incremental_channel_merges_without_wipe()
    {
        var factory = new ThemeRecordingFactory();
        var (cp, _) = await NewPlaneAsync(factory);
        try
        {
            _ = await cp.DispatchAsync(
                ProtocolMethods.ClientHostThemeSet,
                JsonDocument.Parse("""{"fg":{"r":9,"g":8,"b":7}}""").RootElement,
                CancellationToken.None);
            var second = await cp.DispatchAsync(
                ProtocolMethods.ClientHostThemeSet,
                JsonDocument.Parse("""{"bg":{"r":1,"g":2,"b":3}}""").RootElement,
                CancellationToken.None);
            Assert.Equal(9, second.GetProperty("fg").GetProperty("r").GetInt32());
            Assert.Equal(1, second.GetProperty("bg").GetProperty("r").GetInt32());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Empty_object_is_noop()
    {
        var factory = new ThemeRecordingFactory();
        var (cp, _) = await NewPlaneAsync(factory);
        try
        {
            var empty = await cp.DispatchAsync(
                ProtocolMethods.ClientHostThemeSet,
                JsonDocument.Parse("{}").RootElement,
                CancellationToken.None);
            Assert.False(empty.TryGetProperty("fg", out _));
            Assert.Empty(factory.Created);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Set_applies_to_pending_replacement_before_swap()
    {
        var factory = new ThemeRecordingFactory();
        var (cp, _) = await NewPlaneAsync(factory);
        Task<JsonElement>? startTask = null;
        try
        {
            var ws = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["create_pane"] = true,
                    ["command"] = "/bin/echo",
                }.ToJsonString()).RootElement,
                CancellationToken.None);
            var paneId = ws.GetProperty("pane").GetProperty("pane_id").GetString()!;
            var outgoing = factory.Created.Single(r => r.Id.Value == paneId);
            outgoing.Order.Clear();
            factory.HoldNextStart = true;
            startTask = cp.DispatchAsync(
                ProtocolMethods.AgentStart,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["command"] = "/bin/true",
                }.ToJsonString()).RootElement,
                CancellationToken.None);
            var replacement = await factory.WaitHeldAsync()
                .WaitAsync(TimeSpan.FromSeconds(10));

            var set = await cp.DispatchAsync(
                ProtocolMethods.ClientHostThemeSet,
                JsonDocument.Parse("""{"fg":{"r":9,"g":8,"b":7},"bg":{"r":1,"g":2,"b":3}}""").RootElement,
                CancellationToken.None);
            Assert.Equal(1, set.GetProperty("bg").GetProperty("r").GetInt32());
            Assert.Equal(new HostRgb(9, 8, 7), replacement.LastTheme.Foreground);
            Assert.Equal(new HostRgb(1, 2, 3), replacement.LastTheme.Background);
            Assert.Equal(new HostRgb(1, 2, 3), outgoing.LastTheme.Background);

            factory.ReleaseHeld();
            _ = await startTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            factory.ReleaseHeld();
            if (startTask is not null)
            {
                try
                {
                    await startTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // shutdown still runs
                }
            }

            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Later_set_wins_when_earlier_apply_finishes_second()
    {
        var factory = new ThemeRecordingFactory();
        var (cp, _) = await NewPlaneAsync(factory);
        try
        {
            _ = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["create_pane"] = true,
                    ["command"] = "/bin/echo",
                }.ToJsonString()).RootElement,
                CancellationToken.None);
            var runtime = factory.Created.Single();
            runtime.Order.Clear();
            runtime.ArmApplyHold();

            var first = Task.Run(() => cp.DispatchAsync(
                ProtocolMethods.ClientHostThemeSet,
                JsonDocument.Parse("""{"bg":{"r":1,"g":2,"b":3}}""").RootElement,
                CancellationToken.None));
            await runtime.WaitApplyHeldAsync().WaitAsync(TimeSpan.FromSeconds(10));

            var second = await cp.DispatchAsync(
                ProtocolMethods.ClientHostThemeSet,
                JsonDocument.Parse("""{"bg":{"r":9,"g":8,"b":7}}""").RootElement,
                CancellationToken.None);
            Assert.Equal(9, second.GetProperty("bg").GetProperty("r").GetInt32());
            Assert.Equal(new HostRgb(9, 8, 7), runtime.LastTheme.Background);

            runtime.ReleaseApplyHold();
            _ = await first.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(new HostRgb(9, 8, 7), runtime.LastTheme.Background);
            Assert.True(runtime.AppliedVersion >= 2);
        }
        finally
        {
            factory.Created.FirstOrDefault()?.ReleaseApplyHold();
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Create_keeps_newer_theme_when_pre_start_apply_is_stale()
    {
        var factory = new ThemeRecordingFactory { HoldNextApply = true };
        var (cp, _) = await NewPlaneAsync(factory);
        Task<JsonElement>? createTask = null;
        try
        {
            await cp.DispatchAsync(
                ProtocolMethods.ClientHostThemeSet,
                JsonDocument.Parse("""{"bg":{"r":1,"g":2,"b":3}}""").RootElement,
                CancellationToken.None);
            createTask = Task.Run(() => cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["create_pane"] = true,
                    ["command"] = "/bin/echo",
                }.ToJsonString()).RootElement,
                CancellationToken.None));
            var runtime = await factory.WaitApplyHeldAsync()
                .WaitAsync(TimeSpan.FromSeconds(10));

            var set = await cp.DispatchAsync(
                ProtocolMethods.ClientHostThemeSet,
                JsonDocument.Parse("""{"bg":{"r":9,"g":8,"b":7}}""").RootElement,
                CancellationToken.None);
            Assert.Equal(9, set.GetProperty("bg").GetProperty("r").GetInt32());
            Assert.Equal(new HostRgb(9, 8, 7), runtime.LastTheme.Background);

            factory.ReleaseApply();
            _ = await createTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(new HostRgb(9, 8, 7), runtime.LastTheme.Background);
            Assert.Contains("start", runtime.Order);
            Assert.True(runtime.Order.IndexOf("apply") < runtime.Order.IndexOf("start"));
        }
        finally
        {
            factory.ReleaseApply();
            if (createTask is not null)
            {
                try
                {
                    await createTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // shutdown still runs
                }
            }

            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Replacement_keeps_newer_theme_when_pre_start_apply_is_stale()
    {
        var factory = new ThemeRecordingFactory();
        var (cp, _) = await NewPlaneAsync(factory);
        Task<JsonElement>? startTask = null;
        try
        {
            var ws = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["create_pane"] = true,
                    ["command"] = "/bin/echo",
                }.ToJsonString()).RootElement,
                CancellationToken.None);
            var paneId = ws.GetProperty("pane").GetProperty("pane_id").GetString()!;
            await cp.DispatchAsync(
                ProtocolMethods.ClientHostThemeSet,
                JsonDocument.Parse("""{"bg":{"r":1,"g":2,"b":3}}""").RootElement,
                CancellationToken.None);
            factory.HoldNextApply = true;
            startTask = Task.Run(() => cp.DispatchAsync(
                ProtocolMethods.AgentStart,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["command"] = "/bin/true",
                }.ToJsonString()).RootElement,
                CancellationToken.None));
            var replacement = await factory.WaitApplyHeldAsync()
                .WaitAsync(TimeSpan.FromSeconds(10));

            var set = await cp.DispatchAsync(
                ProtocolMethods.ClientHostThemeSet,
                JsonDocument.Parse("""{"bg":{"r":9,"g":8,"b":7}}""").RootElement,
                CancellationToken.None);
            Assert.Equal(9, set.GetProperty("bg").GetProperty("r").GetInt32());
            Assert.Equal(new HostRgb(9, 8, 7), replacement.LastTheme.Background);

            factory.ReleaseApply();
            _ = await startTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(new HostRgb(9, 8, 7), replacement.LastTheme.Background);
            Assert.Contains("start", replacement.Order);
            Assert.True(replacement.Order.IndexOf("apply") < replacement.Order.IndexOf("start"));
        }
        finally
        {
            factory.ReleaseApply();
            if (startTask is not null)
            {
                try
                {
                    await startTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // shutdown still runs
                }
            }

            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Popup_keeps_newer_theme_when_update_arrives_during_pre_start_apply()
    {
        var factory = new ThemeRecordingFactory();
        var (cp, _) = await NewPlaneAsync(factory);
        Task<JsonElement>? popupTask = null;
        try
        {
            _ = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["create_pane"] = true,
                    ["command"] = "/bin/echo",
                }.ToJsonString()).RootElement,
                CancellationToken.None);
            await cp.DispatchAsync(
                ProtocolMethods.ClientHostThemeSet,
                JsonDocument.Parse("""{"bg":{"r":1,"g":2,"b":3}}""").RootElement,
                CancellationToken.None);
            factory.HoldNextApply = true;
            popupTask = Task.Run(() => cp.DispatchAsync(
                ProtocolMethods.PopupOpen,
                JsonDocument.Parse(new JsonObject
                {
                    ["command"] = "/bin/echo",
                    ["area_cols"] = 80,
                    ["area_rows"] = 24,
                }.ToJsonString()).RootElement,
                CancellationToken.None));
            var popup = await factory.WaitApplyHeldAsync()
                .WaitAsync(TimeSpan.FromSeconds(10));

            var set = await cp.DispatchAsync(
                ProtocolMethods.ClientHostThemeSet,
                JsonDocument.Parse("""{"bg":{"r":9,"g":8,"b":7}}""").RootElement,
                CancellationToken.None);
            Assert.Equal(9, set.GetProperty("bg").GetProperty("r").GetInt32());
            Assert.Equal(new HostRgb(9, 8, 7), popup.LastTheme.Background);

            factory.ReleaseApply();
            _ = await popupTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(new HostRgb(9, 8, 7), popup.LastTheme.Background);
            Assert.Contains("start", popup.Order);
            Assert.True(popup.Order.IndexOf("apply") < popup.Order.IndexOf("start"));
        }
        finally
        {
            factory.ReleaseApply();
            if (popupTask is not null)
            {
                try
                {
                    await popupTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // shutdown still runs
                }
            }

            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private async Task<(ControlPlaneService Cp, CapturingConnection Sink, string SubId)> NewSubscribedAsync(
        IPaneRuntimeFactory factory,
        IReadOnlyList<string> types)
    {
        var (cp, _) = await NewPlaneAsync(factory);
        var sink = new CapturingConnection("c_theme");
        var arr = new JsonArray();
        foreach (var type in types)
            arr.Add(type);
        var sub = await cp.DispatchAsync(
            ProtocolMethods.EventsSubscribe,
            JsonDocument.Parse(new JsonObject
            {
                ["from_seq"] = 0,
                ["types"] = arr,
                ["live"] = true,
            }.ToJsonString()).RootElement,
            sink,
            CancellationToken.None);
        var subId = sub.GetProperty("subscription_id").GetString()!;
        await cp.CompleteEventsSubscribeAsync(
            subId,
            [],
            sink,
            CancellationToken.None);
        return (cp, sink, subId);
    }

    private async Task<(ControlPlaneService Cp, EventSubscriptionHub Hub)> NewPlaneAsync(IPaneRuntimeFactory factory)
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        var state = new AppState(SessionId.New("host-theme"));
        state.UpdateSession(s => s with { Name = "host-theme", LifecycleState = SessionLifecycle.Ready });
        var store = new SqliteRuntimeSessionStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
        var manifests = new SqliteJournalManifestStore(_paths);
        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);
        var hub = new EventSubscriptionHub();
        var intel = new PaneIntelligencePipeline();
        var cp = new ControlPlaneService(
            state,
            factory,
            intel,
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: hub);
        return (cp, hub);
    }

    internal sealed class ThemeRecordingFactory : IPaneRuntimeFactory
    {
        public List<ThemeRecordingRuntime> Created { get; } = [];
        public PaneSpawnOptions? LastOptions { get; private set; }
        private bool _holdNextStart;
        private bool _holdNextApply;
        private TaskCompletionSource<ThemeRecordingRuntime>? _held;
        private TaskCompletionSource? _startHold;
        private TaskCompletionSource<ThemeRecordingRuntime>? _applyHeld;
        private TaskCompletionSource? _applyHold;

        public bool HoldNextStart
        {
            get => _holdNextStart;
            set
            {
                _holdNextStart = value;
                if (!value)
                    return;
                _startHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public bool HoldNextApply
        {
            get => _holdNextApply;
            set
            {
                _holdNextApply = value;
                if (!value)
                    return;
                _applyHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _applyHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public Task<ThemeRecordingRuntime> WaitHeldAsync() =>
            _held?.Task ?? throw new InvalidOperationException("HoldNextStart was not set");

        public Task<ThemeRecordingRuntime> WaitApplyHeldAsync() =>
            _applyHeld?.Task ?? throw new InvalidOperationException("HoldNextApply was not set");

        public void ReleaseHeld() => _startHold?.TrySetResult();

        public void ReleaseApply() => _applyHold?.TrySetResult();

        public IPaneRuntime Create(PaneSpawnOptions options)
        {
            LastOptions = options;
            var runtime = new ThemeRecordingRuntime(options);
            if (_holdNextStart)
            {
                _holdNextStart = false;
                runtime.StartHold = _startHold;
                runtime.Held = _held;
            }

            if (_holdNextApply)
            {
                _holdNextApply = false;
                runtime.ApplyHold = _applyHold;
                runtime.ApplyHeld = _applyHeld;
                runtime.ArmApplyHold();
            }

            Created.Add(runtime);
            return runtime;
        }
    }

    internal sealed class ThemeRecordingRuntime(PaneSpawnOptions options) : IPaneRuntime, IPaneVtSnapshot
    {
        private readonly object _applyGate = new();
        private int _applyHoldArmed;

        public PaneId Id { get; } = options.Id;
        public PaneSpawnOptions Options { get; } = options;
        public bool IsAlive { get; private set; } = true;
        public int? ExitCode { get; }
        public int? Pid { get; } = 42_100;
        public List<string> Order { get; } = [];
        public int StartCount { get; private set; }
        public HostTerminalTheme LastTheme { get; private set; }
        public long AppliedVersion { get; private set; }
        public long FeedGeneration { get; private set; }
        public bool ThrowDisposedOnApply { get; set; }
        public Exception? ThrowOnApply { get; set; }
        public TaskCompletionSource? StartHold { get; set; }
        public TaskCompletionSource<ThemeRecordingRuntime>? Held { get; set; }
        public TaskCompletionSource? ApplyHold { get; set; }
        public TaskCompletionSource<ThemeRecordingRuntime>? ApplyHeld { get; set; }
        public PaneFeedPaintDecision LastFeedPaintDecision => new(FeedGeneration, true);
#pragma warning disable CS0067
        public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
        public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
        public event Action<IPaneRuntime, int>? Exited;
#pragma warning restore CS0067

        public void ArmApplyHold()
        {
            ApplyHold ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            ApplyHeld ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _applyHoldArmed, 1);
        }

        public Task WaitApplyHeldAsync() =>
            ApplyHeld?.Task ?? throw new InvalidOperationException("Apply hold was not armed");

        public void ReleaseApplyHold() => ApplyHold?.TrySetResult();

        public void ApplyHostTerminalTheme(HostTerminalTheme theme) =>
            ApplyHostTerminalTheme(theme, version: 0);

        public void ApplyHostTerminalTheme(HostTerminalTheme theme, long version)
        {
            if (Volatile.Read(ref _applyHoldArmed) == 1
                && Interlocked.Exchange(ref _applyHoldArmed, 0) == 1)
            {
                ApplyHeld?.TrySetResult(this);
                ApplyHold?.Task.GetAwaiter().GetResult();
            }

            lock (_applyGate)
            {
                if (ThrowDisposedOnApply)
                    throw new ObjectDisposedException(nameof(ThemeRecordingRuntime));
                if (ThrowOnApply is { } ex)
                    throw ex;
                if (version > 0 && version < AppliedVersion)
                    return;
                if (version > AppliedVersion)
                    AppliedVersion = version;
                Order.Add("apply");
                LastTheme = theme;
                FeedGeneration++;
            }
        }

        public Task StartAsync(CancellationToken ct)
        {
            Order.Add("start");
            StartCount++;
            if (StartHold is null)
            {
                IsAlive = true;
                return Task.CompletedTask;
            }

            return HoldStartAsync(ct);
        }

        private async Task HoldStartAsync(CancellationToken ct)
        {
            Held?.TrySetResult(this);
            await StartHold!.Task.WaitAsync(ct).ConfigureAwait(false);
            IsAlive = true;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask WriteTextAsync(string text, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct) => ValueTask.CompletedTask;
        public string ReadVisibleText() => "";
        public string ReadRecentText(int maxLines) => "";
        public string ReadRecentUnwrappedText(int maxLines) => "";
        public string ReadDetectionText() => "";
        public bool TryCaptureSnapshotJson(out string json, out long feedGeneration)
        {
            feedGeneration = FeedGeneration;
            var marker = LastTheme.Background?.R.ToString() ?? "0";
            json =
                "{\"schemaVersion\":1,\"provider\":\"ghostty\",\"cols\":2,\"rows\":1," +
                "\"cursor\":{\"col\":0,\"row\":0,\"visible\":true},\"activeScreen\":\"main\"," +
                "\"cells\":[[{\"text\":\"" + marker + "\",\"width\":1},{\"text\":\" \",\"width\":1}]]}";
            return true;
        }

        public bool TryGetScrollMetrics(out int offset, out int maxOffset)
        {
            offset = 0;
            maxOffset = 0;
            return false;
        }

        public bool TryGetScrollOrigin(out int offset)
        {
            offset = 0;
            return false;
        }

        public bool TrySetScrollOrigin(int offset) => false;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingConnection(string id) : IClientConnection
    {
        private readonly object _gate = new();
        public string ConnectionId { get; } = id;
        public List<string> Lines { get; } = [];

        public IReadOnlyList<JsonElement> Parsed
        {
            get
            {
                lock (_gate)
                {
                    return Lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
                }
            }
        }

        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default)
        {
            lock (_gate)
                Lines.Add(jsonLine);
            return Task.CompletedTask;
        }

        public Task<JsonElement> WaitForTypeAsync(string type, int timeoutMs = 2000) =>
            WaitForAsync(type, _ => true, timeoutMs);

        public async Task<JsonElement> WaitForAsync(
            string type,
            Func<JsonElement, bool> payloadMatch,
            int timeoutMs = 2000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    foreach (var line in Lines)
                    {
                        using var doc = JsonDocument.Parse(line);
                        if (doc.RootElement.TryGetProperty("params", out var p)
                            && p.TryGetProperty("type", out var t)
                            && t.GetString() == type
                            && p.TryGetProperty("payload", out var payload)
                            && payloadMatch(payload))
                        {
                            return doc.RootElement.Clone();
                        }
                    }
                }

                await Task.Delay(15).ConfigureAwait(false);
            }

            throw new TimeoutException("Did not observe " + type + " in " + string.Join('\n', Lines));
        }
    }
}
