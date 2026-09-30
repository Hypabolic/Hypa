using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.Plugins;
using Hypa.AgentRuntime.Infrastructure.Config;
using Hypa.AgentRuntime.Infrastructure.Plugins;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach.Keys;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class BundledAnnotatePluginTests : IDisposable
{
    private readonly string _root;
    private readonly SystemPluginFiles _files;
    private readonly SystemPluginPathRoots _paths;
    private readonly BundledPluginService _bundled;
    private readonly RecordingLauncher _launcher;
    private readonly PluginHostService _host;

    public BundledAnnotatePluginTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hypa-bundled-annotate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        BundledAnnotateFixtures.WriteHypaPair(_root);
        _files = new SystemPluginFiles();
        _paths = new SystemPluginPathRoots(_root);
        _bundled = new BundledPluginService(_files, _paths);
        _launcher = new RecordingLauncher();
        _host = new PluginHostService(
            _files,
            new SystemPluginClock(),
            _paths,
            new PluginManifestParser(),
            new FilePluginRegistry(_files, _paths),
            _launcher,
            binPath: Path.Combine(_root, "hypa"));
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
    public void Stage_writes_manifest_with_relative_annotate_program()
    {
        var productDir = Path.Combine(_root, "hypa-bin");
        var binary = BundledAnnotateFixtures.WriteHypaPair(productDir);
        var staged = _bundled.StageAnnotate(binary);
        Assert.True(staged.IsOk, staged.IsOk ? "" : staged.Error.Message);
        Assert.True(File.Exists(staged.Value.ManifestPath));
        var stagedProgram = BundledPluginLayout.AnnotateProgramStagePath(staged.Value.PluginRoot);
        Assert.True(File.Exists(stagedProgram));
        Assert.True(Path.IsPathRooted(staged.Value.Command0));
        Assert.Equal(Path.GetFullPath(stagedProgram), staged.Value.Command0);

        var text = File.ReadAllText(staged.Value.ManifestPath);
        Assert.Contains("id = \"annotate\"", text, StringComparison.Ordinal);
        Assert.Contains("platforms = [\"linux\", \"macos\"]", text, StringComparison.Ordinal);
        Assert.Contains("pane.open:self", text, StringComparison.Ordinal);
        Assert.Contains("pane.send_text:self", text, StringComparison.Ordinal);
        Assert.Contains("notification.request", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[[build]]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("HERDR_", text, StringComparison.Ordinal);
        Assert.DoesNotContain("__HYPA_BIN__", text, StringComparison.Ordinal);
        Assert.DoesNotContain("testhost", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(BundledPluginLayout.AnnotateProgramRelativeCommand, text, StringComparison.Ordinal);

        var parsed = new PluginManifestParser().Parse(text);
        Assert.True(parsed.IsOk, parsed.IsOk ? "" : parsed.Error.Message);
        Assert.Equal("annotate", parsed.Value.Id);
        Assert.Empty(parsed.Value.Build);
        Assert.Equal(["linux", "macos"], parsed.Value.Platforms);
        Assert.Equal(
            [PluginGrantCatalog.PaneOpenSelf, PluginGrantCatalog.PaneSendTextSelf, PluginGrantCatalog.NotificationRequest],
            parsed.Value.RequestedGrants);
        Assert.Equal(5, parsed.Value.Actions.Count);
        var copyContext = Assert.Single(parsed.Value.Actions, action => action.Id == "copy-context");
        Assert.Equal(BundledPluginLayout.AnnotateProgramRelativeCommand, copyContext.Command[0]);
        Assert.Equal(["copy-context"], copyContext.Command.Skip(1).ToArray());
        var capture = Assert.Single(parsed.Value.Actions, action => action.Id == "capture");
        Assert.Equal(BundledPluginLayout.AnnotateProgramRelativeCommand, capture.Command[0]);
        Assert.Equal(["capture"], capture.Command.Skip(1).ToArray());
        Assert.Equal(["pane"], capture.Contexts);
        var last = Assert.Single(parsed.Value.Actions, action => action.Id == "last");
        Assert.Equal(["last"], last.Command.Skip(1).ToArray());
        Assert.Contains(parsed.Value.Actions, action => action.Id == "copy-archive");
        Assert.Contains(parsed.Value.Actions, action => action.Id == "manage");
        var editor = Assert.Single(parsed.Value.Panes, pane => pane.Id == "editor");
        Assert.Equal("popup", editor.Placement);
        Assert.Equal(BundledPluginLayout.AnnotateProgramRelativeCommand, editor.Command[0]);
        Assert.Equal(["editor"], editor.Command.Skip(1).ToArray());
        var review = Assert.Single(parsed.Value.Panes, pane => pane.Id == "last-review");
        Assert.Equal("popup", review.Placement);
        Assert.Equal(["last-review"], review.Command.Skip(1).ToArray());
        var manager = Assert.Single(parsed.Value.Panes, pane => pane.Id == "manager");
        Assert.Equal("popup", manager.Placement);
        Assert.Equal(["manager"], manager.Command.Skip(1).ToArray());
    }

    [Fact]
    public void Relative_executable_path_fails_closed()
    {
        var staged = _bundled.StageAnnotate("hypa");
        Assert.False(staged.IsOk);
        Assert.Equal(PluginError.InvalidCommand, staged.Error.Code);
    }

    [Fact]
    public void Missing_annotate_program_fails_closed()
    {
        var hypaOnly = Path.Combine(_root, "hypa-only");
        Directory.CreateDirectory(hypaOnly);
        var hypa = Path.Combine(hypaOnly, "hypa");
        File.WriteAllText(hypa, string.Empty);
        var staged = _bundled.StageAnnotate(hypa);
        Assert.False(staged.IsOk);
        Assert.Equal(PluginError.InvalidCommand, staged.Error.Code);
        Assert.Contains("hypa-annotate", staged.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Link_lists_enabled_plugin_and_trust_preview_shows_pane_open()
    {
        var binary = Path.Combine(_root, "hypa");
        var staged = _bundled.StageAnnotate(binary);
        Assert.True(staged.IsOk, staged.IsOk ? "" : staged.Error.Message);

        var linked = _host.Link(staged.Value.PluginRoot, enabled: true);
        Assert.True(linked.IsOk, linked.IsOk ? "" : linked.Error.Message);
        Assert.Equal("annotate", linked.Value.Plugin.PluginId);
        Assert.True(linked.Value.Plugin.Enabled);
        Assert.Contains(linked.Value.TrustPreview.Grants, g => g == PluginGrantCatalog.PaneOpenSelf);
        Assert.Contains(linked.Value.TrustPreview.Grants, g => g == PluginGrantCatalog.PaneSendTextSelf);
        Assert.Empty(linked.Value.Plugin.Build);

        var listed = _host.List("annotate");
        Assert.True(listed.IsOk);
        var plugin = Assert.Single(listed.Value);
        Assert.True(plugin.Enabled);
        Assert.Equal("annotate", plugin.PluginId);
    }

    [Fact]
    public void Action_invoke_runs_annotate_program_and_injects_plugin_root_and_state_dir()
    {
        var binary = Path.Combine(_root, "hypa");
        var staged = _bundled.StageAnnotate(binary);
        Assert.True(staged.IsOk);
        Assert.True(_host.Link(staged.Value.PluginRoot, true).IsOk);

        var invoked = _host.InvokeAction("copy-context", "annotate", null);
        Assert.True(invoked.IsOk, invoked.IsOk ? "" : invoked.Error.Message);
        var start = Assert.Single(_launcher.Starts);
        Assert.Equal(Path.GetFullPath(staged.Value.Command0), start.Program);
        Assert.Equal(["copy-context"], start.Arguments);
        Assert.Equal(staged.Value.PluginRoot, start.Environment[PluginEnv.PluginRoot]);
        Assert.Equal(_paths.PluginStateDir("annotate"), start.Environment[PluginEnv.StateDir]);
        Assert.Equal(Path.GetFullPath(binary), start.Environment[PluginEnv.BinPath]);
        Assert.DoesNotContain(start.Environment.Keys, k => k.StartsWith("HERDR_", StringComparison.Ordinal));
    }

    [Fact]
    public void Uninstall_unlinks_and_removes_root()
    {
        var binary = Path.Combine(_root, "hypa");
        var staged = _bundled.StageAnnotate(binary);
        Assert.True(staged.IsOk);
        Assert.True(_host.Link(staged.Value.PluginRoot, true).IsOk);
        Assert.True(Directory.Exists(staged.Value.PluginRoot));

        var unlinked = _host.Unlink("annotate");
        Assert.True(unlinked.IsOk);
        Assert.True(unlinked.Value.Removed);
        Assert.Empty(_host.List(null).Value);

        var removed = _bundled.RemoveAnnotate();
        Assert.True(removed.IsOk);
        Assert.True(removed.Value.Removed);
        Assert.False(Directory.Exists(staged.Value.PluginRoot));
        Assert.False(_bundled.InspectAnnotate().Staged);
    }

    [Fact]
    public async Task Cli_rejects_bundled_action_verb()
    {
        await using var client = new ControlPlaneClient("/tmp/hypa-unused.sock");
        var ex = await Assert.ThrowsAsync<ControlPlaneCliUsageException>(() =>
            ControlPlaneCliCommands.PluginAsync(
                client,
                ["bundled", "action", "capture"],
                _bundled,
                Path.Combine(_root, "hypa")));
        Assert.Contains("Unknown plugin bundled subcommand: action", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plugin_action_key_binding_invokes_copy_context()
    {
        var binary = Path.Combine(_root, "hypa");
        var staged = _bundled.StageAnnotate(binary);
        Assert.True(staged.IsOk);
        Assert.True(_host.Link(staged.Value.PluginRoot, true).IsOk);

        var port = new HostAttachPort(_host);
        var dispatcher = new AttachCommandDispatcher(port, "w1", "t1", "p1")
        {
            Commands = [new KeyCommandBinding("prefix+a", "copy-context", "plugin_action")],
        };
        await dispatcher.HandleAsync(new KeyActionRequest(KeyActionId.Command, 0), CancellationToken.None);

        var start = Assert.Single(_launcher.Starts);
        Assert.Equal(["copy-context"], start.Arguments);
        Assert.Equal(Path.GetFullPath(staged.Value.Command0), start.Program);
        Assert.Equal(staged.Value.PluginRoot, start.Environment[PluginEnv.PluginRoot]);
        Assert.Equal(_paths.PluginStateDir("annotate"), start.Environment[PluginEnv.StateDir]);
        Assert.Contains(port.Calls, c => c == ProtocolMethods.PluginActionInvoke);
    }

    [Fact]
    public void Attach_config_accepts_plugin_action_command_type()
    {
        var bound = TomlAttachConfigBinder.Bind("""
            [[keys.command]]
            key = "prefix+a"
            type = "plugin_action"
            command = "copy-context"
            """);
        Assert.True(bound.IsOk, bound.IsOk ? "" : string.Join("; ", bound.Errors.Select(e => e.Message)));
        var command = Assert.Single(bound.Value.Keys.Commands);
        Assert.Equal("plugin_action", command.Type);
        Assert.Equal("copy-context", command.Command);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("uninstall")]
    [InlineData("status")]
    public async Task Cli_bare_bundled_verb_does_not_pick_a_plugin(string verb)
    {
        await using var client = new ControlPlaneClient("/tmp/hypa-unused.sock");
        var ex = await Assert.ThrowsAsync<ControlPlaneCliUsageException>(() =>
            ControlPlaneCliCommands.PluginAsync(
                client,
                ["bundled", verb],
                _bundled,
                Path.Combine(_root, "hypa")));
        Assert.Contains(
            "usage: hypa plugin bundled install|uninstall|status <plugin-id>",
            ex.Message,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(BundledPluginLayout.AnnotateRoot(_paths)));
        Assert.False(Directory.Exists(BundledPluginLayout.SlotsRoot(_paths)));
    }

    [Theory]
    [InlineData("install")]
    [InlineData("uninstall")]
    [InlineData("status")]
    public async Task Cli_rejects_slots_test_fixture(string verb)
    {
        await using var client = new ControlPlaneClient("/tmp/hypa-unused.sock");
        var ex = await Assert.ThrowsAsync<ControlPlaneCliUsageException>(() =>
            ControlPlaneCliCommands.PluginAsync(
                client,
                ["bundled", verb, "slots"],
                _bundled,
                Path.Combine(_root, "hypa")));
        Assert.Contains("Unknown bundled plugin: slots", ex.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Cli_install_links_enabled_plugin_and_uninstall_removes_root()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        var sockDir = Path.Combine(Path.GetTempPath(), "h196" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sockDir);
        var sock = Path.Combine(sockDir, "s.sock");
        var binary = Path.Combine(_root, "hypa");
        var app = new AppState(SessionId.New("bundled-annotate"));
        app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var cp = new ControlPlaneService(
            app,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            stateDirectory: _root,
            plugins: _host);
        await using var server = new UnixSocketServer(cp, sock);
        await server.StartAsync(CancellationToken.None);
        await using var client = new ControlPlaneClient(sock, connectTimeout: TimeSpan.FromSeconds(2));
        await client.ConnectAsync();
        try
        {
            var installed = await ControlPlaneCliCommands.PluginAsync(
                client,
                ["bundled", "install", "annotate"],
                _bundled,
                binary);
            Assert.Equal("annotate", installed.GetProperty("plugin").GetProperty("plugin_id").GetString());
            Assert.True(installed.GetProperty("plugin").GetProperty("enabled").GetBoolean());
            Assert.Contains(
                installed.GetProperty("trust_preview").GetProperty("grants").EnumerateArray().Select(g => g.GetString()),
                g => g == PluginGrantCatalog.PaneOpenSelf);
            var command0 = installed.GetProperty("plugin").GetProperty("actions")
                .EnumerateArray()
                .Single(a => a.GetProperty("id").GetString() == "copy-context")
                .GetProperty("command")[0]
                .GetString();
            Assert.Equal(BundledPluginLayout.AnnotateProgramRelativeCommand, command0);
            Assert.True(File.Exists(BundledPluginLayout.AnnotateProgramStagePath(BundledPluginLayout.AnnotateRoot(_paths))));

            var listed = await ControlPlaneCliCommands.PluginAsync(client, ["list"], _bundled, binary);
            var plugin = Assert.Single(
                listed.GetProperty("plugins").EnumerateArray(),
                p => p.GetProperty("plugin_id").GetString() == "annotate");
            Assert.True(plugin.GetProperty("enabled").GetBoolean());

            var status = await ControlPlaneCliCommands.PluginAsync(
                client,
                ["bundled", "status", "annotate"],
                _bundled,
                binary);
            Assert.True(status.GetProperty("staged").GetBoolean());
            Assert.True(status.GetProperty("linked").GetBoolean());
            Assert.True(status.GetProperty("enabled").GetBoolean());

            var root = BundledPluginLayout.AnnotateRoot(_paths);
            Assert.True(Directory.Exists(root));
            var uninstalled = await ControlPlaneCliCommands.PluginAsync(
                client,
                ["bundled", "uninstall", "annotate"],
                _bundled,
                binary);
            Assert.True(uninstalled.GetProperty("unlinked").GetBoolean());
            Assert.True(uninstalled.GetProperty("removed").GetBoolean());
            Assert.False(Directory.Exists(root));
            Assert.Empty(_host.List(null).Value);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
            try
            {
                Directory.Delete(sockDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class HostAttachPort(PluginHostService host) : IAttachCommandPort
    {
        public List<string> Calls { get; } = [];

        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            Calls.Add(method);
            if (method != ProtocolMethods.PluginActionInvoke)
                return Task.FromResult(Empty());

            var actionId = parameters?["action_id"]?.GetValue<string>();
            var pluginId = parameters?["plugin_id"]?.GetValue<string>();
            var result = host.InvokeAction(actionId ?? "", pluginId, null);
            if (!result.IsOk)
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.InvalidParams,
                    result.Error.Message,
                    result.Error.Code);
            }

            return Task.FromResult(Empty());
        }

        private static JsonElement Empty()
        {
            using var doc = JsonDocument.Parse("{}");
            return doc.RootElement.Clone();
        }
    }

    private sealed class RecordingLauncher : IPluginProcessLauncher
    {
        public List<Launch> Starts { get; } = [];

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
            Starts.Add(new Launch(
                program,
                arguments.ToArray(),
                workingDirectory,
                new Dictionary<string, string>(environment)));
            onExit(new PluginProcessExit(0, "", "", null));
            return true;
        }

        public sealed record Launch(
            string Program,
            IReadOnlyList<string> Arguments,
            string Cwd,
            IReadOnlyDictionary<string, string> Environment);
    }
}
