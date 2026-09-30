using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Infrastructure.Plugins;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class PluginSettingsTests : IDisposable
{
    private readonly string _root;
    private readonly SystemPluginFiles _files;
    private readonly PluginHostService _host;

    public PluginSettingsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hypa-plugin-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _files = new SystemPluginFiles();
        var paths = new SystemPluginPathRoots(_root);
        _host = new PluginHostService(
            _files,
            new SystemPluginClock(),
            paths,
            new PluginManifestParser(),
            new FilePluginRegistry(_files, paths),
            new ProcessPluginLauncher(),
            processRegistry: new PluginProcessRegistry());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void Protocol_includes_config_methods_and_omits_settings_namespace()
    {
        Assert.Contains(ProtocolMethods.PluginConfigGet, ProtocolMethods.All);
        Assert.Contains(ProtocolMethods.PluginConfigSet, ProtocolMethods.All);
        Assert.Contains(ProtocolMethods.PluginConfigGet, ProtocolMethods.Plugins);
        Assert.Contains(ProtocolMethods.PluginConfigSet, ProtocolMethods.Plugins);
        Assert.Contains(ProtocolEventTypes.ConfigChanged, ProtocolEventTypes.PluginConfig);
        Assert.True(ProtocolEventTypes.IsNamedSubscribeToken(ProtocolEventTypes.ConfigChanged));
        Assert.Equal(EventClass.Lifecycle, EventClassMap.FromWireType(ProtocolEventTypes.ConfigChanged));
        Assert.DoesNotContain(ProtocolMethods.All, m => m.StartsWith("settings.", StringComparison.Ordinal));
    }

    [Fact]
    public void Settings_field_types_link_and_unknown_type_fails_closed()
    {
        var dir = WritePlugin("example.settings", SettingsManifest("example.settings"));
        var linked = _host.Link(dir, true);
        Assert.True(linked.IsOk, linked.IsOk ? "" : linked.Error.Message);
        Assert.Equal(4, linked.Value.Plugin.SettingsFields.Count);
        Assert.Contains(linked.Value.Plugin.SettingsFields, f => f.Type == PluginSettingsFieldTypes.String);
        Assert.Contains(linked.Value.Plugin.SettingsFields, f => f.Type == PluginSettingsFieldTypes.Integer);
        Assert.Contains(linked.Value.Plugin.SettingsFields, f => f.Type == PluginSettingsFieldTypes.Boolean);
        Assert.Contains(linked.Value.Plugin.SettingsFields, f => f.Type == PluginSettingsFieldTypes.Choice);

        var bad = WritePlugin("example.bad-type", DefaultManifest("example.bad-type") + """

            [[settings.field]]
            key = "mode"
            type = "color"
            title = "Mode"
            default = "red"
            """);
        var result = _host.Link(bad, true);
        Assert.False(result.IsOk);
        Assert.Equal(PluginError.UnknownSettingsFieldType, result.Error.Code);
    }

    [Fact]
    public void Widget_colour_and_layout_fields_fail_at_link()
    {
        foreach (var banned in new[] { "widget", "colour", "layout" })
        {
            var dir = WritePlugin("example.banned-" + banned, DefaultManifest("example.banned-" + banned) + $"""

                [[settings.field]]
                key = "mode"
                type = "string"
                title = "Mode"
                default = ""
                {banned} = "x"
                """);
            var result = _host.Link(dir, true);
            Assert.False(result.IsOk);
            Assert.Equal(PluginError.InvalidSettingsField, result.Error.Code);
            Assert.Contains(banned, result.Error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Values_persist_under_config_dir_and_invalid_keeps_last_good()
    {
        LinkSettingsPlugin();
        var configPath = Path.Combine(_root, "plugins", "config", "example.settings", PluginSettingsStore.ConfigFileName);

        var first = _host.SetSettings("example.settings", "poll_ms", "16000");
        Assert.True(first.IsOk);
        Assert.True(first.Value.Applied);
        Assert.True(File.Exists(configPath));
        Assert.Contains("16000", File.ReadAllText(configPath), StringComparison.Ordinal);

        var bad = _host.SetSettings("example.settings", "poll_ms", "not-a-number");
        Assert.True(bad.IsOk);
        Assert.False(bad.Value.Applied);
        Assert.Equal("16000", bad.Value.Values["poll_ms"]);

        var read = _host.GetSettings("example.settings");
        Assert.True(read.IsOk);
        Assert.Equal("16000", read.Value.Values["poll_ms"]);
    }

    [Fact]
    public async Task Config_set_emits_change_event_after_valid_write()
    {
        LinkSettingsPlugin();
        var sqliteDir = Path.Combine(Path.GetTempPath(), "hypa-cfg-ev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sqliteDir);
        var paths = new RuntimeStatePaths { StateDirectory = sqliteDir };
        try
        {
            var migrator = new SqliteRuntimeSchemaMigrator(paths);
            Assert.True((await migrator.MigrateAsync()).IsOk);
            var state = new AppState(SessionId.New("plugin-settings-ev"));
            state.UpdateSession(s => s with { Name = "plugin-settings-ev", LifecycleState = SessionLifecycle.Ready });
            var store = new SqliteRuntimeSessionStore(paths);
            Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
            var manifests = new SqliteJournalManifestStore(paths);
            var journal = new FileRuntimeEventJournal(paths, manifests, state.SessionId.Value);
            Assert.True((await journal.RecoverAsync()).IsOk);
            var hub = new EventSubscriptionHub();
            var cp = new ControlPlaneService(
                state,
                TestPaneFactories.Stub(),
                new PaneIntelligencePipeline(),
                new HeuristicAgentDetector(),
                store: store,
                journal: journal,
                subscriptions: hub,
                plugins: _host);
            var sink = new CapturingSink();
            try
            {
                var sub = await cp.DispatchAsync(
                    ProtocolMethods.EventsSubscribe,
                    JsonDocument.Parse(new JsonObject
                    {
                        ["from_seq"] = 0,
                        ["types"] = new JsonArray(ProtocolEventTypes.ConfigChanged),
                        ["live"] = true,
                    }.ToJsonString()).RootElement,
                    sink,
                    CancellationToken.None);
                await cp.CompleteEventsSubscribeAsync(
                    sub.GetProperty("subscription_id").GetString()!,
                    [],
                    sink,
                    CancellationToken.None);
                sink.Lines.Clear();

                var wrote = await DispatchAsync(
                    cp,
                    ProtocolMethods.PluginConfigSet,
                    new JsonObject
                    {
                        ["plugin_id"] = "example.settings",
                        ["key"] = "poll_ms",
                        ["value"] = "17000",
                    });
                Assert.True(wrote.GetProperty("applied").GetBoolean());
                await Task.Delay(80);

                var changed = sink.Parsed
                    .Where(e => e.TryGetProperty("params", out var p)
                        && p.TryGetProperty("type", out var t)
                        && t.GetString() == ProtocolEventTypes.ConfigChanged)
                    .ToArray();
                Assert.Single(changed);
                Assert.Equal(
                    "17000",
                    changed[0].GetProperty("params").GetProperty("payload").GetProperty("value").GetString());
            }
            finally
            {
                await cp.ShutdownAsync(CancellationToken.None);
            }
        }
        finally
        {
            if (Directory.Exists(sqliteDir))
                Directory.Delete(sqliteDir, recursive: true);
        }
    }

    [Fact]
    public void Unknown_array_tables_and_ordinary_settings_tables_fail_closed()
    {
        foreach (var table in new[] { "actionz" })
        {
            var id = "example." + table.Replace('.', '-');
            var dir = WritePlugin(id, DefaultManifest(id) + $"""

                [[{table}]]
                id = "x"
                """);
            var result = _host.Link(dir, true);
            Assert.False(result.IsOk);
            Assert.Equal(PluginError.UnsupportedTable, result.Error.Code);
            Assert.Equal("unsupported_manifest_table: " + table, result.Error.Message);
        }

        foreach (var table in new[] { "menu_items", "settings.field" })
        {
            var id = "example.regular-" + table.Replace('.', '-');
            var dir = WritePlugin(id, DefaultManifest(id) + $"""

                [{table}]
                id = "x"
                """);
            var result = _host.Link(dir, true);
            Assert.False(result.IsOk);
            Assert.Equal(PluginError.UnsupportedTable, result.Error.Code);
            Assert.Equal("unsupported_manifest_table: " + table, result.Error.Message);
        }
    }

    [Fact]
    public void Settings_field_table_links_when_declared()
    {
        var dir = WritePlugin("example.settings-table", DefaultManifest("example.settings-table") + """

            [[settings.field]]
            key = "enabled"
            type = "boolean"
            title = "Enabled"
            default = "false"
            """);
        var result = _host.Link(dir, true);
        Assert.True(result.IsOk);
        Assert.Single(result.Value.Plugin.SettingsFields);
    }

    private void LinkSettingsPlugin()
    {
        var dir = WritePlugin("example.settings", SettingsManifest("example.settings"));
        var linked = _host.Link(dir, true);
        Assert.True(linked.IsOk);
    }

    private static string SettingsManifest(string id) =>
        DefaultManifest(id) + """

        [[settings.field]]
        key = "gateway_url"
        type = "string"
        title = "Atomic gateway"
        default = ""

        [[settings.field]]
        key = "poll_ms"
        type = "integer"
        title = "Poll period"
        default = "15000"

        [[settings.field]]
        key = "enabled"
        type = "boolean"
        title = "Enabled"
        default = "false"

        [[settings.field]]
        key = "mode"
        type = "choice"
        title = "Mode"
        default = "fast"
        choices = ["fast", "slow"]
        """;

    private static string DefaultManifest(string id, string min = "0.1.0") =>
        $$"""
        id = "{{id}}"
        name = "Example"
        version = "0.1.0"
        min_hypa_version = "{{min}}"
        """;

    private string WritePlugin(string id, string manifest)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, PluginHostService.ManifestFileName);
        File.WriteAllText(path, manifest);
        return dir;
    }

    private static async Task<JsonElement> DispatchAsync(ControlPlaneService cp, string method, JsonObject body)
    {
        using var doc = JsonDocument.Parse(body.ToJsonString());
        return await cp.DispatchAsync(method, doc.RootElement, CancellationToken.None);
    }

    private ControlPlaneService NewPlane()
    {
        var app = new AppState(SessionId.New("plugin-settings"));
        app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        return new ControlPlaneService(
            app,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            plugins: _host);
    }

    private sealed class CapturingSink : IClientConnection
    {
        private readonly object _gate = new();
        public string ConnectionId { get; } = "c_cfg";
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
}
