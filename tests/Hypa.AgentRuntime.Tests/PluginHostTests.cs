using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.Plugins;
using Hypa.AgentRuntime.Domain.Worktrees;
using Hypa.AgentRuntime.Infrastructure.Plugins;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class PluginHostTests : IDisposable
{
    private readonly string _root;
    private readonly MemoryPluginFiles _files;
    private readonly FakePluginClock _clock;
    private readonly RecordingLauncher _launcher;
    private readonly PluginProcessRegistry _processRegistry;
    private readonly PluginHostService _host;

    public PluginHostTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hypa-plugin-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _files = new MemoryPluginFiles();
        _clock = new FakePluginClock();
        _launcher = new RecordingLauncher();
        _processRegistry = new PluginProcessRegistry();
        var paths = new SystemPluginPathRoots(_root);
        _host = new PluginHostService(
            _files,
            _clock,
            paths,
            new PluginManifestParser(),
            new FilePluginRegistry(_files, paths),
            _launcher,
            processRegistry: _processRegistry);
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
    public void Link_list_enable_disable_unlink_persist()
    {
        var dir = WritePlugin("example.persist", manifest: DefaultManifest("example.persist"));
        var linked = _host.Link(dir, enabled: true);
        Assert.True(linked.IsOk, linked.IsOk ? "" : linked.Error.Message);
        Assert.Equal("example.persist", linked.Value.Plugin.PluginId);
        Assert.True(linked.Value.Plugin.Enabled);

        var listed = _host.List(null);
        Assert.True(listed.IsOk);
        Assert.Contains(listed.Value, p => p.PluginId == "example.persist");

        var disabled = _host.SetEnabled("example.persist", false);
        Assert.True(disabled.IsOk);
        Assert.False(disabled.Value.Plugin.Enabled);

        var enabled = _host.SetEnabled("example.persist", true);
        Assert.True(enabled.IsOk);
        Assert.True(enabled.Value.Plugin.Enabled);

        var unlinked = _host.Unlink("example.persist");
        Assert.True(unlinked.IsOk);
        Assert.True(unlinked.Value.Removed);
        Assert.Empty(_host.List(null).Value);
    }

    [Fact]
    public void Stale_registry_row_without_resources_lists_and_declares_empty()
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

        var listed = _host.List(null);
        Assert.True(listed.IsOk, listed.IsOk ? "" : listed.Error.Message);
        var stale = Assert.Single(listed.Value, p => p.PluginId == "example.stale");
        Assert.NotNull(stale.Resources);
        Assert.Empty(stale.Resources);
        Assert.Empty(_host.DeclaredResources("example.stale"));
        _host.RunStartupHooks();
    }

    [Fact]
    public void Corrupt_registry_does_not_throw_and_lists_empty()
    {
        var paths = new SystemPluginPathRoots(_root);
        _files.WriteAllText(paths.RegistryPath, "{not-json");

        var listed = _host.List(null);
        Assert.True(listed.IsOk, listed.IsOk ? "" : listed.Error.Message);
        Assert.Empty(listed.Value);
        Assert.Empty(_host.DeclaredResources("example.gone"));
        _host.RunStartupHooks();
    }

    [Fact]
    public void Missing_min_hypa_version_fails_closed()
    {
        var dir = WritePlugin("example.no-min", """
            id = "example.no-min"
            name = "No Min"
            version = "0.1.0"
            """);
        var result = _host.Link(dir, true);
        Assert.False(result.IsOk);
        Assert.Equal(PluginError.InvalidMinVersion, result.Error.Code);
    }

    [Fact]
    public void Newer_min_hypa_version_fails_closed()
    {
        var dir = WritePlugin("example.newer", DefaultManifest("example.newer", min: "99.0.0"));
        var result = _host.Link(dir, true);
        Assert.False(result.IsOk);
        Assert.Equal(PluginError.RequiresNewer, result.Error.Code);
    }

    [Fact]
    public void Unknown_event_hook_fails_at_link()
    {
        var dir = WritePlugin("example.bad-hook", DefaultManifest("example.bad-hook") + """

            [[events]]
            on = "not.a.hook"
            command = ["/bin/echo", "x"]
            """);
        var result = _host.Link(dir, true);
        Assert.False(result.IsOk);
        Assert.Equal(PluginError.UnknownEvent, result.Error.Code);
    }

    [Fact]
    public void Terminal_output_and_render_hooks_are_refused()
    {
        foreach (var hook in new[] { "terminal.output", "terminal.render" })
        {
            var id = "example." + hook.Replace('.', '-');
            var dir = WritePlugin(id, DefaultManifest(id) + $"""

                [[events]]
                on = "{hook}"
                command = ["/bin/echo", "x"]
                """);
            var result = _host.Link(dir, true);
            Assert.False(result.IsOk);
            Assert.Equal(PluginError.UnknownEvent, result.Error.Code);
        }
    }

    [Fact]
    public void Unknown_filter_key_fails_closed()
    {
        var dir = WritePlugin("example.bad-filter", DefaultManifest("example.bad-filter") + """

            [[events]]
            on = "pane.created"
            command = ["/bin/echo", "x"]
            filter.unknown = ["nope"]
            """);
        var result = _host.Link(dir, true);
        Assert.False(result.IsOk);
        Assert.Equal(PluginError.UnknownFilter, result.Error.Code);
    }

    [Fact]
    public void Powershell_command_is_refused()
    {
        var dir = WritePlugin("example.ps1", DefaultManifest("example.ps1") + """

            [[actions]]
            id = "win"
            title = "Win"
            command = ["run.ps1"]
            """);
        var result = _host.Link(dir, true);
        Assert.False(result.IsOk);
        Assert.Equal(PluginError.UnsupportedCommand, result.Error.Code);
    }

    [Fact]
    public void Unsupported_manifest_tables_fail_closed()
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
            Assert.DoesNotContain(_host.List(null).Value, p => p.PluginId == id);
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
            Assert.DoesNotContain(_host.List(null).Value, p => p.PluginId == id);
        }

        var supported = WritePlugin("example.supported", DefaultManifest("example.supported") + """

            [[build]]
            command = ["/bin/true"]

            [[startup]]
            command = ["/bin/echo", "boot"]

            [[actions]]
            id = "ping"
            title = "Ping"
            command = ["/bin/echo", "ok"]

            [[events]]
            on = "pane.created"
            command = ["/bin/echo", "hook"]

            [[panes]]
            id = "board"
            title = "Board"
            command = ["/bin/echo", "board"]

            [[link_handlers]]
            id = "github-issue"
            title = "Open GitHub issue"
            pattern = "^https://github\\.com/"
            action = "ping"

            [[resources]]
            id = "queue"
            kind = "collection"
            projection = "hypa.projection.collection.v1"
            title = "Queue"

            [[menu_items]]
            id = "plan"
            title = "Plan"
            contexts = ["collection_item"]
            action = "ping"

            [[settings.field]]
            key = "theme"
            type = "string"
            title = "Theme"
            default = "dark"

            [[doctor]]
            id = "store"
            label = "notes"
            command = ["/bin/echo", "ok"]
            """);
        var linked = _host.Link(supported, true);
        Assert.True(linked.IsOk, linked.IsOk ? "" : linked.Error.Message);
        Assert.Contains(_host.List(null).Value, p => p.PluginId == "example.supported");
        var installed = Assert.Single(_host.List(null).Value, p => p.PluginId == "example.supported");
        var handlers = installed.LinkHandlers;
        var handler = Assert.Single(handlers);
        Assert.Equal("github-issue", handler.Id);
        Assert.Equal("ping", handler.Action);
        var resource = Assert.Single(installed.Resources);
        Assert.Equal("queue", resource.Id);
        Assert.Equal(PluginResourceLimits.KindCollection, resource.Kind);
        Assert.Equal(PluginResourceLimits.CollectionSchema, resource.Projection);
        var menuItem = Assert.Single(installed.MenuItems);
        Assert.Equal("plan", menuItem.Id);
        Assert.Equal("ping", menuItem.Action);
        var field = Assert.Single(installed.SettingsFields);
        Assert.Equal("theme", field.Key);
        Assert.Equal("string", field.Type);
        var doctor = Assert.Single(installed.Doctor);
        Assert.Equal("store", doctor.Id);
        Assert.Equal("notes", doctor.Label);
        Assert.Equal(["/bin/echo", "ok"], doctor.Command);
        Assert.True(_host.HasAction("example.supported", "ping"));
    }

    [Fact]
    public void Doctor_array_table_links()
    {
        var dir = WritePlugin("example.doctor", DefaultManifest("example.doctor") + """

            [[doctor]]
            id = "store"
            label = "annotations"
            command = ["./bin/hypa-annotate.exe", "doctor"]
            """);
        var result = _host.Link(dir, true);
        Assert.True(result.IsOk, result.IsOk ? "" : result.Error.Message);
        var plugin = Assert.Single(_host.List(null).Value, p => p.PluginId == "example.doctor");
        var spec = Assert.Single(plugin.Doctor);
        Assert.Equal("store", spec.Id);
        Assert.Equal("annotations", spec.Label);
        Assert.Equal(["./bin/hypa-annotate.exe", "doctor"], spec.Command);
    }

    [Fact]
    public void Duplicate_doctor_id_fails_closed()
    {
        var dir = WritePlugin("example.dup-doctor", DefaultManifest("example.dup-doctor") + """

            [[doctor]]
            id = "store"
            label = "one"
            command = ["/bin/echo", "a"]

            [[doctor]]
            id = "store"
            label = "two"
            command = ["/bin/echo", "b"]
            """);
        var result = _host.Link(dir, true);
        Assert.False(result.IsOk);
        Assert.Equal(PluginError.DuplicateDoctor, result.Error.Code);
        Assert.DoesNotContain(_host.List(null).Value, p => p.PluginId == "example.dup-doctor");
    }

    [Fact]
    public void Duplicate_link_handler_id_fails_closed()
    {
        var dir = WritePlugin("example.dup-link", DefaultManifest("example.dup-link") + """

            [[actions]]
            id = "open"
            title = "Open"
            command = ["/bin/echo", "ok"]

            [[link_handlers]]
            id = "github-issue"
            title = "One"
            pattern = "^https://github\\.com/"
            action = "open"

            [[link_handlers]]
            id = "github-issue"
            title = "Two"
            pattern = "^https://example\\.com/"
            action = "open"
            """);
        var result = _host.Link(dir, true);
        Assert.False(result.IsOk);
        Assert.Equal(PluginError.DuplicateLinkHandler, result.Error.Code);
    }

    [Fact]
    public void Link_handler_missing_action_fails_closed()
    {
        var dir = WritePlugin("example.missing-action", DefaultManifest("example.missing-action") + """

            [[actions]]
            id = "open"
            title = "Open"
            command = ["/bin/echo", "ok"]

            [[link_handlers]]
            id = "github-issue"
            title = "Open GitHub issue"
            pattern = "^https://github\\.com/"
            action = "missing"
            """);
        var result = _host.Link(dir, true);
        Assert.False(result.IsOk);
        Assert.Equal(PluginError.InvalidLinkHandlerAction, result.Error.Code);
    }

    [Fact]
    public void Invalid_link_handler_pattern_fails_closed()
    {
        var dir = WritePlugin("example.bad-pattern", DefaultManifest("example.bad-pattern") + """

            [[actions]]
            id = "open"
            title = "Open"
            command = ["/bin/echo", "ok"]

            [[link_handlers]]
            id = "bad"
            title = "Bad"
            pattern = "["
            action = "open"
            """);
        var result = _host.Link(dir, true);
        Assert.False(result.IsOk);
        Assert.Equal(PluginError.InvalidLinkHandlerPattern, result.Error.Code);
    }

    [Fact]
    public void Link_handlers_keep_manifest_order_for_overlapping_patterns()
    {
        var dir = WritePlugin("example.link-order", DefaultManifest("example.link-order") + """

            [[actions]]
            id = "specific"
            title = "Specific"
            command = ["/bin/echo", "specific"]

            [[actions]]
            id = "generic"
            title = "Generic"
            command = ["/bin/echo", "generic"]

            [[link_handlers]]
            id = "z-specific"
            title = "Specific GitHub issue"
            pattern = "^https://github\\.com/[^/]+/[^/]+/issues/[0-9]+$"
            action = "specific"

            [[link_handlers]]
            id = "a-generic"
            title = "Generic GitHub"
            pattern = "^https://github\\.com/"
            action = "generic"
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var handled = _host.ActivateLink("https://github.com/org/repo/issues/398", null);
        Assert.True(handled.IsOk);
        Assert.True(handled.Value);
        var start = Assert.Single(_launcher.Starts);
        Assert.Equal("specific", start.Environment[PluginEnv.ActionId]);
        Assert.Equal("z-specific", start.Environment[PluginEnv.LinkHandlerId]);
    }

    [Fact]
    public void Unmatched_url_returns_unhandled_and_starts_no_process()
    {
        var dir = WritePlugin("example.nomatch", DefaultManifest("example.nomatch") + LinkHandlerManifest());
        Assert.True(_host.Link(dir, true).IsOk);
        var handled = _host.ActivateLink("https://example.test/not-github", null);
        Assert.True(handled.IsOk);
        Assert.False(handled.Value);
        Assert.Empty(_launcher.Starts);
    }

    [Fact]
    public void Disabled_plugin_link_handler_does_not_match()
    {
        var dir = WritePlugin("example.disabled-link", DefaultManifest("example.disabled-link") + LinkHandlerManifest());
        Assert.True(_host.Link(dir, enabled: false).IsOk);
        var handled = _host.ActivateLink("https://github.com/org/repo/issues/1", null);
        Assert.True(handled.IsOk);
        Assert.False(handled.Value);
        Assert.Empty(_launcher.Starts);
    }

    [Fact]
    public void Unsupported_platform_link_handler_does_not_match()
    {
        var foreign = PluginIdentifiers.CurrentPlatform() == "windows" ? "linux" : "windows";
        var dir = WritePlugin("example.plat-link", DefaultManifest("example.plat-link") + $"""

            [[actions]]
            id = "open"
            title = "Open"
            command = ["/bin/echo", "ok"]

            [[link_handlers]]
            id = "github-issue"
            title = "Open GitHub issue"
            pattern = "^https://github\\.com/"
            action = "open"
            platforms = ["{foreign}"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var handled = _host.ActivateLink("https://github.com/org/repo/issues/1", null);
        Assert.True(handled.IsOk);
        Assert.False(handled.Value);
        Assert.Empty(_launcher.Starts);
    }

    [Fact]
    public void Activate_link_injects_hypa_clicked_url_env()
    {
        var dir = WritePlugin("example.links", DefaultManifest("example.links") + LinkHandlerManifest());
        Assert.True(_host.Link(dir, true).IsOk);
        var url = "https://github.com/org/repo/issues/1";
        var handled = _host.ActivateLink(url, null);
        Assert.True(handled.IsOk, handled.IsOk ? "" : handled.Error.Message);
        Assert.True(handled.Value);
        var start = Assert.Single(_launcher.Starts);
        Assert.Equal("github-issue", start.Environment[PluginEnv.LinkHandlerId]);
        Assert.Equal(url, start.Environment[PluginEnv.ClickedUrl]);
        Assert.DoesNotContain(start.Environment.Keys, k => k.StartsWith("HERDR_", StringComparison.Ordinal));
        var json = start.Environment[PluginEnv.ContextJson];
        Assert.Contains("\"invocation_source\":\"link_click\"", json, StringComparison.Ordinal);
        Assert.Contains("\"clicked_url\":", json, StringComparison.Ordinal);
        Assert.Contains("\"link_handler_id\":\"github-issue\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("selected_text", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_grant_fails_closed()
    {
        var dir = WritePlugin("example.grant", DefaultManifest("example.grant") + """

            [grants]
            request = ["not.a.grant"]
            """);
        var result = _host.Link(dir, true);
        Assert.False(result.IsOk);
        Assert.Equal(PluginError.UnknownGrant, result.Error.Code);
    }

    [Fact]
    public void Trust_preview_lists_commands_and_grants()
    {
        var dir = WritePlugin("example.preview", DefaultManifest("example.preview") + """

            [grants]
            request = ["pane.open:self", "action.invoke:self"]

            [[actions]]
            id = "ping"
            title = "Ping"
            command = ["/bin/echo", "ok"]
            """);
        var result = _host.Link(dir, true);
        Assert.True(result.IsOk, result.IsOk ? "" : result.Error.Message);
        Assert.Contains(result.Value.TrustPreview.Grants, g => g == "pane.open:self");
        Assert.Contains(result.Value.TrustPreview.Commands, c => c.Count >= 1 && c[0] == "/bin/echo");
    }

    [Fact]
    public void Send_text_grant_parses_at_link_and_appears_in_trust_preview()
    {
        Assert.True(PluginGrantCatalog.IsKnown(PluginGrantCatalog.PaneSendTextSelf));
        Assert.Contains(PluginGrantCatalog.Advertised, g => g == PluginGrantCatalog.PaneSendTextSelf);
        Assert.True(PluginGrantCatalog.IsDispatchAllowed(ProtocolMethods.PluginPaneSendText));
        Assert.False(PluginGrantCatalog.IsDispatchAllowed(ProtocolMethods.PaneSendText));

        var dir = WritePlugin("example.send-preview", DefaultManifest("example.send-preview") + """

            [grants]
            request = ["pane.send_text:self"]
            """);
        var result = _host.Link(dir, true);
        Assert.True(result.IsOk, result.IsOk ? "" : result.Error.Message);
        Assert.Contains(result.Value.TrustPreview.Grants, g => g == PluginGrantCatalog.PaneSendTextSelf);
    }

    [Fact]
    public void Action_invoke_runs_argv_and_injects_hypa_env()
    {
        var dir = WritePlugin("example.action", DefaultManifest("example.action") + """

            [grants]
            request = ["action.invoke:self"]

            [[actions]]
            id = "ping"
            title = "Ping"
            command = ["/bin/echo", "ok"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var invoked = _host.InvokeAction("ping", "example.action", null);
        Assert.True(invoked.IsOk, invoked.IsOk ? "" : invoked.Error.Message);
        var start = Assert.Single(_launcher.Starts);
        Assert.Equal("/bin/echo", start.Program);
        Assert.Equal(["ok"], start.Arguments);
        Assert.Contains(PluginEnv.PluginId, start.Environment.Keys);
        Assert.Equal("example.action", start.Environment[PluginEnv.PluginId]);
        Assert.Contains(PluginEnv.GrantToken, start.Environment.Keys);
        Assert.DoesNotContain(start.Environment.Keys, k => k.StartsWith("HERDR_", StringComparison.Ordinal));
        Assert.False(start.Environment.ContainsKey("selected_text"));
    }

    [Fact]
    public void Structured_action_result_parses_last_json_line()
    {
        _launcher.Exit = new PluginProcessExit(0, "noise\n{\"status\":\"ok\",\"message\":\"hi\",\"data\":{\"n\":1}}\n", "", null);
        var dir = WritePlugin("example.json", DefaultManifest("example.json") + """

            [[actions]]
            id = "ping"
            title = "Ping"
            command = ["/bin/echo", "ok"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        Assert.True(_host.InvokeAction("ping", "example.json", null).IsOk);
        var logs = _host.ListLogs("example.json", 10);
        Assert.True(logs.IsOk);
        var log = Assert.Single(logs.Value);
        Assert.Equal("succeeded", log.Status);
        Assert.NotNull(log.Result);
        Assert.Equal("ok", log.Result!.Status);
        Assert.Equal("hi", log.Result.Message);
    }

    [Fact]
    public void Malformed_action_json_falls_back_to_exit_code()
    {
        _launcher.Exit = new PluginProcessExit(0, "not-json\n", "", null);
        var dir = WritePlugin("example.plain", DefaultManifest("example.plain") + """

            [[actions]]
            id = "ping"
            title = "Ping"
            command = ["/bin/echo", "ok"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        Assert.True(_host.InvokeAction("ping", "example.plain", null).IsOk);
        var log = Assert.Single(_host.ListLogs("example.plain", 10).Value);
        Assert.Equal(0, log.ExitCode);
        Assert.Null(log.Result);
    }

    [Fact]
    public void Event_filter_skips_process_launch()
    {
        var dir = WritePlugin("example.filter", DefaultManifest("example.filter") + """

            [[events]]
            on = "pane.created"
            command = ["/bin/echo", "hook"]
            filter.workspace = ["w-keep"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        _host.HandleRuntimeEvent("pane.lifecycle", """{"pane_id":"p1","state":"created","workspace_id":"w-other"}""");
        Assert.Empty(_launcher.Starts);
        _host.HandleRuntimeEvent("pane.lifecycle", """{"pane_id":"p1","state":"created","workspace_id":"w-keep"}""");
        Assert.Single(_launcher.Starts);
    }

    [Fact]
    public void Event_without_subscriber_does_not_query_context()
    {
        var recorder = new RecordingContextSource();
        var host = new PluginHostService(
            _files,
            _clock,
            new SystemPluginPathRoots(_root),
            new PluginManifestParser(),
            new FilePluginRegistry(_files, new SystemPluginPathRoots(_root)),
            _launcher,
            recorder,
            processRegistry: _processRegistry);
        host.HandleRuntimeEvent(
            "pane.lifecycle",
            """{"pane_id":"p1","state":"created","workspace_id":"w-keep"}""");
        Assert.Equal(0, recorder.ForEventCalls);
    }

    [Fact]
    public void Event_with_subscriber_queries_context_once()
    {
        var recorder = new RecordingContextSource();
        var host = new PluginHostService(
            _files,
            _clock,
            new SystemPluginPathRoots(_root),
            new PluginManifestParser(),
            new FilePluginRegistry(_files, new SystemPluginPathRoots(_root)),
            _launcher,
            recorder,
            processRegistry: _processRegistry);
        var dir = WritePlugin("example.ctx", DefaultManifest("example.ctx") + """

            [[events]]
            on = "pane.created"
            command = ["/bin/echo", "hook"]
            """);
        Assert.True(host.Link(dir, true).IsOk);
        host.HandleRuntimeEvent(
            "pane.lifecycle",
            """{"pane_id":"p1","state":"created","workspace_id":"w-keep"}""");
        Assert.Equal(1, recorder.ForEventCalls);
        Assert.Single(_launcher.Starts);
    }

    [Fact]
    public void Startup_hook_is_one_shot_and_does_not_wait()
    {
        _launcher.CompleteSynchronously = false;
        var dir = WritePlugin("example.start", DefaultManifest("example.start") + """

            [[startup]]
            command = ["/bin/echo", "boot"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        _host.RunStartupHooks();
        Assert.Single(_launcher.Starts);
        Assert.Equal("startup", _launcher.Starts[0].Environment[PluginEnv.Event]);
    }

    [Fact]
    public void Grant_token_missing_expired_and_over_scoped_fail_closed()
    {
        var dir = WritePlugin("example.token", DefaultManifest("example.token") + """

            [grants]
            request = ["action.invoke:self"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var token = _host.PeekGrantToken("example.token");
        Assert.False(string.IsNullOrWhiteSpace(token));

        var missing = _host.CheckGrant("nope", ProtocolMethods.PluginActionInvoke, null, "example.token");
        Assert.False(missing.Allowed);
        Assert.Equal(PluginError.CapabilityMissing, missing.ErrorCode);

        var over = _host.CheckGrant(token, ProtocolMethods.IntegrationInstall, null, null);
        Assert.False(over.Allowed);

        var official = _host.CheckGrant(token, ProtocolMethods.PaneReportAgent, "hypa:claude", null);
        Assert.False(official.Allowed);

        var other = _host.CheckGrant(token, ProtocolMethods.PaneReportAgent, "plugin:other", null);
        Assert.False(other.Allowed);

        var self = _host.CheckGrant(token, ProtocolMethods.PaneReportAgent, "plugin:example.token", null);
        Assert.True(self.Allowed);

        _clock.UtcNow = _clock.UtcNow.AddHours(13);
        var expired = _host.CheckGrant(token, ProtocolMethods.PaneReportAgent, "plugin:example.token", null);
        Assert.False(expired.Allowed);
    }

    [Fact]
    public void Plugin_token_cannot_call_host_control_methods()
    {
        var dir = WritePlugin("example.scope", DefaultManifest("example.scope") + """

            [grants]
            request = ["action.invoke:self", "pane.open:self"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var token = _host.PeekGrantToken("example.scope");
        Assert.False(string.IsNullOrWhiteSpace(token));

        Assert.False(_host.CheckGrant(token, ProtocolMethods.ServerStop, null, null).Allowed);
        Assert.False(_host.CheckGrant(token, ProtocolMethods.WorkspaceClose, null, null).Allowed);
        Assert.False(_host.CheckGrant(token, ProtocolMethods.PaneSendInput, null, null).Allowed);
        Assert.False(_host.CheckGrant(token, ProtocolMethods.PaneSendText, null, null).Allowed);
        Assert.False(_host.CheckGrant(token, ProtocolMethods.PaneLinkActivate, null, null).Allowed);
        Assert.False(_host.CheckGrant(token, ProtocolMethods.PluginPaneSendText, null, "example.scope").Allowed);
        Assert.False(_host.CheckGrant(token, ProtocolMethods.PluginLink, null, "example.scope").Allowed);
        Assert.False(_host.CheckGrant(token, ProtocolMethods.PluginActionList, null, "other.plugin").Allowed);
        Assert.True(_host.CheckGrant(token, ProtocolMethods.PluginActionList, null, "example.scope").Allowed);
    }

    [Fact]
    public void Explicit_foreign_manifest_filename_is_refused()
    {
        var dir = Path.Combine(_root, "example.herdr");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "herdr-plugin.toml");
        _files.WriteAllText(path, DefaultManifest("example.herdr"));
        var result = _host.Link(path, true);
        Assert.False(result.IsOk);
        Assert.Equal(PluginError.InvalidParams, result.Error.Code);
    }

    [Fact]
    public void User_cli_without_token_is_not_restricted()
    {
        var open = _host.CheckGrant(null, ProtocolMethods.IntegrationInstall, null, null);
        Assert.True(open.Allowed);
    }

    [Fact]
    public void Plugin_source_field_without_token_is_human_authority_identity()
    {
        var authority = _host.CheckGrant(
            null,
            ProtocolMethods.PaneReportAgent,
            "plugin:claude",
            null);
        Assert.True(authority.Allowed);

        var install = _host.CheckGrant(
            null,
            ProtocolMethods.IntegrationInstall,
            "plugin:example.attrib",
            null);
        Assert.True(install.Allowed);
    }

    [Fact]
    public void Tokenless_plugin_connection_cannot_call_host_or_authority()
    {
        var install = _host.CheckGrant(
            null,
            ProtocolMethods.IntegrationInstall,
            null,
            null,
            pluginConnection: true);
        Assert.False(install.Allowed);
        Assert.Equal(PluginError.CapabilityMissing, install.ErrorCode);

        var authority = _host.CheckGrant(
            null,
            ProtocolMethods.PaneReportAgent,
            "plugin:claude",
            null,
            pluginConnection: true);
        Assert.False(authority.Allowed);
        Assert.Equal(PluginError.CapabilityMissing, authority.ErrorCode);
    }

    [Fact]
    public void Tokenless_live_plugin_peer_connection_cannot_integration_install()
    {
        const int pluginPeerPid = 88001;
        _processRegistry.Register(pluginPeerPid);
        Assert.True(_host.IsLivePluginProcess(pluginPeerPid));

        var denied = _host.CheckGrant(
            null,
            ProtocolMethods.IntegrationInstall,
            null,
            null,
            pluginConnection: _host.IsLivePluginProcess(pluginPeerPid));
        Assert.False(denied.Allowed);
        Assert.Equal(PluginError.CapabilityMissing, denied.ErrorCode);

        _processRegistry.Unregister(pluginPeerPid);
        Assert.False(_host.IsLivePluginProcess(pluginPeerPid));
    }

    [Fact]
    public void Plugin_cannot_focus_another_plugins_pane_with_own_token()
    {
        var dirA = WritePlugin("plugin.a", DefaultManifest("plugin.a") + """

            [grants]
            request = ["pane.open:self"]
            """);
        var dirB = WritePlugin("plugin.b", DefaultManifest("plugin.b") + """

            [grants]
            request = ["pane.open:self"]
            """);
        Assert.True(_host.Link(dirA, true).IsOk);
        Assert.True(_host.Link(dirB, true).IsOk);
        _host.NoteOwnedPane("plugin.b", "board", "pane_b");
        var tokenA = _host.PeekGrantToken("plugin.a");
        Assert.False(string.IsNullOrWhiteSpace(tokenA));

        var denied = _host.CheckGrant(tokenA, ProtocolMethods.PluginPaneFocus, null, "plugin.b");
        Assert.False(denied.Allowed);
        Assert.Equal(PluginError.CapabilityMissing, denied.ErrorCode);
    }

    [Fact]
    public void Plugin_cannot_close_another_plugins_pane_with_own_token()
    {
        var dirA = WritePlugin("plugin.a", DefaultManifest("plugin.a") + """

            [grants]
            request = ["pane.open:self"]
            """);
        var dirB = WritePlugin("plugin.b", DefaultManifest("plugin.b") + """

            [grants]
            request = ["pane.open:self"]
            """);
        Assert.True(_host.Link(dirA, true).IsOk);
        Assert.True(_host.Link(dirB, true).IsOk);
        _host.NoteOwnedPane("plugin.b", "board", "pane_b");
        var tokenA = _host.PeekGrantToken("plugin.a");
        Assert.False(string.IsNullOrWhiteSpace(tokenA));

        var denied = _host.CheckGrant(tokenA, ProtocolMethods.PluginPaneClose, null, "plugin.b");
        Assert.False(denied.Allowed);
        Assert.Equal(PluginError.CapabilityMissing, denied.ErrorCode);
    }

    [Fact]
    public void Plugin_cannot_invoke_other_plugins_action_by_omitting_plugin_id()
    {
        var dirA = WritePlugin("plugin.a", DefaultManifest("plugin.a") + """

            [grants]
            request = ["action.invoke:self"]
            """);
        var dirB = WritePlugin("plugin.b", DefaultManifest("plugin.b") + """

            [[actions]]
            id = "deploy"
            title = "Deploy"
            command = ["/bin/echo", "deploy"]
            """);
        Assert.True(_host.Link(dirA, true).IsOk);
        Assert.True(_host.Link(dirB, true).IsOk);
        var tokenA = _host.PeekGrantToken("plugin.a");
        Assert.False(string.IsNullOrWhiteSpace(tokenA));

        var owner = _host.ResolveActionOwnerPluginId("deploy", null);
        Assert.Equal("plugin.b", owner);

        var denied = _host.CheckGrant(tokenA, ProtocolMethods.PluginActionInvoke, null, owner);
        Assert.False(denied.Allowed);
        Assert.Equal(PluginError.CapabilityMissing, denied.ErrorCode);
    }

    [Fact]
    public void Send_text_token_without_grant_is_refused()
    {
        var dir = WritePlugin("example.no-send", DefaultManifest("example.no-send") + """

            [grants]
            request = ["pane.open:self"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var token = _host.PeekGrantToken("example.no-send");
        Assert.False(string.IsNullOrWhiteSpace(token));

        var denied = _host.CheckGrant(token, ProtocolMethods.PluginPaneSendText, null, "example.no-send");
        Assert.False(denied.Allowed);
        Assert.Equal(PluginError.CapabilityMissing, denied.ErrorCode);
        Assert.False(_host.CheckGrant(null, ProtocolMethods.PluginPaneSendText, null, "example.no-send").Allowed);
    }

    [Fact]
    public void Send_text_token_with_grant_allows_self_and_refuses_other_plugin()
    {
        var dir = WritePlugin("example.send-self", DefaultManifest("example.send-self") + """

            [grants]
            request = ["pane.send_text:self"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var token = _host.PeekGrantToken("example.send-self");
        Assert.False(string.IsNullOrWhiteSpace(token));

        var allowed = _host.CheckGrant(token, ProtocolMethods.PluginPaneSendText, null, "example.send-self");
        Assert.True(allowed.Allowed);
        Assert.False(_host.CheckGrant(token, ProtocolMethods.PluginPaneSendText, null, "other.plugin").Allowed);
        Assert.False(_host.CheckGrant(token, ProtocolMethods.PaneSendText, null, "example.send-self").Allowed);
    }

    [Fact]
    public async Task Send_text_writes_to_owned_pane_with_newline()
    {
        var dir = WritePlugin("example.send-owned", DefaultManifest("example.send-owned") + """

            [grants]
            request = ["pane.send_text:self"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var token = _host.PeekGrantToken("example.send-owned");
        var factory = TestPaneFactories.Capturing();
        var (cp, _) = CreatePluginControlPlane(factory);
        try
        {
            var paneId = await CreateWorkspacePaneAsync(cp);
            _host.NoteOwnedPane("example.send-owned", "board", paneId);
            var before = factory.Writes.Count;
            var result = await cp.DispatchAsync(
                ProtocolMethods.PluginPaneSendText,
                SendTextParams(paneId, "review note"),
                connection: null,
                grantToken: token,
                pluginConnection: true,
                CancellationToken.None);
            Assert.True(result.GetProperty("ok").GetBoolean());
            Assert.Equal(paneId, result.GetProperty("pane_id").GetString());
            Assert.Equal(before + 1, factory.Writes.Count);
            Assert.Equal("review note\n", factory.Writes[^1]);
            var log = Assert.Single(_host.ListLogs("example.send-owned", 10).Value);
            Assert.Equal("succeeded", log.Status);
            Assert.Equal(ProtocolMethods.PluginPaneSendText, log.Event);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Send_text_writes_to_recorded_delivery_target()
    {
        var dir = WritePlugin("example.send-target", DefaultManifest("example.send-target") + """

            [grants]
            request = ["pane.send_text:self"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var token = _host.PeekGrantToken("example.send-target");
        var factory = TestPaneFactories.Capturing();
        var (cp, _) = CreatePluginControlPlane(factory);
        try
        {
            var targetId = await CreateWorkspacePaneAsync(cp);
            var otherId = await CreateWorkspacePaneAsync(cp);
            _host.NoteDeliveryTarget("example.send-target", targetId);
            var before = factory.Writes.Count;
            var result = await cp.DispatchAsync(
                ProtocolMethods.PluginPaneSendText,
                SendTextParams(targetId, "from review"),
                connection: null,
                grantToken: token,
                pluginConnection: true,
                CancellationToken.None);
            Assert.True(result.GetProperty("ok").GetBoolean());
            Assert.Equal(before + 1, factory.Writes.Count);
            Assert.Equal("from review\n", factory.Writes[^1]);

            var refused = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PluginPaneSendText,
                    SendTextParams(otherId, "nope"),
                    connection: null,
                    grantToken: token,
                    pluginConnection: true,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrors.CapabilityMissing, refused.Message);
            Assert.Equal(before + 1, factory.Writes.Count);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Send_text_keeps_existing_newline()
    {
        var dir = WritePlugin("example.send-nl", DefaultManifest("example.send-nl") + """

            [grants]
            request = ["pane.send_text:self"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var token = _host.PeekGrantToken("example.send-nl");
        var factory = TestPaneFactories.Capturing();
        var (cp, _) = CreatePluginControlPlane(factory);
        try
        {
            var paneId = await CreateWorkspacePaneAsync(cp);
            _host.NoteOwnedPane("example.send-nl", "board", paneId);
            await cp.DispatchAsync(
                ProtocolMethods.PluginPaneSendText,
                SendTextParams(paneId, "line\n"),
                connection: null,
                grantToken: token,
                pluginConnection: true,
                CancellationToken.None);
            Assert.Equal("line\n", factory.Writes[^1]);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Send_text_records_focused_pane_from_action_invoke()
    {
        var dir = WritePlugin("example.send-action", DefaultManifest("example.send-action") + """

            [grants]
            request = ["action.invoke:self", "pane.send_text:self"]

            [[actions]]
            id = "review"
            title = "Review"
            command = ["/bin/echo", "ok"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var token = _host.PeekGrantToken("example.send-action");
        var factory = TestPaneFactories.Capturing();
        var (cp, _) = CreatePluginControlPlane(factory);
        try
        {
            var paneId = await CreateWorkspacePaneAsync(cp);
            var invoked = _host.InvokeAction(
                "review",
                "example.send-action",
                new PluginInvocationContext { FocusedPaneId = paneId });
            Assert.True(invoked.IsOk, invoked.IsOk ? "" : invoked.Error.Message);
            var before = factory.Writes.Count;
            var result = await cp.DispatchAsync(
                ProtocolMethods.PluginPaneSendText,
                SendTextParams(paneId, "action target"),
                connection: null,
                grantToken: token,
                pluginConnection: true,
                CancellationToken.None);
            Assert.True(result.GetProperty("ok").GetBoolean());
            Assert.Equal(before + 1, factory.Writes.Count);
            Assert.Equal("action target\n", factory.Writes[^1]);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Send_text_records_open_target_pane()
    {
        var dir = WritePlugin("example.send-open", DefaultManifest("example.send-open") + """

            [grants]
            request = ["pane.open:self", "pane.send_text:self"]

            [[panes]]
            id = "board"
            title = "Board"
            placement = "split"
            command = ["/bin/echo", "board"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var token = _host.PeekGrantToken("example.send-open");
        var factory = TestPaneFactories.Capturing();
        var (cp, _) = CreatePluginControlPlane(factory);
        try
        {
            var targetId = await CreateWorkspacePaneAsync(cp);
            await cp.DispatchAsync(
                ProtocolMethods.PluginPaneOpen,
                JsonDocument.Parse(
                    $$"""{"plugin_id":"example.send-open","entrypoint":"board","placement":"split","target_pane_id":{{JsonSerializer.Serialize(targetId)}}}""")
                    .RootElement,
                connection: null,
                grantToken: token,
                pluginConnection: true,
                CancellationToken.None);
            var before = factory.Writes.Count;
            var result = await cp.DispatchAsync(
                ProtocolMethods.PluginPaneSendText,
                SendTextParams(targetId, "open target"),
                connection: null,
                grantToken: token,
                pluginConnection: true,
                CancellationToken.None);
            Assert.True(result.GetProperty("ok").GetBoolean());
            Assert.Equal(before + 1, factory.Writes.Count);
            Assert.Equal("open target\n", factory.Writes[^1]);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Split_no_focus_keeps_the_previous_pane()
    {
        var dir = WritePlugin("example.split-nofocus", DefaultManifest("example.split-nofocus") + """

            [grants]
            request = ["pane.open:self"]

            [[panes]]
            id = "board"
            title = "Board"
            placement = "split"
            command = ["/bin/echo", "board"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var token = _host.PeekGrantToken("example.split-nofocus");
        var factory = TestPaneFactories.Capturing();
        var (cp, app) = CreatePluginControlPlane(factory);
        try
        {
            var targetId = await CreateWorkspacePaneAsync(cp);
            await cp.DispatchAsync(
                ProtocolMethods.PluginPaneOpen,
                JsonDocument.Parse(
                    $$"""{"plugin_id":"example.split-nofocus","entrypoint":"board","placement":"split","target_pane_id":{{JsonSerializer.Serialize(targetId)}},"focus":false}""")
                    .RootElement,
                connection: null,
                grantToken: token,
                pluginConnection: true,
                CancellationToken.None);
            var tab = Assert.Single(app.Snapshot().Tabs.Values);
            Assert.Equal(targetId, tab.FocusedPaneId?.Value);
            Assert.True(tab.PaneIds.Count >= 2);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Overlay_no_focus_keeps_the_previous_pane()
    {
        var dir = WritePlugin("example.overlay-nofocus", DefaultManifest("example.overlay-nofocus") + """

            [grants]
            request = ["pane.open:self"]

            [[panes]]
            id = "board"
            title = "Board"
            placement = "overlay"
            command = ["/bin/echo", "board"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var token = _host.PeekGrantToken("example.overlay-nofocus");
        var factory = TestPaneFactories.Capturing();
        var (cp, app) = CreatePluginControlPlane(factory);
        try
        {
            var targetId = await CreateWorkspacePaneAsync(cp);
            await cp.DispatchAsync(
                ProtocolMethods.PluginPaneOpen,
                JsonDocument.Parse(
                    $$"""{"plugin_id":"example.overlay-nofocus","entrypoint":"board","placement":"overlay","target_pane_id":{{JsonSerializer.Serialize(targetId)}},"focus":false}""")
                    .RootElement,
                connection: null,
                grantToken: token,
                pluginConnection: true,
                CancellationToken.None);
            var tab = Assert.Single(app.Snapshot().Tabs.Values);
            Assert.Equal(targetId, tab.FocusedPaneId?.Value);
            Assert.True(tab.PaneIds.Count >= 2);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Frozen_server_refuses_send_text()
    {
        var dir = WritePlugin("example.send-frozen", DefaultManifest("example.send-frozen") + """

            [grants]
            request = ["pane.send_text:self"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var token = _host.PeekGrantToken("example.send-frozen");
        var factory = TestPaneFactories.Capturing();
        var (cp, app) = CreatePluginControlPlane(factory);
        try
        {
            var paneId = await CreateWorkspacePaneAsync(cp);
            _host.NoteOwnedPane("example.send-frozen", "board", paneId);
            app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.FrozenReadOnly });
            var before = factory.Writes.Count;
            var refused = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PluginPaneSendText,
                    SendTextParams(paneId, "frozen"),
                    connection: null,
                    grantToken: token,
                    pluginConnection: true,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidState, refused.Code);
            Assert.Equal(before, factory.Writes.Count);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Disable_and_unlink_revoke_send_text()
    {
        var dir = WritePlugin("example.send-revoke", DefaultManifest("example.send-revoke") + """

            [grants]
            request = ["pane.send_text:self"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var token = _host.PeekGrantToken("example.send-revoke");
        var factory = TestPaneFactories.Capturing();
        var (cp, _) = CreatePluginControlPlane(factory);
        try
        {
            var paneId = await CreateWorkspacePaneAsync(cp);
            _host.NoteOwnedPane("example.send-revoke", "board", paneId);
            await cp.DispatchAsync(
                ProtocolMethods.PluginPaneSendText,
                SendTextParams(paneId, "before"),
                connection: null,
                grantToken: token,
                pluginConnection: true,
                CancellationToken.None);
            var afterWrite = factory.Writes.Count;
            Assert.True(_host.SetEnabled("example.send-revoke", false).IsOk);
            var disabled = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PluginPaneSendText,
                    SendTextParams(paneId, "after disable"),
                    connection: null,
                    grantToken: token,
                    pluginConnection: true,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrors.CapabilityMissing, disabled.Message);
            Assert.Equal(afterWrite, factory.Writes.Count);

            Assert.True(_host.SetEnabled("example.send-revoke", true).IsOk);
            _host.NoteOwnedPane("example.send-revoke", "board", paneId);
            var fresh = _host.PeekGrantToken("example.send-revoke");
            Assert.False(string.IsNullOrWhiteSpace(fresh));
            Assert.True(_host.Unlink("example.send-revoke").IsOk);
            var unlinked = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PluginPaneSendText,
                    SendTextParams(paneId, "after unlink"),
                    connection: null,
                    grantToken: fresh,
                    pluginConnection: true,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrors.CapabilityMissing, unlinked.Message);
            Assert.Equal(afterWrite, factory.Writes.Count);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Missing_and_expired_send_text_token_writes_nothing()
    {
        var dir = WritePlugin("example.send-expire", DefaultManifest("example.send-expire") + """

            [grants]
            request = ["pane.send_text:self"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var token = _host.PeekGrantToken("example.send-expire");
        var factory = TestPaneFactories.Capturing();
        var (cp, _) = CreatePluginControlPlane(factory);
        try
        {
            var paneId = await CreateWorkspacePaneAsync(cp);
            _host.NoteOwnedPane("example.send-expire", "board", paneId);
            var before = factory.Writes.Count;
            var missing = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PluginPaneSendText,
                    SendTextParams(paneId, "missing"),
                    connection: null,
                    grantToken: "nope",
                    pluginConnection: true,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrors.CapabilityMissing, missing.Message);
            Assert.Equal(before, factory.Writes.Count);

            _clock.UtcNow = _clock.UtcNow.AddHours(13);
            var expired = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PluginPaneSendText,
                    SendTextParams(paneId, "expired"),
                    connection: null,
                    grantToken: token,
                    pluginConnection: true,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrors.CapabilityMissing, expired.Message);
            Assert.Equal(before, factory.Writes.Count);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void InvokeAction_succeeds_after_registry_reload_via_manifest_file_path()
    {
        var dir = WritePlugin("example.reload", DefaultManifest("example.reload") + """

            [[actions]]
            id = "ping"
            title = "Ping"
            command = ["/bin/echo", "ok"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var plugin = Assert.Single(_host.List(null).Value);
        Assert.EndsWith(PluginHostService.ManifestFileName, plugin.ManifestPath);
        Assert.True(_files.FileExists(plugin.ManifestPath));
        Assert.False(_files.DirectoryExists(plugin.ManifestPath));

        var invoked = _host.InvokeAction("ping", plugin.PluginId, null);
        Assert.True(invoked.IsOk, invoked.IsOk ? "" : invoked.Error.Message);
        Assert.Single(_launcher.Starts);
    }

    [Fact]
    public void Concurrent_command_caps_match_host_limits()
    {
        _launcher.CompleteSynchronously = false;
        var dir = WritePlugin("example.cap", DefaultManifest("example.cap") + """

            [[actions]]
            id = "ping"
            title = "Ping"
            command = ["/bin/echo", "ok"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        for (var i = 0; i < PluginCommandLimits.MaxPerPluginInFlight; i++)
            Assert.True(_host.InvokeAction("ping", "example.cap", null).IsOk);
        var blocked = _host.InvokeAction("ping", "example.cap", null);
        Assert.False(blocked.IsOk);
        Assert.Equal(PluginError.CommandLimit, blocked.Error.Code);
    }

    [Fact]
    public void Context_json_omits_selected_text_and_caps()
    {
        var json = PluginContextJson.Serialize(new PluginInvocationContext
        {
            WorkspaceId = "w1",
            SelectedText = "SECRET",
            ProgramName = "pi",
            CorrelationId = "c1",
        });
        Assert.True(json.IsOk);
        Assert.DoesNotContain("SECRET", json.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("selected_text", json.Value, StringComparison.Ordinal);
        Assert.Contains("program_name", json.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_json_writes_clicked_url_and_handler_id()
    {
        var json = PluginContextJson.Serialize(new PluginInvocationContext
        {
            InvocationSource = "link_click",
            ClickedUrl = "https://github.com/org/repo/issues/1",
            LinkHandlerId = "github-issue",
            SelectedText = "SECRET",
        });
        Assert.True(json.IsOk);
        Assert.Contains("\"clicked_url\":\"https://github.com/org/repo/issues/1\"", json.Value, StringComparison.Ordinal);
        Assert.Contains("\"link_handler_id\":\"github-issue\"", json.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", json.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("selected_text", json.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_json_writes_agent_session_when_present()
    {
        var session = SampleSession(sessionStartSource: "startup");
        var json = PluginContextJson.Serialize(new PluginInvocationContext
        {
            FocusedPaneId = "p1",
            AgentSession = session,
        });
        Assert.True(json.IsOk);
        using var doc = JsonDocument.Parse(json.Value);
        var nested = doc.RootElement.GetProperty("agent_session");
        Assert.Equal("id", nested.GetProperty("kind").GetString());
        Assert.Equal("sess-1", nested.GetProperty("value").GetString());
        Assert.Equal("plugin:claude", nested.GetProperty("source").GetString());
        Assert.Equal("claude", nested.GetProperty("agent").GetString());
        Assert.Equal("startup", nested.GetProperty("session_start_source").GetString());
        Assert.False(doc.RootElement.TryGetProperty("selected_text", out _));
    }

    [Fact]
    public void Context_json_omits_agent_session_when_absent()
    {
        var json = PluginContextJson.Serialize(new PluginInvocationContext { FocusedPaneId = "p1" });
        Assert.True(json.IsOk);
        Assert.DoesNotContain("agent_session", json.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_json_omits_session_start_source_when_unset()
    {
        var json = PluginContextJson.Serialize(new PluginInvocationContext
        {
            AgentSession = SampleSession(),
        });
        Assert.True(json.IsOk);
        using var doc = JsonDocument.Parse(json.Value);
        var nested = doc.RootElement.GetProperty("agent_session");
        Assert.False(nested.TryGetProperty("session_start_source", out _));
    }

    [Fact]
    public void Context_json_shrink_pass_keeps_agent_session_under_cap()
    {
        var pad = new string('x', 5000);
        var json = PluginContextJson.Serialize(new PluginInvocationContext
        {
            WorkspaceId = "w1",
            WorkspaceLabel = pad,
            TabLabel = pad,
            WorkspaceCwd = pad,
            FocusedPaneCwd = pad,
            AgentSession = SampleSession(),
            ProgramName = "pi",
        });
        Assert.True(json.IsOk);
        Assert.Contains("agent_session", json.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(pad, json.Value, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(json.Value) <= PluginCommandLimits.ContextJsonMaxBytes);
    }

    [Fact]
    public void Membership_maps_label_to_repo_name()
    {
        var mapped = PluginInvocationContextWorktree.FromMembership(SampleMembership("git"));
        Assert.NotNull(mapped);
        Assert.Equal("key-git", mapped.RepoKey);
        Assert.Equal("repo-git", mapped.RepoName);
        Assert.Equal("/repo/git", mapped.RepoRoot);
        Assert.Equal("/repo/git/wt", mapped.CheckoutPath);
        Assert.True(mapped.IsLinkedWorktree);
        Assert.Null(PluginInvocationContextWorktree.FromMembership(null));
        var empty = PluginInvocationContextWorktree.FromMembership(new WorktreeSpaceMembership
        {
            Key = "",
            Label = "",
            RepoRoot = "",
            CheckoutPath = "",
            IsLinkedWorktree = false,
        });
        Assert.NotNull(empty);
        Assert.Equal("", empty.RepoKey);
        Assert.False(empty.IsLinkedWorktree);
    }

    [Fact]
    public void Context_json_writes_worktree_when_present()
    {
        var json = PluginContextJson.Serialize(new PluginInvocationContext
        {
            WorkspaceId = "w1",
            Worktree = SampleWorktree(),
            SelectedText = "SECRET",
        });
        Assert.True(json.IsOk);
        using var doc = JsonDocument.Parse(json.Value);
        var nested = doc.RootElement.GetProperty("worktree");
        Assert.Equal("key-a", nested.GetProperty("repo_key").GetString());
        Assert.Equal("repo-a", nested.GetProperty("repo_name").GetString());
        Assert.Equal("/repo/a", nested.GetProperty("repo_root").GetString());
        Assert.Equal("/repo/a/wt", nested.GetProperty("checkout_path").GetString());
        Assert.True(nested.GetProperty("is_linked_worktree").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("selected_text", out _));
    }

    [Fact]
    public void Context_json_omits_worktree_when_absent()
    {
        var json = PluginContextJson.Serialize(new PluginInvocationContext { WorkspaceId = "w1" });
        Assert.True(json.IsOk);
        Assert.DoesNotContain("worktree", json.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("selected_text", json.Value, StringComparison.Ordinal);
        var emptyJson = PluginContextJson.Serialize(new PluginInvocationContext
        {
            WorkspaceId = "w1",
            Worktree = new PluginWorktreeContext
            {
                RepoKey = "",
                RepoName = "",
                RepoRoot = "",
                CheckoutPath = "",
                IsLinkedWorktree = false,
            },
        });
        Assert.True(emptyJson.IsOk);
        using var emptyDoc = JsonDocument.Parse(emptyJson.Value);
        var nested = emptyDoc.RootElement.GetProperty("worktree");
        Assert.Equal("", nested.GetProperty("repo_key").GetString());
        Assert.False(nested.GetProperty("is_linked_worktree").GetBoolean());
    }

    [Fact]
    public void Context_json_shrink_pass_keeps_worktree_under_cap()
    {
        var pad = new string('x', 5000);
        var json = PluginContextJson.Serialize(new PluginInvocationContext
        {
            WorkspaceId = "w1",
            WorkspaceLabel = pad,
            TabLabel = pad,
            WorkspaceCwd = pad,
            FocusedPaneCwd = pad,
            Worktree = SampleWorktree(),
            ProgramName = "pi",
        });
        Assert.True(json.IsOk);
        using var doc = JsonDocument.Parse(json.Value);
        var nested = doc.RootElement.GetProperty("worktree");
        Assert.Equal("/repo/a", nested.GetProperty("repo_root").GetString());
        Assert.Equal("/repo/a/wt", nested.GetProperty("checkout_path").GetString());
        Assert.DoesNotContain(pad, json.Value, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(json.Value) <= PluginCommandLimits.ContextJsonMaxBytes);
    }

    [Fact]
    public void Invoke_prefers_caller_worktree_over_focused()
    {
        var focused = new WorktreeContextSource { Worktree = SampleWorktree("focus") };
        var host = new PluginHostService(
            _files,
            _clock,
            new SystemPluginPathRoots(_root),
            new PluginManifestParser(),
            new FilePluginRegistry(_files, new SystemPluginPathRoots(_root)),
            _launcher,
            focused,
            processRegistry: _processRegistry);
        var dir = WritePlugin("example.wt-merge", DefaultManifest("example.wt-merge") + """

            [grants]
            request = ["action.invoke:self"]

            [[actions]]
            id = "ping"
            title = "Ping"
            command = ["/bin/echo", "ok"]
            """);
        Assert.True(host.Link(dir, true).IsOk);
        var invoked = host.InvokeAction(
            "ping",
            "example.wt-merge",
            new PluginInvocationContext { Worktree = SampleWorktree("caller") });
        Assert.True(invoked.IsOk, invoked.IsOk ? "" : invoked.Error.Message);
        using var doc = JsonDocument.Parse(_launcher.Starts[0].Environment[PluginEnv.ContextJson]);
        Assert.Equal("key-caller", doc.RootElement.GetProperty("worktree").GetProperty("repo_key").GetString());
    }

    [Fact]
    public async Task Invoke_action_keeps_empty_caller_worktree()
    {
        var (cp, app, host) = CreateEventControlPlane();
        try
        {
            var dir = WritePlugin("example.wt-empty-invoke", DefaultManifest("example.wt-empty-invoke") + """

                [grants]
                request = ["action.invoke:self"]

                [[actions]]
                id = "ping"
                title = "Ping"
                command = ["/bin/echo", "ok"]
                """);
            Assert.True(host.Link(dir, true).IsOk);
            var paneId = await CreateWorkspacePaneAsync(cp);
            var ws = app.GetPane(new PaneId(paneId))!.WorkspaceId;
            app.SetWorktreeMembership(ws, SampleMembership("focus"));
            await FocusWorkspaceAsync(cp, ws.Value);
            var invoked = await cp.DispatchAsync(
                ProtocolMethods.PluginActionInvoke,
                JsonDocument.Parse("""
                    {"action_id":"ping","plugin_id":"example.wt-empty-invoke","context":{"worktree":{"repo_key":"","repo_name":"","repo_root":"","checkout_path":"","is_linked_worktree":false}}}
                    """).RootElement,
                CancellationToken.None);
            var nested = invoked.GetProperty("context").GetProperty("worktree");
            Assert.Equal("", nested.GetProperty("repo_key").GetString());
            Assert.Equal("", nested.GetProperty("repo_name").GetString());
            Assert.Equal("", nested.GetProperty("repo_root").GetString());
            Assert.Equal("", nested.GetProperty("checkout_path").GetString());
            Assert.False(nested.GetProperty("is_linked_worktree").GetBoolean());
            using var env = JsonDocument.Parse(_launcher.Starts[0].Environment[PluginEnv.ContextJson]);
            Assert.Equal("", env.RootElement.GetProperty("worktree").GetProperty("repo_key").GetString());
            Assert.False(env.RootElement.GetProperty("worktree").GetProperty("is_linked_worktree").GetBoolean());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Activate_link_prefers_provided_worktree()
    {
        var focused = new WorktreeContextSource { Worktree = SampleWorktree("focus") };
        var host = new PluginHostService(
            _files,
            _clock,
            new SystemPluginPathRoots(_root),
            new PluginManifestParser(),
            new FilePluginRegistry(_files, new SystemPluginPathRoots(_root)),
            _launcher,
            focused,
            processRegistry: _processRegistry);
        var dir = WritePlugin("example.wt-link", DefaultManifest("example.wt-link") + LinkHandlerManifest());
        Assert.True(host.Link(dir, true).IsOk);
        var handled = host.ActivateLink(
            "https://github.com/org/repo/issues/1",
            new PluginInvocationContext { Worktree = SampleWorktree("click") });
        Assert.True(handled.IsOk, handled.IsOk ? "" : handled.Error.Message);
        Assert.True(handled.Value);
        using var doc = JsonDocument.Parse(_launcher.Starts[0].Environment[PluginEnv.ContextJson]);
        Assert.Equal("key-click", doc.RootElement.GetProperty("worktree").GetProperty("repo_key").GetString());
    }

    [Fact]
    public void Startup_uses_focused_workspace_worktree()
    {
        var source = new WorktreeContextSource { Worktree = SampleWorktree("start") };
        var host = new PluginHostService(
            _files,
            _clock,
            new SystemPluginPathRoots(_root),
            new PluginManifestParser(),
            new FilePluginRegistry(_files, new SystemPluginPathRoots(_root)),
            _launcher,
            source,
            processRegistry: _processRegistry);
        var dir = WritePlugin("example.wt-start", DefaultManifest("example.wt-start") + """

            [[startup]]
            command = ["/bin/echo", "boot"]
            """);
        Assert.True(host.Link(dir, true).IsOk);
        host.RunStartupHooks();
        using var doc = JsonDocument.Parse(_launcher.Starts[0].Environment[PluginEnv.ContextJson]);
        Assert.Equal("key-start", doc.RootElement.GetProperty("worktree").GetProperty("repo_key").GetString());
        Assert.Equal("/repo/start/wt", doc.RootElement.GetProperty("worktree").GetProperty("checkout_path").GetString());
    }

    [Fact]
    public async Task Invoke_context_includes_focused_workspace_worktree()
    {
        var root = Path.Combine(Path.GetTempPath(), "hypa-plugin-wt-action-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dir = Path.Combine(root, "example.wt-action");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, PluginHostService.ManifestFileName),
            DefaultManifest("example.wt-action") + """

            [grants]
            request = ["action.invoke:self"]

            [[actions]]
            id = "ping"
            title = "Ping"
            command = ["/bin/echo", "ok"]
            """);
        var app = new AppState(SessionId.New("plugin-wt-action"));
        app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var cp = new ControlPlaneService(
            app,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            stateDirectory: root);
        try
        {
            await cp.DispatchAsync(
                ProtocolMethods.PluginLink,
                JsonDocument.Parse($$"""{"path":{{JsonSerializer.Serialize(dir)}},"enabled":true}""").RootElement,
                CancellationToken.None);
            var paneId = await CreateWorkspacePaneAsync(cp);
            var pane = app.GetPane(new PaneId(paneId));
            Assert.NotNull(pane);
            app.SetWorktreeMembership(pane.WorkspaceId, SampleMembership("live"));
            var invoked = await cp.DispatchAsync(
                ProtocolMethods.PluginActionInvoke,
                JsonDocument.Parse("""{"action_id":"ping","plugin_id":"example.wt-action"}""").RootElement,
                CancellationToken.None);
            var nested = invoked.GetProperty("context").GetProperty("worktree");
            Assert.Equal("key-live", nested.GetProperty("repo_key").GetString());
            Assert.Equal("repo-live", nested.GetProperty("repo_name").GetString());
            Assert.Equal("/repo/live", nested.GetProperty("repo_root").GetString());
            Assert.Equal("/repo/live/wt", nested.GetProperty("checkout_path").GetString());
            Assert.True(nested.GetProperty("is_linked_worktree").GetBoolean());
            Assert.False(invoked.GetProperty("context").TryGetProperty("selected_text", out _));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void Pane_open_uses_focused_workspace_worktree()
    {
        var dir = WritePlugin("example.wt-pane", DefaultManifest("example.wt-pane") + """

            [grants]
            request = ["pane.open:self"]

            [[panes]]
            id = "board"
            title = "Board"
            placement = "split"
            command = ["/bin/echo", "board"]
            """);
        Assert.True(_host.Link(dir, true).IsOk);
        var plan = _host.PlanPaneOpen(
            "example.wt-pane",
            "board",
            "split",
            new PluginInvocationContext { Worktree = SampleWorktree("pane") },
            extraEnv: null);
        Assert.True(plan.IsOk, plan.IsOk ? "" : plan.Error.Message);
        using var doc = JsonDocument.Parse(plan.Value.Environment[PluginEnv.ContextJson]);
        Assert.Equal("key-pane", doc.RootElement.GetProperty("worktree").GetProperty("repo_key").GetString());
        Assert.Equal("/repo/pane/wt", doc.RootElement.GetProperty("worktree").GetProperty("checkout_path").GetString());
    }

    [Fact]
    public void Event_json_writes_workspace_snapshot_and_worktree_row()
    {
        var json = PluginWorktreeEventJson.Write(
            "w-event",
            "issue",
            SampleWorktree("event"),
            SampleWorktreeRow("event", "w-event"),
            alreadyOpen: true,
            forced: null);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("w-event", doc.RootElement.GetProperty("workspace_id").GetString());
        var workspace = doc.RootElement.GetProperty("workspace");
        Assert.Equal("w-event", workspace.GetProperty("workspace_id").GetString());
        Assert.Equal("issue", workspace.GetProperty("label").GetString());
        var nested = workspace.GetProperty("worktree");
        Assert.Equal("key-event", nested.GetProperty("repo_key").GetString());
        Assert.Equal("/repo/event/wt", nested.GetProperty("checkout_path").GetString());
        var row = doc.RootElement.GetProperty("worktree");
        Assert.Equal("/repo/event/wt", row.GetProperty("path").GetString());
        Assert.Equal("worktree/event", row.GetProperty("branch").GetString());
        Assert.False(row.GetProperty("is_bare").GetBoolean());
        Assert.False(row.GetProperty("is_detached").GetBoolean());
        Assert.False(row.GetProperty("is_prunable").GetBoolean());
        Assert.True(row.GetProperty("is_linked_worktree").GetBoolean());
        Assert.Equal("w-event", row.GetProperty("open_workspace_id").GetString());
        Assert.Equal("repo-event", row.GetProperty("label").GetString());
        Assert.True(doc.RootElement.GetProperty("already_open").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("forced", out _));
        Assert.DoesNotContain(WorktreePathRules.RedactedCheckout, json, StringComparison.Ordinal);
        var closed = PluginWorktreeEventJson.Write(
            "w-event",
            "issue",
            SampleWorktree("event"),
            SampleWorktreeRow("event", "w-event"),
            alreadyOpen: false,
            forced: null);
        using var closedDoc = JsonDocument.Parse(closed);
        Assert.False(closedDoc.RootElement.GetProperty("already_open").GetBoolean());
        var recovered = PluginWorktreeEventJson.ReadNestedWorktree(closed);
        Assert.NotNull(recovered);
        Assert.Equal("key-event", recovered.RepoKey);
        var emptyJson = PluginWorktreeEventJson.Write(
            "w-empty",
            "issue",
            EmptyWorktree(),
            SampleWorktreeRow("event", "w-empty"),
            alreadyOpen: false,
            forced: null);
        var emptyNested = PluginWorktreeEventJson.ReadNestedWorktree(emptyJson);
        Assert.NotNull(emptyNested);
        Assert.Equal("", emptyNested.RepoKey);
        Assert.False(emptyNested.IsLinkedWorktree);
    }

    [Fact]
    public void Event_json_removed_keeps_forced_and_path()
    {
        var json = PluginWorktreeEventJson.Write(
            "w-gone",
            "issue",
            SampleWorktree("gone"),
            SampleWorktreeRow("gone", "w-gone"),
            alreadyOpen: null,
            forced: true);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("forced").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("already_open", out _));
        Assert.Equal("/repo/gone/wt", doc.RootElement.GetProperty("worktree").GetProperty("path").GetString());
    }

    [Fact]
    public void Event_filter_reads_nested_workspace_id()
    {
        var filter = new PluginEventFilter { Workspace = ["w-event"] };
        var context = new PluginInvocationContext { WorkspaceId = "w-focus" };
        Assert.True(PluginEventFilterMatcher.Matches(
            filter,
            context,
            """{"workspace":{"workspace_id":"w-event"}}"""));
        Assert.False(PluginEventFilterMatcher.Matches(
            filter,
            context,
            """{"workspace":{"workspace_id":"w-other"}}"""));
    }

    [Fact]
    public async Task Created_hook_uses_event_workspace_not_focused()
    {
        var (cp, app, host) = CreateEventControlPlane();
        try
        {
            var dir = WritePlugin("example.wt-created", DefaultManifest("example.wt-created") + """

                [[events]]
                on = "worktree.created"
                command = ["/bin/echo", "hook"]
                """);
            Assert.True(host.Link(dir, true).IsOk);
            var focusPane = await CreateWorkspacePaneAsync(cp);
            var eventPane = await CreateWorkspacePaneAsync(cp);
            var focusWs = app.GetPane(new PaneId(focusPane))!.WorkspaceId;
            var eventWs = app.GetPane(new PaneId(eventPane))!.WorkspaceId;
            app.SetWorktreeMembership(focusWs, SampleMembership("focus"));
            app.SetWorktreeMembership(eventWs, SampleMembership("event"));
            await FocusWorkspaceAsync(cp, focusWs.Value);
            var payload = PluginWorktreeEventJson.Write(
                eventWs.Value,
                "event-ws",
                SampleWorktree("event"),
                SampleWorktreeRow("event", eventWs.Value),
                alreadyOpen: false,
                forced: null);
            cp.NotifyPluginEvent(ProtocolEventTypes.WorktreeCreated, payload);
            var start = Assert.Single(_launcher.Starts);
            using var ctx = JsonDocument.Parse(start.Environment[PluginEnv.ContextJson]);
            Assert.Equal(eventWs.Value, ctx.RootElement.GetProperty("workspace_id").GetString());
            Assert.Equal("key-event", ctx.RootElement.GetProperty("worktree").GetProperty("repo_key").GetString());
            Assert.NotEqual(focusWs.Value, ctx.RootElement.GetProperty("workspace_id").GetString());
            using var ev = JsonDocument.Parse(start.Environment[PluginEnv.EventJson]);
            Assert.Equal("/repo/event/wt", ev.RootElement.GetProperty("worktree").GetProperty("path").GetString());
            Assert.False(ev.RootElement.GetProperty("worktree").GetProperty("is_bare").GetBoolean());
            Assert.False(ev.RootElement.GetProperty("already_open").GetBoolean());
            Assert.False(ctx.RootElement.TryGetProperty("selected_text", out _));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Opened_hook_uses_event_workspace_not_focused()
    {
        var (cp, app, host) = CreateEventControlPlane();
        try
        {
            var dir = WritePlugin("example.wt-opened", DefaultManifest("example.wt-opened") + """

                [[events]]
                on = "worktree.opened"
                command = ["/bin/echo", "hook"]
                """);
            Assert.True(host.Link(dir, true).IsOk);
            var focusPane = await CreateWorkspacePaneAsync(cp);
            var eventPane = await CreateWorkspacePaneAsync(cp);
            var focusWs = app.GetPane(new PaneId(focusPane))!.WorkspaceId;
            var eventWs = app.GetPane(new PaneId(eventPane))!.WorkspaceId;
            app.SetWorktreeMembership(focusWs, SampleMembership("focus"));
            app.SetWorktreeMembership(eventWs, SampleMembership("event"));
            await FocusWorkspaceAsync(cp, focusWs.Value);
            var payload = PluginWorktreeEventJson.Write(
                eventWs.Value,
                "event-ws",
                SampleWorktree("event"),
                SampleWorktreeRow("event", eventWs.Value) with { IsDetached = true },
                alreadyOpen: false,
                forced: null);
            cp.NotifyPluginEvent(ProtocolEventTypes.WorktreeOpened, payload);
            var start = Assert.Single(_launcher.Starts);
            using var ctx = JsonDocument.Parse(start.Environment[PluginEnv.ContextJson]);
            Assert.Equal(eventWs.Value, ctx.RootElement.GetProperty("workspace_id").GetString());
            Assert.Equal("key-event", ctx.RootElement.GetProperty("worktree").GetProperty("repo_key").GetString());
            using var ev = JsonDocument.Parse(start.Environment[PluginEnv.EventJson]);
            Assert.True(ev.RootElement.GetProperty("worktree").GetProperty("is_detached").GetBoolean());
            Assert.False(ev.RootElement.GetProperty("already_open").GetBoolean());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Created_hook_filter_matches_event_workspace()
    {
        var (cp, app, host) = CreateEventControlPlane();
        try
        {
            var focusPane = await CreateWorkspacePaneAsync(cp);
            var eventPane = await CreateWorkspacePaneAsync(cp);
            var focusWs = app.GetPane(new PaneId(focusPane))!.WorkspaceId;
            var eventWs = app.GetPane(new PaneId(eventPane))!.WorkspaceId;
            app.SetWorktreeMembership(focusWs, SampleMembership("focus"));
            app.SetWorktreeMembership(eventWs, SampleMembership("event"));
            await FocusWorkspaceAsync(cp, focusWs.Value);
            var dir = WritePlugin(
                "example.wt-filter",
                DefaultManifest("example.wt-filter") + $"""

                [[events]]
                on = "worktree.created"
                command = ["/bin/echo", "hook"]
                filter.workspace = ["{focusWs.Value}"]
                """);
            Assert.True(host.Link(dir, true).IsOk);
            var payload = PluginWorktreeEventJson.Write(
                eventWs.Value,
                "event-ws",
                SampleWorktree("event"),
                SampleWorktreeRow("event", eventWs.Value),
                alreadyOpen: null,
                forced: null);
            cp.NotifyPluginEvent(ProtocolEventTypes.WorktreeCreated, payload);
            Assert.Empty(_launcher.Starts);
            host.Unlink("example.wt-filter");
            var keep = WritePlugin(
                "example.wt-filter-keep",
                DefaultManifest("example.wt-filter-keep") + $"""

                [[events]]
                on = "worktree.created"
                command = ["/bin/echo", "hook"]
                filter.workspace = ["{eventWs.Value}"]
                """);
            Assert.True(host.Link(keep, true).IsOk);
            cp.NotifyPluginEvent(ProtocolEventTypes.WorktreeCreated, payload);
            Assert.Single(_launcher.Starts);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Removed_hook_keeps_snapshot_after_workspace_close()
    {
        var (cp, app, host) = CreateEventControlPlane();
        try
        {
            var dir = WritePlugin("example.wt-removed", DefaultManifest("example.wt-removed") + """

                [[events]]
                on = "worktree.removed"
                command = ["/bin/echo", "hook"]
                """);
            Assert.True(host.Link(dir, true).IsOk);
            var keepPane = await CreateWorkspacePaneAsync(cp);
            var gonePane = await CreateWorkspacePaneAsync(cp);
            var keepWs = app.GetPane(new PaneId(keepPane))!.WorkspaceId;
            var goneWs = app.GetPane(new PaneId(gonePane))!.WorkspaceId;
            app.SetWorktreeMembership(keepWs, SampleMembership("keep"));
            app.SetWorktreeMembership(goneWs, SampleMembership("gone"));
            await FocusWorkspaceAsync(cp, keepWs.Value);
            var payload = PluginWorktreeEventJson.Write(
                goneWs.Value,
                "gone-ws",
                SampleWorktree("gone"),
                SampleWorktreeRow("gone", goneWs.Value),
                alreadyOpen: null,
                forced: true);
            await cp.DispatchAsync(
                ProtocolMethods.WorkspaceClose,
                JsonDocument.Parse($$"""{"workspace_id":{{JsonSerializer.Serialize(goneWs.Value)}}}""").RootElement,
                CancellationToken.None);
            Assert.Null(app.GetWorkspace(goneWs));
            cp.NotifyPluginEvent(ProtocolEventTypes.WorktreeRemoved, payload);
            var start = Assert.Single(_launcher.Starts);
            using var ctx = JsonDocument.Parse(start.Environment[PluginEnv.ContextJson]);
            Assert.Equal(goneWs.Value, ctx.RootElement.GetProperty("workspace_id").GetString());
            Assert.Equal("key-gone", ctx.RootElement.GetProperty("worktree").GetProperty("repo_key").GetString());
            Assert.Equal("/repo/gone/wt", ctx.RootElement.GetProperty("worktree").GetProperty("checkout_path").GetString());
            using var ev = JsonDocument.Parse(start.Environment[PluginEnv.EventJson]);
            Assert.Equal("/repo/gone/wt", ev.RootElement.GetProperty("worktree").GetProperty("path").GetString());
            Assert.True(ev.RootElement.GetProperty("forced").GetBoolean());
            Assert.True(ev.RootElement.GetProperty("worktree").GetProperty("is_linked_worktree").GetBoolean());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Removed_hook_keeps_empty_membership_after_workspace_close()
    {
        var (cp, app, host) = CreateEventControlPlane();
        try
        {
            var dir = WritePlugin("example.wt-removed-empty", DefaultManifest("example.wt-removed-empty") + """

                [[events]]
                on = "worktree.removed"
                command = ["/bin/echo", "hook"]
                """);
            Assert.True(host.Link(dir, true).IsOk);
            var keepPane = await CreateWorkspacePaneAsync(cp);
            var gonePane = await CreateWorkspacePaneAsync(cp);
            var keepWs = app.GetPane(new PaneId(keepPane))!.WorkspaceId;
            var goneWs = app.GetPane(new PaneId(gonePane))!.WorkspaceId;
            app.SetWorktreeMembership(keepWs, SampleMembership("keep"));
            app.SetWorktreeMembership(goneWs, EmptyMembership());
            await FocusWorkspaceAsync(cp, keepWs.Value);
            var payload = PluginWorktreeEventJson.Write(
                goneWs.Value,
                "gone-ws",
                EmptyWorktree(),
                SampleWorktreeRow("gone", goneWs.Value),
                alreadyOpen: null,
                forced: true);
            await cp.DispatchAsync(
                ProtocolMethods.WorkspaceClose,
                JsonDocument.Parse($$"""{"workspace_id":{{JsonSerializer.Serialize(goneWs.Value)}}}""").RootElement,
                CancellationToken.None);
            Assert.Null(app.GetWorkspace(goneWs));
            cp.NotifyPluginEvent(ProtocolEventTypes.WorktreeRemoved, payload);
            var start = Assert.Single(_launcher.Starts);
            using var ctx = JsonDocument.Parse(start.Environment[PluginEnv.ContextJson]);
            Assert.Equal(goneWs.Value, ctx.RootElement.GetProperty("workspace_id").GetString());
            var nested = ctx.RootElement.GetProperty("worktree");
            Assert.Equal("", nested.GetProperty("repo_key").GetString());
            Assert.Equal("", nested.GetProperty("checkout_path").GetString());
            Assert.False(nested.GetProperty("is_linked_worktree").GetBoolean());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Emit_keeps_journal_redacted_and_plugin_path_open()
    {
        var journal = new MemoryJournal();
        var (cp, _, host) = CreateEventControlPlane(journal);
        try
        {
            var dir = WritePlugin("example.wt-journal", DefaultManifest("example.wt-journal") + """

                [[events]]
                on = "worktree.created"
                command = ["/bin/echo", "hook"]
                """);
            Assert.True(host.Link(dir, true).IsOk);
            await cp.EmitWorktreeEventAsync(
                ProtocolEventTypes.WorktreeCreated,
                "w-event",
                "worktree/event",
                "repo-event",
                alreadyOpen: false,
                SampleWorktreeRow("event", "w-event"),
                SampleWorktree("event"),
                "event-ws",
                forced: null,
                CancellationToken.None);
            var stored = Assert.Single(journal.Records);
            Assert.Contains(WorktreePathRules.RedactedCheckout, stored.PayloadJson, StringComparison.Ordinal);
            Assert.DoesNotContain("/repo/event/wt", stored.PayloadJson, StringComparison.Ordinal);
            var start = Assert.Single(_launcher.Starts);
            using var ev = JsonDocument.Parse(start.Environment[PluginEnv.EventJson]);
            Assert.Equal("/repo/event/wt", ev.RootElement.GetProperty("worktree").GetProperty("path").GetString());
            Assert.False(ev.RootElement.GetProperty("already_open").GetBoolean());
            Assert.DoesNotContain(WorktreePathRules.RedactedCheckout, start.Environment[PluginEnv.EventJson], StringComparison.Ordinal);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Pane_created_hook_omits_worktree_when_event_workspace_differs()
    {
        var (cp, app, host) = CreateEventControlPlane();
        try
        {
            var dir = WritePlugin("example.wt-pane-hook", DefaultManifest("example.wt-pane-hook") + """

                [[events]]
                on = "pane.created"
                command = ["/bin/echo", "hook"]
                """);
            Assert.True(host.Link(dir, true).IsOk);
            var focusPane = await CreateWorkspacePaneAsync(cp);
            var otherPane = await CreateWorkspacePaneAsync(cp);
            var focusWs = app.GetPane(new PaneId(focusPane))!.WorkspaceId;
            var otherWs = app.GetPane(new PaneId(otherPane))!.WorkspaceId;
            app.SetWorktreeMembership(focusWs, SampleMembership("focus"));
            app.SetWorktreeMembership(otherWs, SampleMembership("other"));
            await FocusWorkspaceAsync(cp, focusWs.Value);
            cp.NotifyPluginEvent(
                "pane.lifecycle",
                $$"""{"pane_id":"p-new","state":"created","workspace_id":{{JsonSerializer.Serialize(otherWs.Value)}}}""");
            var start = Assert.Single(_launcher.Starts);
            using var ctx = JsonDocument.Parse(start.Environment[PluginEnv.ContextJson]);
            Assert.Equal(otherWs.Value, ctx.RootElement.GetProperty("workspace_id").GetString());
            Assert.False(ctx.RootElement.TryGetProperty("worktree", out _));
            _launcher.Starts.Clear();
            cp.NotifyPluginEvent(
                "pane.lifecycle",
                $$"""{"pane_id":"p-same","state":"created","workspace_id":{{JsonSerializer.Serialize(focusWs.Value)}}}""");
            var same = Assert.Single(_launcher.Starts);
            using var sameCtx = JsonDocument.Parse(same.Environment[PluginEnv.ContextJson]);
            Assert.Equal(focusWs.Value, sameCtx.RootElement.GetProperty("workspace_id").GetString());
            Assert.Equal("key-focus", sameCtx.RootElement.GetProperty("worktree").GetProperty("repo_key").GetString());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Link_resolver_uses_clicked_pane_workspace_not_focused()
    {
        var (cp, app, _) = CreateEventControlPlane();
        try
        {
            var focusPane = await CreateWorkspacePaneAsync(cp);
            var clickPane = await CreateWorkspacePaneAsync(cp);
            var focusWs = app.GetPane(new PaneId(focusPane))!.WorkspaceId;
            var clickWs = app.GetPane(new PaneId(clickPane))!.WorkspaceId;
            app.SetWorktreeMembership(focusWs, SampleMembership("focus"));
            app.SetWorktreeMembership(clickWs, SampleMembership("click"));
            await FocusWorkspaceAsync(cp, focusWs.Value);
            var pane = app.GetPane(new PaneId(clickPane));
            Assert.NotNull(pane);
            var ctx = cp.PluginContextForPane(pane, "link_click");
            Assert.Equal(clickWs.Value, ctx.WorkspaceId);
            Assert.Equal("key-click", ctx.Worktree!.RepoKey);
            Assert.Equal("/repo/click/wt", ctx.Worktree.CheckoutPath);
            Assert.NotEqual("key-focus", ctx.Worktree.RepoKey);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Invoke_context_includes_focused_pane_agent_session()
    {
        var root = Path.Combine(Path.GetTempPath(), "hypa-plugin-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dir = Path.Combine(root, "example.session");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, PluginHostService.ManifestFileName),
            DefaultManifest("example.session") + """

            [grants]
            request = ["action.invoke:self"]

            [[actions]]
            id = "ping"
            title = "Ping"
            command = ["/bin/echo", "ok"]
            """);

        var app = new AppState(SessionId.New("plugin-session"));
        app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var cp = new ControlPlaneService(
            app,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            stateDirectory: root);
        try
        {
            await cp.DispatchAsync(
                ProtocolMethods.PluginLink,
                JsonDocument.Parse($$"""{"path":{{JsonSerializer.Serialize(dir)}},"enabled":true}""").RootElement,
                CancellationToken.None);

            var created = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                JsonDocument.Parse(
                    $$"""{"cwd":{{JsonSerializer.Serialize(Path.GetTempPath())}},"command":"/bin/true","create_pane":true}""")
                    .RootElement,
                CancellationToken.None);
            var paneId = created.GetProperty("pane").GetProperty("pane_id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(paneId));

            await cp.DispatchAsync(
                ProtocolMethods.PaneReportAgent,
                JsonDocument.Parse(
                    $$"""{"pane_id":{{JsonSerializer.Serialize(paneId)}},"source":"plugin:claude","agent":"claude","state":"working","seq":1}""")
                    .RootElement,
                CancellationToken.None);
            await cp.DispatchAsync(
                ProtocolMethods.PaneReportAgentSession,
                JsonDocument.Parse(
                    $$"""{"pane_id":{{JsonSerializer.Serialize(paneId)}},"source":"plugin:claude","agent":"claude","agent_session_id":"sess-1","session_start_source":"startup","seq":2}""")
                    .RootElement,
                CancellationToken.None);

            var invoked = await cp.DispatchAsync(
                ProtocolMethods.PluginActionInvoke,
                JsonDocument.Parse("""{"action_id":"ping","plugin_id":"example.session"}""").RootElement,
                CancellationToken.None);
            var context = invoked.GetProperty("context");
            var session = context.GetProperty("agent_session");
            Assert.Equal("id", session.GetProperty("kind").GetString());
            Assert.Equal("sess-1", session.GetProperty("value").GetString());
            Assert.Equal("plugin:claude", session.GetProperty("source").GetString());
            Assert.Equal("claude", session.GetProperty("agent").GetString());
            Assert.Equal("startup", session.GetProperty("session_start_source").GetString());
            Assert.False(context.TryGetProperty("selected_text", out _));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task Invoke_context_clears_agent_session_after_release()
    {
        var root = Path.Combine(Path.GetTempPath(), "hypa-plugin-session-rel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dir = Path.Combine(root, "example.session-rel");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, PluginHostService.ManifestFileName),
            DefaultManifest("example.session-rel") + """

            [grants]
            request = ["action.invoke:self"]

            [[actions]]
            id = "ping"
            title = "Ping"
            command = ["/bin/echo", "ok"]
            """);

        var app = new AppState(SessionId.New("plugin-session-rel"));
        app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var cp = new ControlPlaneService(
            app,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            stateDirectory: root);
        try
        {
            await cp.DispatchAsync(
                ProtocolMethods.PluginLink,
                JsonDocument.Parse($$"""{"path":{{JsonSerializer.Serialize(dir)}},"enabled":true}""").RootElement,
                CancellationToken.None);

            var created = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                JsonDocument.Parse(
                    $$"""{"cwd":{{JsonSerializer.Serialize(Path.GetTempPath())}},"command":"/bin/true","create_pane":true}""")
                    .RootElement,
                CancellationToken.None);
            var paneId = created.GetProperty("pane").GetProperty("pane_id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(paneId));

            await cp.DispatchAsync(
                ProtocolMethods.PaneReportAgent,
                JsonDocument.Parse(
                    $$"""{"pane_id":{{JsonSerializer.Serialize(paneId)}},"source":"plugin:claude","agent":"claude","state":"working","seq":1,"agent_session_id":"sess-1"}""")
                    .RootElement,
                CancellationToken.None);
            await cp.DispatchAsync(
                ProtocolMethods.PaneReleaseAgent,
                JsonDocument.Parse(
                    $$"""{"pane_id":{{JsonSerializer.Serialize(paneId)}},"source":"plugin:claude","agent":"claude","seq":2}""")
                    .RootElement,
                CancellationToken.None);

            var invoked = await cp.DispatchAsync(
                ProtocolMethods.PluginActionInvoke,
                JsonDocument.Parse("""{"action_id":"ping","plugin_id":"example.session-rel"}""").RootElement,
                CancellationToken.None);
            Assert.False(invoked.GetProperty("context").TryGetProperty("agent_session", out _));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task Plugin_link_unsupported_table_preserves_path_on_wire()
    {
        var id = "example.actionz";
        var dir = WritePlugin(id, DefaultManifest(id) + """

            [[actionz]]
            id = "x"
            """);
        var app = new AppState(SessionId.New("plugin-link-table"));
        app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var cp = new ControlPlaneService(
            app,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            plugins: _host);
        try
        {
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PluginLink,
                    JsonDocument.Parse($$"""{"path":{{JsonSerializer.Serialize(dir)}},"enabled":true}""").RootElement,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidParams, ex.Code);
            Assert.Equal(PluginError.UnsupportedTable, ex.ErrorCode);
            Assert.Equal("unsupported_manifest_table: actionz", ex.Message);
            Assert.DoesNotContain(_host.List(null).Value, p => p.PluginId == id);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Control_plane_link_and_source_prefix()
    {
        var dir = WritePlugin("example.rpc", DefaultManifest("example.rpc") + """

            [grants]
            request = ["action.invoke:self"]

            [[actions]]
            id = "ping"
            title = "Ping"
            command = ["/bin/echo", "ok"]
            """);
        var app = new AppState(SessionId.New("plugin-rpc"));
        app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var cp = new ControlPlaneService(
            app,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            plugins: _host);
        try
        {
            var linked = await cp.DispatchAsync(
                ProtocolMethods.PluginLink,
                JsonDocument.Parse($$"""{"path":{{JsonSerializer.Serialize(dir)}},"enabled":true}""").RootElement,
                CancellationToken.None);
            Assert.Equal("example.rpc", linked.GetProperty("plugin").GetProperty("plugin_id").GetString());
            Assert.True(linked.GetProperty("trust_preview").GetProperty("commands").GetArrayLength() > 0);

            var listed = await cp.DispatchAsync(ProtocolMethods.PluginList, null, CancellationToken.None);
            Assert.Equal(JsonValueKind.Array, listed.GetProperty("plugins").ValueKind);

            var token = _host.PeekGrantToken("example.rpc");
            var refused = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneReportAgent,
                    JsonDocument.Parse("""{"pane_id":"p1","source":"hypa:claude","agent":"claude","state":"idle"}""").RootElement,
                    connection: null,
                    grantToken: token,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrors.CapabilityMissing, refused.Message);

            var created = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                JsonDocument.Parse(
                    $$"""{"cwd":{{JsonSerializer.Serialize(Path.GetTempPath())}},"command":"/bin/true","create_pane":true}""")
                    .RootElement,
                CancellationToken.None);
            var paneId = created.GetProperty("pane").GetProperty("pane_id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(paneId));

            var human = await cp.DispatchAsync(
                ProtocolMethods.PaneReportAgent,
                JsonDocument.Parse(
                    $$"""{"pane_id":{{JsonSerializer.Serialize(paneId)}},"source":"plugin:claude","agent":"claude","state":"idle","seq":1}""")
                    .RootElement,
                CancellationToken.None);
            Assert.True(human.GetProperty("ok").GetBoolean());
            Assert.Equal("plugin:claude", app.GetPane(new PaneId(paneId!))!.AgentAuthority!.Source);

            var pluginWorkspace = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                JsonDocument.Parse(
                    $$"""{"cwd":{{JsonSerializer.Serialize(Path.GetTempPath())}},"command":"/bin/true","create_pane":true}""")
                    .RootElement,
                CancellationToken.None);
            var pluginPaneId = pluginWorkspace.GetProperty("pane").GetProperty("pane_id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(pluginPaneId));

            var pluginReport = await cp.DispatchAsync(
                ProtocolMethods.PaneReportAgent,
                JsonDocument.Parse(
                    $$"""{"pane_id":{{JsonSerializer.Serialize(pluginPaneId)}},"source":"plugin:example.rpc","agent":"claude","state":"working","seq":1}""")
                    .RootElement,
                connection: null,
                grantToken: token,
                pluginConnection: true,
                CancellationToken.None);
            Assert.True(pluginReport.GetProperty("ok").GetBoolean());
            Assert.Equal(
                "plugin:example.rpc",
                app.GetPane(new PaneId(pluginPaneId!))!.AgentAuthority!.Source);

            var resource = await cp.DispatchAsync("plugin.resource.list", null, CancellationToken.None);
            Assert.Equal(JsonValueKind.Array, resource.GetProperty("resources").ValueKind);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Protocol_inventory_includes_resource_methods()
    {
        Assert.Contains("plugin.resource.list", ProtocolMethods.All);
        Assert.Contains("plugin.resource.get", ProtocolMethods.All);
        Assert.Contains("plugin.resource.publish", ProtocolMethods.All);
        Assert.Contains("plugin.resource.remove", ProtocolMethods.All);
        foreach (var method in ProtocolMethods.Plugins)
        {
            var req = FixtureCatalog.Load(FixtureCatalog.MethodRequestPath(method));
            var res = FixtureCatalog.Load(FixtureCatalog.MethodResponsePath(method));
            Assert.Contains(method, req, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(res));
        }

        Assert.Contains(ProtocolMethods.PaneLinkActivate, ProtocolMethods.All);
        Assert.DoesNotContain(ProtocolMethods.PaneLinkActivate, PluginGrantCatalog.DispatchAllowlist);
        foreach (var method in ProtocolMethods.PaneLink)
        {
            var req = FixtureCatalog.Load(FixtureCatalog.MethodRequestPath(method));
            var res = FixtureCatalog.Load(FixtureCatalog.MethodResponsePath(method));
            Assert.Contains(method, req, StringComparison.Ordinal);
            Assert.DoesNotContain("\"url\"", req, StringComparison.Ordinal);
            Assert.Contains("handled", res, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Disable_and_unlink_return_owned_panes_for_close()
    {
        var dir = WritePlugin("example.panes", DefaultManifest("example.panes"));
        Assert.True(_host.Link(dir, true).IsOk);
        _host.NoteOwnedPane("example.panes", "board", "p_owned");
        var disabled = _host.SetEnabled("example.panes", false);
        Assert.Contains("p_owned", disabled.Value.ClosedPaneIds);
        Assert.Contains("p_owned", _host.OwnedPaneIds("example.panes"));

        Assert.True(_host.SetEnabled("example.panes", true).IsOk);
        _host.NoteOwnedPane("example.panes", "board", "p_owned2");
        var unlinked = _host.Unlink("example.panes");
        Assert.Contains("p_owned2", unlinked.Value.ClosedPaneIds);
        Assert.Contains("p_owned2", _host.OwnedPaneIds("example.panes"));
    }

    [Fact]
    public async Task Disable_keeps_owned_pane_when_close_is_frozen()
    {
        var dir = WritePlugin("example.frozen", DefaultManifest("example.frozen"));
        Assert.True(_host.Link(dir, true).IsOk);
        _host.NoteOwnedPane("example.frozen", "board", "p_frozen");
        var app = new AppState(SessionId.New("plugin-frozen"));
        app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.FrozenReadOnly });
        var cp = new ControlPlaneService(
            app,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            plugins: _host);
        try
        {
            var refused = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PluginDisable,
                    JsonDocument.Parse("""{"plugin_id":"example.frozen"}""").RootElement,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidState, refused.Code);
            Assert.Contains("p_frozen", _host.OwnedPaneIds("example.frozen"));
            Assert.True(_host.List("example.frozen").Value[0].Enabled);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Absent_plugin_surface_ignores_not_found_only()
    {
        Assert.True(ControlPlaneService.IsAbsentPluginSurface(
            new ControlPlaneException(ProtocolErrorCodes.NotFound, "Pane not found: p1")));
        Assert.True(ControlPlaneService.IsAbsentPluginSurface(
            new ControlPlaneException(ProtocolErrorCodes.NotFound, ControlPlaneService.PopupNotOpenMessage)));
        Assert.False(ControlPlaneService.IsAbsentPluginSurface(
            new ControlPlaneException(ProtocolErrorCodes.InvalidState, "Session is frozen_read_only; pane.close is not allowed")));
    }

    private (ControlPlaneService Cp, AppState App) CreatePluginControlPlane(IPaneRuntimeFactory factory)
    {
        var app = new AppState(SessionId.New("plugin-send-text"));
        app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var cp = new ControlPlaneService(
            app,
            factory,
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
        var paneId = created.GetProperty("pane").GetProperty("pane_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(paneId));
        return paneId!;
    }

    private static JsonElement SendTextParams(string paneId, string text) =>
        JsonDocument.Parse(
            $$"""{"pane_id":{{JsonSerializer.Serialize(paneId)}},"text":{{JsonSerializer.Serialize(text)}}}""")
            .RootElement;

    private string WritePlugin(string id, string manifest)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, PluginHostService.ManifestFileName);
        _files.WriteAllText(path, manifest);
        return dir;
    }

    private static string DefaultManifest(string id, string min = "0.1.0") =>
        $"""
        id = "{id}"
        name = "Test {id}"
        version = "0.1.0"
        min_hypa_version = "{min}"
        platforms = ["linux", "macos"]
        """;

    private static string LinkHandlerManifest() =>
        """

        [[actions]]
        id = "open"
        title = "Open"
        command = ["/bin/echo", "ok"]

        [[link_handlers]]
        id = "github-issue"
        title = "Open GitHub issue"
        pattern = "^https://github\\.com/[^/]+/[^/]+/(issues|pull)/[0-9]+$"
        action = "open"
        """;

    private static NativeAgentSessionRef SampleSession(string? sessionStartSource = null) =>
        new()
        {
            Kind = NativeAgentSessionRef.KindId,
            Value = "sess-1",
            Source = "plugin:claude",
            Agent = "claude",
            SessionStartSource = sessionStartSource,
        };

    private static PluginWorktreeContext EmptyWorktree() =>
        new()
        {
            RepoKey = "",
            RepoName = "",
            RepoRoot = "",
            CheckoutPath = "",
            IsLinkedWorktree = false,
        };

    private static WorktreeSpaceMembership EmptyMembership() =>
        new()
        {
            Key = "",
            Label = "",
            RepoRoot = "",
            CheckoutPath = "",
            IsLinkedWorktree = false,
        };

    private static PluginWorktreeContext SampleWorktree(string suffix = "a") =>
        new()
        {
            RepoKey = "key-" + suffix,
            RepoName = "repo-" + suffix,
            RepoRoot = "/repo/" + suffix,
            CheckoutPath = "/repo/" + suffix + "/wt",
            IsLinkedWorktree = true,
        };

    private static WorktreeSpaceMembership SampleMembership(string suffix) =>
        new()
        {
            Key = "key-" + suffix,
            Label = "repo-" + suffix,
            RepoRoot = "/repo/" + suffix,
            CheckoutPath = "/repo/" + suffix + "/wt",
            IsLinkedWorktree = true,
        };

    private static WorktreeInfo SampleWorktreeRow(string suffix, string workspaceId) =>
        new()
        {
            Path = "/repo/" + suffix + "/wt",
            Branch = "worktree/" + suffix,
            IsBare = false,
            IsDetached = false,
            IsPrunable = false,
            IsLinkedWorktree = true,
            OpenWorkspaceId = workspaceId,
            Label = "repo-" + suffix,
        };

    private (ControlPlaneService Cp, AppState App, PluginHostService Host) CreateEventControlPlane(
        IRuntimeEventJournal? journal = null)
    {
        var deferred = new DeferredContextSource();
        var host = new PluginHostService(
            _files,
            _clock,
            new SystemPluginPathRoots(_root),
            new PluginManifestParser(),
            new FilePluginRegistry(_files, new SystemPluginPathRoots(_root)),
            _launcher,
            deferred,
            processRegistry: _processRegistry);
        var app = new AppState(SessionId.New("plugin-wt-event"));
        app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var cp = new ControlPlaneService(
            app,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            journal: journal,
            plugins: host);
        deferred.Inner = new ControlPlaneContextAdapter(cp);
        return (cp, app, host);
    }

    private static async Task FocusWorkspaceAsync(ControlPlaneService cp, string workspaceId) =>
        await cp.DispatchAsync(
            ProtocolMethods.WorkspaceFocus,
            JsonDocument.Parse($$"""{"workspace_id":{{JsonSerializer.Serialize(workspaceId)}}}""").RootElement,
            CancellationToken.None);

    private sealed class FakePluginClock : IPluginClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch.AddDays(1);
    }

    private sealed class DeferredContextSource : IPluginContextSource
    {
        public IPluginContextSource? Inner { get; set; }

        public PluginInvocationContext Current(string correlationId) =>
            Inner?.Current(correlationId)
            ?? new PluginInvocationContext { CorrelationId = correlationId, InvocationSource = "api", SelectedText = null };

        public PluginInvocationContext ForEvent(string hookName, string eventJson, string correlationId) =>
            Inner?.ForEvent(hookName, eventJson, correlationId)
            ?? Current(correlationId) with { InvocationSource = hookName };
    }

    private sealed class ControlPlaneContextAdapter(ControlPlaneService cp) : IPluginContextSource
    {
        public PluginInvocationContext Current(string correlationId) =>
            cp.CurrentPluginContext(correlationId);

        public PluginInvocationContext ForEvent(string hookName, string eventJson, string correlationId) =>
            cp.PluginContextForEvent(hookName, eventJson, correlationId);
    }

    private sealed class MemoryJournal : IRuntimeEventJournal
    {
        private long _seq = 1;
        public List<RuntimeEventRecord> Records { get; } = [];

        public Task<RuntimeResult<JournalHealth>> RecoverAsync(CancellationToken ct = default) =>
            Task.FromResult(RuntimeResult<JournalHealth>.Ok(GetHealth()));

        public Task<RuntimeResult<RuntimeEventRecord>> AppendAsync(
            EventClass @class,
            EventReliability reliability,
            string type,
            string payloadJson,
            DateTimeOffset? occurredAt = null,
            CancellationToken ct = default)
        {
            var rec = new RuntimeEventRecord
            {
                Seq = _seq++,
                Class = @class,
                Reliability = reliability,
                Type = type,
                OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
                PayloadJson = payloadJson,
            };
            Records.Add(rec);
            return Task.FromResult(RuntimeResult<RuntimeEventRecord>.Ok(rec));
        }

        public Task<RuntimeResult<IReadOnlyList<RuntimeEventRecord>>> ReadRangeAsync(
            long fromSeqExclusive,
            IReadOnlySet<EventClass>? classes,
            int budget,
            CancellationToken ct = default) =>
            Task.FromResult(RuntimeResult<IReadOnlyList<RuntimeEventRecord>>.Ok(
                Records.Where(r => r.Seq > fromSeqExclusive).Take(budget).ToList()));

        public Task<RuntimeResult<RuntimeUnit>> CloseOpenSegmentAsync(CancellationToken ct = default) =>
            Task.FromResult(RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value));

        public JournalHealth GetHealth() => new()
        {
            NextSeq = _seq,
            ReplayComplete = true,
            Bytes = 0,
        };

        public long NextSeq => _seq;
    }

    private sealed class WorktreeContextSource : IPluginContextSource
    {
        public PluginWorktreeContext? Worktree { get; init; }

        public PluginInvocationContext Current(string correlationId) =>
            new()
            {
                CorrelationId = correlationId,
                InvocationSource = "api",
                Worktree = Worktree,
                SelectedText = null,
            };

        public PluginInvocationContext ForEvent(string hookName, string eventJson, string correlationId)
        {
            _ = eventJson;
            return Current(correlationId) with { InvocationSource = hookName };
        }
    }

    private sealed class RecordingContextSource : IPluginContextSource
    {
        public int ForEventCalls { get; private set; }

        public PluginInvocationContext Current(string correlationId) =>
            new() { CorrelationId = correlationId, InvocationSource = "api", SelectedText = null };

        public PluginInvocationContext ForEvent(string hookName, string eventJson, string correlationId)
        {
            ForEventCalls++;
            return Current(correlationId) with { InvocationSource = hookName };
        }
    }

    private sealed class RecordingLauncher : IPluginProcessLauncher
    {
        public List<Launch> Starts { get; } = [];

        public PluginProcessExit Exit { get; set; } = new(0, "", "", null);

        public bool CompleteSynchronously { get; set; } = true;

        public bool TryStart(
            string program,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment,
            int outputCapBytes,
            Action<PluginProcessExit> onExit,
            out string? error)
        {
            error = null;
            Starts.Add(new Launch(program, arguments.ToArray(), workingDirectory, new Dictionary<string, string>(environment)));
            if (CompleteSynchronously)
                onExit(Exit);
            return true;
        }

        public sealed record Launch(
            string Program,
            IReadOnlyList<string> Arguments,
            string Cwd,
            IReadOnlyDictionary<string, string> Environment);
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
