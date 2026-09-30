using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.Plugins;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Infrastructure.Plugins;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class PluginResourceControlPlaneTests : IDisposable
{
    private readonly string _root;
    private readonly MemoryPluginFiles _files;
    private readonly PluginHostService _host;

    public PluginResourceControlPlaneTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hypa-plugin-res-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _files = new MemoryPluginFiles();
        var paths = new SystemPluginPathRoots(_root);
        _host = new PluginHostService(
            _files,
            new SystemPluginClock(),
            paths,
            new PluginManifestParser(),
            new FilePluginRegistry(_files, paths),
            new ProcessPluginLauncher(),
            processRegistry: new PluginProcessRegistry(),
            refreshRunner: new ProcessPluginRefreshRunner());
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
    public void Protocol_includes_resource_methods_and_omits_ui_names()
    {
        Assert.Contains(ProtocolMethods.PluginResourceList, ProtocolMethods.All);
        Assert.Contains(ProtocolMethods.PluginResourceGet, ProtocolMethods.All);
        Assert.Contains(ProtocolMethods.PluginResourcePublish, ProtocolMethods.All);
        Assert.Contains(ProtocolMethods.PluginResourceRemove, ProtocolMethods.All);
        Assert.Contains(ProtocolEventTypes.ResourceChanged, ProtocolEventTypes.PluginResources);
        Assert.True(ProtocolEventTypes.IsNamedSubscribeToken(ProtocolEventTypes.ResourceChanged));
        Assert.Equal(EventClass.Lifecycle, EventClassMap.FromWireType(ProtocolEventTypes.ResourceChanged));
        Assert.Contains(PluginGrantCatalog.ResourcePublish, PluginGrantCatalog.Advertised);
        Assert.Contains(PluginGrantCatalog.ResourceRead, PluginGrantCatalog.Advertised);
        Assert.True(PluginGrantCatalog.IsDispatchAllowed(ProtocolMethods.PluginResourceList));
        Assert.True(PluginGrantCatalog.IsDispatchAllowed(ProtocolMethods.PluginResourcePublish));

        Assert.DoesNotContain(ProtocolMethods.All, m => m.StartsWith("sidebar.", StringComparison.Ordinal));
        Assert.DoesNotContain("toast.show", ProtocolMethods.All);
        Assert.DoesNotContain("status.render", ProtocolMethods.All);
        Assert.DoesNotContain("collection.view.set", ProtocolMethods.All);
        Assert.DoesNotContain(ProtocolEventTypes.PluginResources, t => t.StartsWith("sidebar.", StringComparison.Ordinal));
    }

    [Fact]
    public void Fixtures_cover_ready_stale_unavailable_malformed_denied_and_full()
    {
        foreach (var method in ProtocolMethods.Plugins)
        {
            var req = FixtureCatalog.Load(FixtureCatalog.MethodRequestPath(method));
            var res = FixtureCatalog.Load(FixtureCatalog.MethodResponsePath(method));
            Assert.Contains(method, req, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(res));
        }

        var changed = FixtureCatalog.Load(FixtureCatalog.EventPath(ProtocolEventTypes.ResourceChanged));
        Assert.Contains("resource.changed", changed, StringComparison.Ordinal);
        Assert.Contains("summary", changed, StringComparison.Ordinal);

        Assert.Contains("ready", FixtureCatalog.Load(FixtureCatalog.PluginResourceGetReadyResponse), StringComparison.Ordinal);
        Assert.Contains("stale", FixtureCatalog.Load(FixtureCatalog.PluginResourceGetStaleResponse), StringComparison.Ordinal);
        Assert.Contains("unavailable", FixtureCatalog.Load(FixtureCatalog.PluginResourceGetUnavailableResponse), StringComparison.Ordinal);
        Assert.Contains("plugin_resource_malformed", FixtureCatalog.Load(FixtureCatalog.PluginResourcePublishMalformed), StringComparison.Ordinal);
        Assert.Contains("source_denied", FixtureCatalog.Load(FixtureCatalog.PluginResourcePublishDenied), StringComparison.Ordinal);
        Assert.Contains("plugin_resource_full", FixtureCatalog.Load(FixtureCatalog.PluginResourcePublishFull), StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_get_publish_remove_without_token()
    {
        LinkReadyPlugin();
        var (cp, _) = NewPlane();
        try
        {
            var published = await DispatchAsync(cp, ProtocolMethods.PluginResourcePublish, ReadyEnvelope(1));
            Assert.False(published.GetProperty("ignored").GetBoolean());
            Assert.Equal("ready", published.GetProperty("resource").GetProperty("freshness").GetString());

            var listed = await cp.DispatchAsync(ProtocolMethods.PluginResourceList, null, CancellationToken.None);
            Assert.Equal(1, listed.GetProperty("resources").GetArrayLength());

            var got = await DispatchAsync(
                cp,
                ProtocolMethods.PluginResourceGet,
                new JsonObject { ["resource_id"] = "plugin:example.res/queue" });
            Assert.Equal(1, got.GetProperty("resource").GetProperty("revision").GetInt64());

            var removed = await DispatchAsync(
                cp,
                ProtocolMethods.PluginResourceRemove,
                new JsonObject { ["resource_id"] = "plugin:example.res/queue" });
            Assert.True(removed.GetProperty("removed").GetBoolean());
            var empty = await cp.DispatchAsync(ProtocolMethods.PluginResourceList, null, CancellationToken.None);
            Assert.Equal(0, empty.GetProperty("resources").GetArrayLength());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Unknown_projection_fails_closed()
    {
        LinkReadyPlugin();
        var (cp, _) = NewPlane();
        try
        {
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                DispatchAsync(cp, ProtocolMethods.PluginResourcePublish, ReadyEnvelope(1, schema: "hypa.projection.collection.v0")));
            Assert.Equal(PluginError.UnknownProjection, ex.ErrorCode);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Missing_grant_returns_capability_missing()
    {
        var dir = WritePlugin("example.res", DefaultManifest("example.res") + """

            [grants]
            request = ["action.invoke:self"]

            [[actions]]
            id = "open"
            title = "Open"
            command = ["/bin/echo", "ok"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var token = _host.PeekGrantToken("example.res");
        var (cp, _) = NewPlane();
        try
        {
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PluginResourcePublish,
                    JsonDocument.Parse(ReadyEnvelope(1).ToJsonString()).RootElement,
                    connection: null,
                    grantToken: token,
                    pluginConnection: true,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.CapabilityInvalid, ex.Code);
            Assert.Equal(ProtocolErrors.CapabilityMissing, ex.ErrorCode);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Resource_id_outside_caller_prefix_returns_source_denied()
    {
        LinkReadyPlugin();
        var token = _host.PeekGrantToken("example.res");
        var (cp, _) = NewPlane();
        try
        {
            var envelope = ReadyEnvelope(1);
            envelope["resource_id"] = "plugin:other.plugin/queue";
            envelope["owner_id"] = "plugin:other.plugin";
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PluginResourcePublish,
                    JsonDocument.Parse(envelope.ToJsonString()).RootElement,
                    connection: null,
                    grantToken: token,
                    pluginConnection: true,
                    CancellationToken.None));
            Assert.Equal(PluginError.SourceDenied, ex.ErrorCode);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Stale_publish_is_ignored_and_stale_invoke_returns_stale_revision()
    {
        LinkReadyPlugin();
        var (cp, _) = NewPlane();
        try
        {
            await DispatchAsync(cp, ProtocolMethods.PluginResourcePublish, ReadyEnvelope(2));
            var ignored = await DispatchAsync(cp, ProtocolMethods.PluginResourcePublish, ReadyEnvelope(1, summary: "old"));
            Assert.True(ignored.GetProperty("ignored").GetBoolean());
            Assert.Equal("3 need attention", ignored.GetProperty("resource").GetProperty("value").GetProperty("summary").GetString());

            var stale = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                DispatchAsync(cp, ProtocolMethods.PluginActionInvoke, new JsonObject
                {
                    ["plugin_id"] = "example.res",
                    ["action_id"] = "open",
                    ["resource_id"] = "plugin:example.res/queue",
                    ["revision"] = 1,
                }));
            Assert.Equal(ProtocolErrorCodes.InvalidState, stale.Code);
            Assert.Equal(PluginError.StaleRevision, stale.ErrorCode);

            var ok = await DispatchAsync(cp, ProtocolMethods.PluginActionInvoke, new JsonObject
            {
                ["plugin_id"] = "example.res",
                ["action_id"] = "open",
                ["resource_id"] = "plugin:example.res/queue",
                ["revision"] = 2,
            });
            Assert.Equal("open", ok.GetProperty("action").GetProperty("action_id").GetString());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Oversized_publish_is_rejected_whole()
    {
        LinkReadyPlugin();
        var (cp, _) = NewPlane();
        try
        {
            var envelope = ReadyEnvelope(1, summary: new string('x', 70_000));
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                DispatchAsync(cp, ProtocolMethods.PluginResourcePublish, envelope));
            Assert.Equal(PluginError.ResourceFull, ex.ErrorCode);
            var listed = await cp.DispatchAsync(ProtocolMethods.PluginResourceList, null, CancellationToken.None);
            Assert.Equal(0, listed.GetProperty("resources").GetArrayLength());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Malformed_publish_is_rejected_whole()
    {
        LinkReadyPlugin();
        var (cp, _) = NewPlane();
        try
        {
            await DispatchAsync(cp, ProtocolMethods.PluginResourcePublish, ReadyEnvelope(1));
            var envelope = ReadyEnvelope(2);
            envelope["value"] = new JsonObject
            {
                ["summary"] = "bad",
                ["items"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "work-42",
                        ["status"] = "nope",
                        ["attention"] = 0,
                    },
                },
            };
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                DispatchAsync(cp, ProtocolMethods.PluginResourcePublish, envelope));
            Assert.Equal(PluginError.ResourceMalformed, ex.ErrorCode);
            var got = await DispatchAsync(
                cp,
                ProtocolMethods.PluginResourceGet,
                new JsonObject { ["resource_id"] = "plugin:example.res/queue" });
            Assert.Equal(1, got.GetProperty("resource").GetProperty("revision").GetInt64());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Snapshot_includes_live_resources_and_restart_does_not_restore_them()
    {
        LinkReadyPlugin();
        var (cp, _) = NewPlane();
        try
        {
            await DispatchAsync(cp, ProtocolMethods.PluginResourcePublish, ReadyEnvelope(1));
            var snap = await cp.DispatchAsync(ProtocolMethods.SessionSnapshot, null, CancellationToken.None);
            Assert.Equal(1, snap.GetProperty("resources").GetArrayLength());
            Assert.Equal("plugin:example.res/queue", snap.GetProperty("resources")[0].GetProperty("resource_id").GetString());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }

        var (restarted, _) = NewPlane();
        try
        {
            var snap = await restarted.DispatchAsync(ProtocolMethods.SessionSnapshot, null, CancellationToken.None);
            Assert.False(snap.TryGetProperty("resources", out _));
        }
        finally
        {
            await restarted.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Disable_and_unlink_drop_plugin_resources()
    {
        LinkReadyPlugin();
        var (cp, _) = NewPlane();
        try
        {
            await DispatchAsync(cp, ProtocolMethods.PluginResourcePublish, ReadyEnvelope(1));
            await DispatchAsync(cp, ProtocolMethods.PluginDisable, new JsonObject { ["plugin_id"] = "example.res" });
            var afterDisable = await cp.DispatchAsync(ProtocolMethods.PluginResourceList, null, CancellationToken.None);
            Assert.Equal(0, afterDisable.GetProperty("resources").GetArrayLength());

            await DispatchAsync(cp, ProtocolMethods.PluginEnable, new JsonObject { ["plugin_id"] = "example.res" });
            await DispatchAsync(cp, ProtocolMethods.PluginResourcePublish, ReadyEnvelope(3));
            await DispatchAsync(cp, ProtocolMethods.PluginUnlink, new JsonObject { ["plugin_id"] = "example.res" });
            var afterUnlink = await cp.DispatchAsync(ProtocolMethods.PluginResourceList, null, CancellationToken.None);
            Assert.Equal(0, afterUnlink.GetProperty("resources").GetArrayLength());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Occupant_crash_drops_plugin_resources()
    {
        LinkReadyPlugin();
        var factory = TestPaneFactories.Scripted();
        var (cp, _) = NewPlane(factory);
        try
        {
            var paneId = await CreateWorkspacePaneAsync(cp);
            _host.NoteOwnedPane("example.res", "board", paneId);
            await DispatchAsync(cp, ProtocolMethods.PluginResourcePublish, ReadyEnvelope(1));
            Assert.NotEmpty(factory.Created);
            factory.Created[^1].FireExited(1);
            var listed = await cp.DispatchAsync(ProtocolMethods.PluginResourceList, null, CancellationToken.None);
            Assert.Equal(0, listed.GetProperty("resources").GetArrayLength());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Startup_hooks_survive_corrupt_plugins_json()
    {
        var paths = new SystemPluginPathRoots(_root);
        _files.WriteAllText(paths.RegistryPath, "{not-json");
        var (cp, _) = NewPlane();
        try
        {
            await cp.FlushPluginStartupHooksAsync(CancellationToken.None);
            var ping = await cp.DispatchAsync(ProtocolMethods.Ping, null, CancellationToken.None);
            Assert.True(ping.GetProperty("ok").GetBoolean());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Startup_hooks_survive_stale_registry_row_without_resources()
    {
        var paths = new SystemPluginPathRoots(_root);
        _files.WriteAllText(
            paths.RegistryPath,
            """
            [
              {
                "plugin_id": "example.stale",
                "name": "Test example.stale",
                "version": "0.1.0",
                "min_hypa_version": "0.1.0",
                "manifest_path": "/tmp/hypa-missing/example.stale/hypa-plugin.toml",
                "plugin_root": "/tmp/hypa-missing/example.stale",
                "enabled": true,
                "platforms": ["linux", "macos"],
                "startup": [],
                "actions": [],
                "events": [],
                "panes": [],
                "link_handlers": [],
                "requested_grants": [],
                "warnings": ["manifest unavailable: plugin manifest not found"],
                "enable_generation": 1,
                "source_kind": "local"
              }
            ]
            """);
        var (cp, _) = NewPlane();
        try
        {
            await cp.FlushPluginStartupHooksAsync(CancellationToken.None);
            var ping = await cp.DispatchAsync(ProtocolMethods.Ping, null, CancellationToken.None);
            Assert.True(ping.GetProperty("ok").GetBoolean());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Failed_refresh_marks_stale_and_ping_still_works()
    {
        var dir = WritePlugin("example.res", DefaultManifest("example.res") + """

            [grants]
            request = ["resource.publish", "resource.read"]

            [[resources]]
            id = "queue"
            kind = "collection"
            projection = "hypa.projection.collection.v1"
            title = "Queue"
            command = ["/bin/false"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var (cp, _) = NewPlane();
        try
        {
            await cp.RefreshPluginResourceAsync("example.res", "queue", CancellationToken.None);
            var got = await DispatchAsync(
                cp,
                ProtocolMethods.PluginResourceGet,
                new JsonObject { ["resource_id"] = "plugin:example.res/queue" });
            Assert.Equal(PluginResourceLimits.FreshnessUnavailable, got.GetProperty("resource").GetProperty("freshness").GetString());
            var logs = await cp.DispatchAsync(
                ProtocolMethods.PluginLogList,
                JsonDocument.Parse("""{"plugin_id":"example.res"}""").RootElement,
                CancellationToken.None);
            Assert.Equal(1, logs.GetProperty("logs").GetArrayLength());
            Assert.Equal("resource.refresh", logs.GetProperty("logs")[0].GetProperty("event").GetString());
            var ping = await cp.DispatchAsync(ProtocolMethods.Ping, null, CancellationToken.None);
            Assert.True(ping.GetProperty("ok").GetBoolean());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Pull_refresh_writes_one_log_and_accepts_stdout()
    {
        var dir = WritePlugin("example.res", DefaultManifest("example.res") + """

            [grants]
            request = ["resource.publish", "resource.read"]

            [[resources]]
            id = "queue"
            kind = "collection"
            projection = "hypa.projection.collection.v1"
            title = "Queue"
            command = ["/bin/echo", "{\"schema\":\"hypa.projection.collection.v1\",\"revision\":4,\"value\":{\"summary\":\"from pull\",\"items\":[]}}"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var (cp, _) = NewPlane();
        try
        {
            await cp.RefreshPluginResourceAsync("example.res", "queue", CancellationToken.None);
            var got = await DispatchAsync(
                cp,
                ProtocolMethods.PluginResourceGet,
                new JsonObject { ["resource_id"] = "plugin:example.res/queue" });
            Assert.Equal("from pull", got.GetProperty("resource").GetProperty("value").GetProperty("summary").GetString());
            Assert.Equal(4, got.GetProperty("resource").GetProperty("revision").GetInt64());
            var logs = await cp.DispatchAsync(
                ProtocolMethods.PluginLogList,
                JsonDocument.Parse("""{"plugin_id":"example.res"}""").RootElement,
                CancellationToken.None);
            Assert.Equal(1, logs.GetProperty("logs").GetArrayLength());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Refresh_is_serialized_per_resource()
    {
        var runner = new OverlapRefreshRunner();
        var root = Path.Combine(Path.GetTempPath(), "hypa-plugin-ref-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var files = new MemoryPluginFiles();
        var paths = new SystemPluginPathRoots(root);
        var host = new PluginHostService(
            files,
            new SystemPluginClock(),
            paths,
            new PluginManifestParser(),
            new FilePluginRegistry(files, paths),
            new ProcessPluginLauncher(),
            processRegistry: new PluginProcessRegistry(),
            refreshRunner: runner);
        try
        {
            var dir = Path.Combine(root, "example.res");
            files.CreateDirectory(dir);
            files.WriteAllText(
                Path.Combine(dir, PluginHostService.ManifestFileName),
                DefaultManifest("example.res") + """

                    [grants]
                    request = ["resource.publish", "resource.read"]

                    [[resources]]
                    id = "queue"
                    kind = "collection"
                    projection = "hypa.projection.collection.v1"
                    title = "Queue"
                    command = ["/bin/true"]
                    """);
            Assert.True(host.Link(dir, true).IsOk);
            var app = new AppState(SessionId.New("plugin-res-serial"));
            app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
            var cp = new ControlPlaneService(
                app,
                TestPaneFactories.Stub(),
                new PaneIntelligencePipeline(),
                new HeuristicAgentDetector(),
                plugins: host);
            try
            {
                var first = cp.RefreshPluginResourceAsync("example.res", "queue", CancellationToken.None);
                var second = cp.RefreshPluginResourceAsync("example.res", "queue", CancellationToken.None);
                await Task.WhenAll(first, second);
                Assert.Equal(1, runner.MaxInFlight);
                Assert.Equal(2, runner.Starts);
            }
            finally
            {
                await cp.ShutdownAsync(CancellationToken.None);
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task Updates_coalesce_to_one_event_per_resource_per_tick()
    {
        LinkReadyPlugin();
        var sqliteDir = Path.Combine(Path.GetTempPath(), "hypa-res-ev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sqliteDir);
        var paths = new RuntimeStatePaths { StateDirectory = sqliteDir };
        try
        {
            var migrator = new SqliteRuntimeSchemaMigrator(paths);
            Assert.True((await migrator.MigrateAsync()).IsOk);
            var state = new AppState(SessionId.New("plugin-res-ev"));
            state.UpdateSession(s => s with { Name = "plugin-res-ev", LifecycleState = SessionLifecycle.Ready });
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
                        ["types"] = new JsonArray(ProtocolEventTypes.ResourceChanged),
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

                await DispatchAsync(cp, ProtocolMethods.PluginResourcePublish, ReadyEnvelope(1, summary: "a"));
                await DispatchAsync(cp, ProtocolMethods.PluginResourcePublish, ReadyEnvelope(2, summary: "b"));
                await DispatchAsync(cp, ProtocolMethods.PluginResourcePublish, ReadyEnvelope(3, summary: "c"));
                await cp.ResourceChangedFlush;

                var changed = sink.Parsed
                    .Where(e => e.TryGetProperty("params", out var p)
                        && p.TryGetProperty("type", out var t)
                        && t.GetString() == ProtocolEventTypes.ResourceChanged)
                    .ToArray();
                Assert.Single(changed);
                Assert.Equal(
                    "c",
                    changed[0].GetProperty("params").GetProperty("payload").GetProperty("summary").GetString());

                sink.Lines.Clear();
                await DispatchAsync(
                    cp,
                    ProtocolMethods.PluginResourceRemove,
                    new JsonObject { ["resource_id"] = "plugin:example.res/queue" });
                await cp.ResourceChangedFlush;
                var removed = sink.Parsed
                    .Where(e => e.TryGetProperty("params", out var p)
                        && p.TryGetProperty("type", out var t)
                        && t.GetString() == ProtocolEventTypes.ResourceChanged)
                    .ToArray();
                Assert.Single(removed);
                Assert.True(removed[0].GetProperty("params").GetProperty("payload").GetProperty("removed").GetBoolean());
            }
            finally
            {
                await cp.ShutdownAsync(CancellationToken.None);
            }
        }
        finally
        {
            SqliteTestCleanup.ReleaseAndDelete(sqliteDir, paths.DatabasePath);
        }
    }

    [Fact]
    public async Task Resource_changed_redacts_like_pane_titles()
    {
        LinkReadyPlugin();
        var sqliteDir = Path.Combine(Path.GetTempPath(), "hypa-res-redact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sqliteDir);
        var paths = new RuntimeStatePaths { StateDirectory = sqliteDir };
        try
        {
            var migrator = new SqliteRuntimeSchemaMigrator(paths);
            Assert.True((await migrator.MigrateAsync()).IsOk);
            var state = new AppState(SessionId.New("plugin-res-redact"));
            state.UpdateSession(s => s with { Name = "plugin-res-redact", LifecycleState = SessionLifecycle.Ready });
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
                        ["types"] = new JsonArray(ProtocolEventTypes.ResourceChanged),
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

                await DispatchAsync(
                    cp,
                    ProtocolMethods.PluginResourcePublish,
                    ReadyEnvelope(1, summary: "sk-abcdefghijklmnopqrstuvwxyz123456"));
                await cp.ResourceChangedFlush;
                var line = Assert.Single(sink.Lines, l => l.Contains("resource.changed", StringComparison.Ordinal));
                Assert.Contains("[REDACTED]", line, StringComparison.Ordinal);
                Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz123456", line, StringComparison.Ordinal);
            }
            finally
            {
                await cp.ShutdownAsync(CancellationToken.None);
            }
        }
        finally
        {
            SqliteTestCleanup.ReleaseAndDelete(sqliteDir, paths.DatabasePath);
        }
    }

    private void LinkReadyPlugin()
    {
        var dir = WritePlugin("example.res", DefaultManifest("example.res") + """

            [grants]
            request = ["resource.publish", "resource.read", "action.invoke:self"]

            [[actions]]
            id = "open"
            title = "Open"
            command = ["/bin/echo", "ok"]

            [[resources]]
            id = "queue"
            kind = "collection"
            projection = "hypa.projection.collection.v1"
            title = "Queue"
            """);
        var linked = _host.Link(dir, true);
        Assert.True(linked.IsOk, linked.IsOk ? "" : linked.Error.Message);
    }

    private (ControlPlaneService Cp, AppState App) NewPlane(IPaneRuntimeFactory? factory = null)
    {
        var app = new AppState(SessionId.New("plugin-res"));
        app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var cp = new ControlPlaneService(
            app,
            factory ?? TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            plugins: _host);
        return (cp, app);
    }

    private static async Task<string> CreateWorkspacePaneAsync(ControlPlaneService cp)
    {
        var created = await cp.DispatchAsync(
            ProtocolMethods.WorkspaceCreate,
            JsonDocument.Parse(
                $$"""{"cwd":{{JsonSerializer.Serialize(Path.GetTempPath())}},"command":"/bin/true","create_pane":true}""")
                .RootElement,
            CancellationToken.None);
        return created.GetProperty("pane").GetProperty("pane_id").GetString()!;
    }

    private static async Task<JsonElement> DispatchAsync(ControlPlaneService cp, string method, JsonObject body)
    {
        using var doc = JsonDocument.Parse(body.ToJsonString());
        return await cp.DispatchAsync(method, doc.RootElement, CancellationToken.None);
    }

    private static JsonObject ReadyEnvelope(
        long revision,
        string summary = "3 need attention",
        string schema = PluginResourceLimits.CollectionSchema) =>
        new()
        {
            ["owner_id"] = "plugin:example.res",
            ["resource_id"] = "plugin:example.res/queue",
            ["schema"] = schema,
            ["revision"] = revision,
            ["value"] = new JsonObject
            {
                ["summary"] = summary,
                ["items"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "work-42",
                        ["label"] = "Plan retry policy",
                        ["status"] = "blocked",
                        ["attention"] = 2,
                    },
                },
            },
        };

    private string WritePlugin(string id, string manifest)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, PluginHostService.ManifestFileName);
        _files.WriteAllText(path, manifest);
        return dir;
    }

    private static string DefaultManifest(string id) =>
        $"""
        id = "{id}"
        name = "Test {id}"
        version = "0.1.0"
        min_hypa_version = "0.1.0"
        platforms = ["linux", "macos"]
        """;

    private sealed class OverlapRefreshRunner : IPluginRefreshRunner
    {
        private readonly object _gate = new();
        private int _inFlight;

        public int MaxInFlight { get; private set; }

        public int Starts { get; private set; }

        public PluginProcessExit Run(
            string program,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment,
            int outputCapBytes,
            TimeSpan timeout)
        {
            _ = program;
            _ = arguments;
            _ = workingDirectory;
            _ = environment;
            _ = outputCapBytes;
            _ = timeout;
            lock (_gate)
            {
                Starts++;
                _inFlight++;
                if (_inFlight > MaxInFlight)
                    MaxInFlight = _inFlight;
            }

            Thread.Sleep(80);
            lock (_gate)
                _inFlight--;
            return new PluginProcessExit(
                0,
                """{"schema":"hypa.projection.collection.v1","revision":1,"value":{"summary":"x","items":[]}}""",
                "",
                null);
        }
    }

    private sealed class CapturingSink : IClientConnection
    {
        private readonly object _gate = new();
        public string ConnectionId { get; } = "c_res";
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

    private sealed class MemoryPluginFiles : IPluginFiles
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
        private readonly HashSet<string> _dirs = new(StringComparer.Ordinal);

        public bool FileExists(string path) => _files.ContainsKey(Path.GetFullPath(path));

        public bool DirectoryExists(string path) => _dirs.Contains(Path.GetFullPath(path));

        public string ReadAllText(string path) => _files[Path.GetFullPath(path)];

        public void WriteAllText(string path, string contents)
        {
            var full = Path.GetFullPath(path);
            _files[full] = contents;
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent))
                _dirs.Add(parent);
        }

        public void CreateDirectory(string path) => _dirs.Add(Path.GetFullPath(path));

        public bool DeleteFile(string path) => _files.Remove(Path.GetFullPath(path));

        public bool DeleteDirectory(string path)
        {
            var full = Path.GetFullPath(path);
            var prefix = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var removed = _dirs.Remove(full);
            var nestedDirs = _dirs.Where(d => d.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            foreach (var dir in nestedDirs)
            {
                _dirs.Remove(dir);
                removed = true;
            }

            var nestedFiles = _files.Keys
                .Where(key => key.StartsWith(prefix, StringComparison.Ordinal) || key == full)
                .ToArray();
            foreach (var file in nestedFiles)
            {
                _files.Remove(file);
                removed = true;
            }

            return removed;
        }

        public string GetFullPath(string path) => Path.GetFullPath(path);
    }
}
