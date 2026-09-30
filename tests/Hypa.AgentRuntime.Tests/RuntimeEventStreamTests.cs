using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Infrastructure.Config;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Settings;
using Hypa.ControlPlane;
using Hypa.ControlPlane.Unix;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class RuntimeEventStreamTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public RuntimeEventStreamTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-stream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose()
    {
        try
        {
            SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);
        }
        catch
        {
        }
    }

    [Fact]
    public void Unmapped_wire_type_is_refused()
    {
        Assert.False(EventClassMap.TryFromWireType("test.unmapped", out _));
        Assert.Throws<ArgumentException>(() => EventClassMap.FromWireType("test.unmapped"));
    }

    [Fact]
    public void Checkpoint_lifecycle_is_control_through_explicit_branch()
    {
        Assert.True(EventClassMap.TryFromWireType(ProtocolEventTypes.CheckpointLifecycle, out var mapped));
        Assert.Equal(EventClass.Control, mapped);
        Assert.False(EventClassMap.TryFromWireType("invented.lifecycle", out _));
    }

    [Fact]
    public void Every_protocol_event_type_has_an_explicit_class()
    {
        foreach (var type in ProtocolEventTypes.All.Distinct(StringComparer.Ordinal))
        {
            Assert.True(
                EventClassMap.TryFromWireType(type, out _),
                type + " must have an explicit class mapping");
        }
    }

    [Fact]
    public async Task Unmapped_type_is_not_journaled()
    {
        await using var harness = await Harness.Start(_paths);
        var before = harness.Journal.NextSeq;
        var ok = await harness.Control.TryEmitMappedAsync(
            "test.unmapped",
            "{}",
            CancellationToken.None);
        Assert.False(ok);
        Assert.Equal(before, harness.Journal.NextSeq);
    }

    [Fact]
    public async Task Theme_apply_emits_settings_changed_and_replays()
    {
        var configPath = Path.Combine(_dir, "config.toml");
        File.WriteAllText(path: configPath, contents: "[theme]\nname = \"catppuccin\"\n");
        var loader = new FileAttachConfigLoader(new PathEnv(configPath));
        var loaded = loader.Load();
        Assert.True(loaded.IsOk, loaded.IsOk ? null : loaded.Error.ToString());
        await using var harness = await Harness.Start(_paths, loader, loaded.Value);

        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var engine = new KeyEngine(table, settingsPages: SettingsPageRegistry.Product());
        var live = new AttachLiveState
        {
            Engine = engine,
            Table = table,
            Dispatcher = null!,
            ConfigLoader = loader,
            SessionName = "stream",
            AttachClientId = "c1",
            ProcessLog = new CapturingProcessLogSink(ProcessLogLevel.Debug),
        };
        live.Engine.Settings.Bind(live.Theme, live.Ui);
        live.Engine.Settings.OpenAt(SettingsPageRegistry.ThemeId);
        Assert.True(live.Engine.Settings.SelectItem(1));
        Assert.True(live.Engine.Settings.Apply());
        var expectedTheme = live.Engine.Settings.PendingPatch![0].TomlLiteral.Trim('"');

        var port = new DispatchPort(harness.Control);
        var ok = await AttachSession.ApplySettingsPatchAsync(live, port, CancellationToken.None);
        Assert.True(ok);
        Assert.Equal(expectedTheme, loader.Load().Value.Theme.Name);

        var sink = new CapturingSink("c_settings");
        var sub = await harness.Control.DispatchAsync(
            ProtocolMethods.EventsSubscribe,
            Json(new JsonObject
            {
                ["from_seq"] = 0,
                ["live"] = true,
                ["types"] = new JsonArray(ProtocolEventTypes.SettingsChanged),
            }),
            sink,
            CancellationToken.None);
        var subId = sub.GetProperty("subscription_id").GetString()!;
        var prepared = await harness.Control.PrepareEventsSubscribeAsync(subId, CancellationToken.None);
        Assert.True(prepared.IsOk, prepared.IsOk ? null : prepared.Error.Message);
        await harness.Control.CompleteEventsSubscribeAsync(
            subId, prepared.Value, sink, CancellationToken.None);

        await Task.Delay(50);
        var types = EventTypes(sink);
        Assert.Contains(ProtocolEventTypes.SettingsChanged, types);
        var payload = sink.Parsed
            .Select(el => el.TryGetProperty("params", out var p) ? p : default)
            .First(p => p.ValueKind == JsonValueKind.Object
                && p.TryGetProperty("type", out var t)
                && t.GetString() == ProtocolEventTypes.SettingsChanged)
            .GetProperty("payload");
        Assert.Equal(SettingsValuePolicy.ThemeNameKey, payload.GetProperty("key").GetString());
        Assert.Equal(expectedTheme, payload.GetProperty("value").GetString());
        Assert.Single(types, t => t == ProtocolEventTypes.SettingsChanged);
    }

    [Fact]
    public async Task Overlay_lifecycle_never_carries_pane_id()
    {
        await using var harness = await Harness.Start(_paths);
        await harness.Control.EmitOverlayLifecycleIfChangedAsync("terminal", "settings", CancellationToken.None);
        var range = await harness.Journal.ReadRangeAsync(0, null, 20, CancellationToken.None);
        Assert.True(range.IsOk);
        var overlay = range.Value.Single(r => r.Type == ProtocolEventTypes.OverlayLifecycle);
        using var doc = JsonDocument.Parse(overlay.PayloadJson);
        Assert.False(doc.RootElement.TryGetProperty("pane_id", out _));
        Assert.Equal("settings", doc.RootElement.GetProperty("surface").GetString());
        Assert.Equal("opened", doc.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Named_tab_lifecycle_subscribe_excludes_layout_updated()
    {
        await using var harness = await Harness.Start(_paths);
        var sink = new CapturingSink("c_tab");
        var sub = await harness.Control.DispatchAsync(
            ProtocolMethods.EventsSubscribe,
            Json(new JsonObject
            {
                ["from_seq"] = 0,
                ["live"] = true,
                ["types"] = new JsonArray(ProtocolEventTypes.TabLifecycle),
            }),
            sink,
            CancellationToken.None);
        var subId = sub.GetProperty("subscription_id").GetString()!;
        await harness.Control.CompleteEventsSubscribeAsync(
            subId, [], sink, CancellationToken.None);

        var ws = harness.State.CreateWorkspace("/tmp/ws");
        var tab = harness.State.CreateTab(ws.Id);
        await harness.Control.DispatchAsync(
            ProtocolMethods.TabFocus,
            Json(new JsonObject { ["tab_id"] = tab.Id.Value }),
            sink,
            CancellationToken.None);
        await harness.Control.TryEmitMappedAsync(
            ProtocolEventTypes.LayoutUpdated,
            "{}",
            CancellationToken.None);
        await Task.Delay(80);

        var types = EventTypes(sink);
        Assert.Contains(ProtocolEventTypes.TabLifecycle, types);
        Assert.DoesNotContain(ProtocolEventTypes.LayoutUpdated, types);
    }

    [Fact]
    public void Settings_overlay_fixtures_are_additive()
    {
        foreach (var type in ProtocolEventTypes.SettingsOverlay)
        {
            var json = FixtureCatalog.Load(FixtureCatalog.EventPath(type));
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.GetProperty("params").GetProperty("payload").TryGetProperty("note", out _));
            if (type == ProtocolEventTypes.OverlayLifecycle)
                Assert.False(doc.RootElement.GetProperty("params").GetProperty("payload").TryGetProperty("pane_id", out _));
        }
    }

    [Fact]
    public void Plugin_hooks_do_not_accept_settings_or_overlay_names()
    {
        Assert.Null(Hypa.AgentRuntime.Application.Plugins.PluginEventFilterMatcher.MapHookName(
            ProtocolEventTypes.SettingsChanged, "{}"));
        Assert.Null(Hypa.AgentRuntime.Application.Plugins.PluginEventFilterMatcher.MapHookName(
            ProtocolEventTypes.OverlayLifecycle, "{\"surface\":\"settings\",\"state\":\"opened\"}"));
    }

    [Fact]
    public void Named_subscribe_tokens_include_tab_and_workspace_lifecycle()
    {
        Assert.True(ProtocolEventTypes.IsNamedSubscribeToken(ProtocolEventTypes.TabLifecycle));
        Assert.True(ProtocolEventTypes.IsNamedSubscribeToken(ProtocolEventTypes.WorkspaceLifecycle));
        Assert.True(ProtocolEventTypes.IsNamedSubscribeToken(ProtocolEventTypes.SettingsChanged));
        Assert.True(ProtocolEventTypes.IsNamedSubscribeToken(ProtocolEventTypes.OverlayLifecycle));
    }

    private static JsonElement Json(JsonObject obj) =>
        JsonDocument.Parse(obj.ToJsonString()).RootElement.Clone();

    private static List<string?> EventTypes(CapturingSink sink) =>
        sink.Parsed
            .Select(el => el.TryGetProperty("params", out var p) ? p : default)
            .Where(p => p.ValueKind == JsonValueKind.Object
                && p.TryGetProperty("type", out _))
            .Select(p => p.GetProperty("type").GetString())
            .ToList();

    private sealed class CapturingSink(string id) : IClientConnection
    {
        private readonly object _gate = new();
        public string ConnectionId { get; } = id;
        public List<string> Lines { get; } = [];

        public IReadOnlyList<JsonElement> Parsed
        {
            get
            {
                lock (_gate)
                    return Lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
            }
        }

        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default)
        {
            lock (_gate)
                Lines.Add(jsonLine);
            return Task.CompletedTask;
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required ControlPlaneService Control { get; init; }
        public required AppState State { get; init; }
        public required FileRuntimeEventJournal Journal { get; init; }
        public required EventSubscriptionHub Hub { get; init; }

        public static async Task<Harness> Start(
            RuntimeStatePaths paths,
            IAttachConfigLoader? loader = null,
            AttachClientConfig? initial = null)
        {
            var migrator = new SqliteRuntimeSchemaMigrator(paths);
            Assert.True((await migrator.MigrateAsync()).IsOk);
            var state = new AppState(SessionId.New("stream"));
            state.UpdateSession(s => s with { Name = "stream", LifecycleState = SessionLifecycle.Ready });
            var store = new SqliteRuntimeSessionStore(paths);
            Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
            var manifests = new SqliteJournalManifestStore(paths);
            var journal = new FileRuntimeEventJournal(paths, manifests, state.SessionId.Value);
            Assert.True((await journal.RecoverAsync()).IsOk);
            var hub = new EventSubscriptionHub();
            Hypa.AgentRuntime.Application.IAttachConfigRuntime? runtime = null;
            if (loader is not null)
            {
                runtime = new LiveAttachConfigRuntime(loader, initial ?? AttachClientConfig.Default);
            }

            var intel = new PaneIntelligencePipeline();
            var cp = new ControlPlaneService(
                state,
                TestPaneFactories.Create(intel),
                intel,
                new HeuristicAgentDetector(),
                store: store,
                journal: journal,
                subscriptions: hub,
                attachConfigRuntime: runtime);
            return new Harness
            {
                Control = cp,
                State = state,
                Journal = journal,
                Hub = hub,
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Control.ShutdownAsync(CancellationToken.None);
            await Hub.DisposeAsync();
            await Journal.DisposeAsync();
        }
    }

    private sealed class PathEnv(string path) : IAttachConfigEnvironment
    {
        public string? GetVariable(string name) =>
            name == FileAttachConfigLoader.ConfigPathVariable ? path : null;

        public string UserHome => Path.GetDirectoryName(path) ?? "/tmp";

        public string? AppData => null;

        public bool IsWindows => false;

        public bool IsMacOs => true;
    }

    private sealed class DispatchPort(ControlPlaneService control) : IAttachCommandPort
    {
        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            var json = parameters?.ToJsonString() ?? "{}";
            return control.DispatchAsync(
                method,
                JsonDocument.Parse(json).RootElement,
                ct);
        }
    }
}
