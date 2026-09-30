using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Commands;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// Program.cs uses <see cref="ControlPlaneCliCommands.IsClientCommand"/>
/// before System.CommandLine. These cases must match that predicate.
/// </summary>
public sealed class ControlPlaneClientCommandTests
{
    [Theory]
    [InlineData("-c", "ping")]
    [InlineData("-c", "ping", "-c", "1", "127.0.0.1")]
    [InlineData("-t", "ping")]
    [InlineData("-c", "echo hello")]
    [InlineData("attach")]
    [InlineData("git", "status")]
    [InlineData("doctor")]
    [InlineData("--timeout-ms", "1000", "-c", "ping")]
    public void CompressionAndNonMuxArgv_IsNotClientCommand(params string[] args)
    {
        Assert.False(ControlPlaneCliCommands.IsClientCommand(args));
    }

    [Theory]
    [InlineData("ping")]
    [InlineData("snapshot")]
    [InlineData("pane", "list")]
    [InlineData("workspace", "list")]
    [InlineData("agent", "list")]
    [InlineData("integration", "status")]
    [InlineData("plugin", "list")]
    [InlineData("rpc", "ping")]
    [InlineData("events", "wait")]
    [InlineData("tab", "list")]
    [InlineData("layout", "export")]
    [InlineData("pane", "split")]
    [InlineData("agent", "start")]
    [InlineData("--session", "x", "ping")]
    [InlineData("--socket", "/tmp/hypa.sock", "ping")]
    [InlineData("--session", "x", "--socket", "/tmp/hypa.sock", "pane", "list")]
    [InlineData("pane", "run", "p1", "grep", "-c", "TOKEN")]
    [InlineData("pane", "run", "p1", "tail", "-t", "5", "log")]
    public void MuxVerbs_AreClientCommands(params string[] args)
    {
        Assert.True(ControlPlaneCliCommands.IsClientCommand(args));
    }

    [Fact]
    public void MuxClientCommand_BuildAll_lists_tab_and_layout()
    {
        var names = new MuxClientCommand().BuildAll().Select(c => c.Name).ToArray();
        Assert.Contains("tab", names);
        Assert.Contains("layout", names);
        Assert.Contains("workspace", names);
        Assert.Contains("pane", names);
        Assert.Contains("agent", names);
        Assert.Contains("integration", names);
        Assert.Contains("plugin", names);
        Assert.Contains("events", names);
        Assert.Contains("rpc", names);
    }

    [Fact]
    public void Help_lists_bundled_plugin_verbs()
    {
        var help = ControlPlaneCliCommands.GetHelpText();
        Assert.Contains("plugin bundled install <plugin-id>", help, StringComparison.Ordinal);
        Assert.Contains("plugin bundled uninstall <plugin-id>", help, StringComparison.Ordinal);
        Assert.Contains("plugin bundled status <plugin-id>", help, StringComparison.Ordinal);
        Assert.DoesNotContain("slots", help, StringComparison.Ordinal);
        Assert.DoesNotContain("plugin bundled action", help, StringComparison.Ordinal);
        Assert.Contains(
            "plugin pane open <plugin_id> <entrypoint> [--placement overlay|popup|split|tab|zoomed] [--width SIZE] [--height SIZE] [--cwd PATH] [--env KEY=VALUE] [--target-pane PANE] [--focus|--no-focus]",
            help,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Plugin_resource_cli_parses_without_attach()
    {
        var list = ControlPlaneCliCommands.Parse(["plugin", "resource", "list", "--plugin", "example.res"]);
        Assert.Equal(["plugin", "resource", "list"], list.Tokens);
        Assert.Equal("example.res", list.Flags["--plugin"]);

        var get = ControlPlaneCliCommands.Parse(["plugin", "resource", "get", "plugin:example.res/queue"]);
        Assert.Equal("plugin:example.res/queue", get.Tokens[3]);

        var envelope = """{"resource_id":"plugin:example.res/queue","schema":"hypa.projection.collection.v1","revision":1,"value":{"items":[]}}""";
        var publish = ControlPlaneCliCommands.BuildPluginResourcePublishParams([envelope]);
        Assert.Equal("plugin:example.res/queue", publish["resource_id"]!.GetValue<string>());
        Assert.Equal(1, publish["revision"]!.GetValue<long>());

        var help = ControlPlaneCliCommands.GetHelpText();
        Assert.Contains("plugin resource list", help, StringComparison.Ordinal);
        Assert.Contains("plugin resource get", help, StringComparison.Ordinal);
        Assert.Contains("plugin resource publish", help, StringComparison.Ordinal);
        Assert.Contains("plugin resource remove", help, StringComparison.Ordinal);
        Assert.DoesNotContain("sidebar.", help, StringComparison.Ordinal);
        Assert.DoesNotContain("toast.show", help, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyArgv_IsNotClientCommand()
    {
        Assert.False(ControlPlaneCliCommands.IsClientCommand([]));
    }

    [Fact]
    public async Task Workspace_focus_rename_close_parse()
    {
        var focus = ControlPlaneCliCommands.Parse(["workspace", "focus", "w1"]);
        Assert.Equal(["workspace", "focus", "w1"], focus.Tokens);

        var rename = ControlPlaneCliCommands.Parse(
            ["workspace", "rename", "w1", "--label", "docs"]);
        Assert.Equal(["workspace", "rename", "w1"], rename.Tokens);
        Assert.Equal("docs", rename.Flags["--label"]);

        var close = ControlPlaneCliCommands.Parse(["workspace", "close", "w1"]);
        Assert.Equal(["workspace", "close", "w1"], close.Tokens);

        var help = ControlPlaneCliCommands.GetHelpText();
        Assert.Contains("workspace focus", help, StringComparison.Ordinal);
        Assert.Contains("workspace rename", help, StringComparison.Ordinal);
        Assert.Contains("workspace close", help, StringComparison.Ordinal);

        var parsed = ControlPlaneCliCommands.Parse(["workspace", "rename", "w1"]);
        var ex = await Assert.ThrowsAsync<ControlPlaneCliUsageException>(() =>
            ControlPlaneCliCommands.WorkspaceAsync(
                null!, parsed.Tokens.Skip(1).ToArray(), parsed, ["workspace", "rename", "w1"]));
        Assert.Contains("--label", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Workspace_move_move_block_and_report_metadata_parse()
    {
        var move = ControlPlaneCliCommands.Parse(["workspace", "move", "w1", "--index", "2"]);
        Assert.Equal(["workspace", "move", "w1"], move.Tokens);
        Assert.Equal("2", move.Flags["--index"]);

        var block = ControlPlaneCliCommands.Parse(
            ["workspace", "move-block", "--workspace-id", "w2", "--workspace-id", "w1", "--before", "w3"]);
        Assert.Equal("move-block", block.Tokens[1]);
        var blockParams = ControlPlaneCliCommands.BuildWorkspaceMoveBlockParams(
            ["workspace", "move-block", "--workspace-id", "w2", "--workspace-id", "w1", "--before", "w3"]);
        var ids = Assert.IsType<JsonArray>(blockParams["workspace_ids"]);
        Assert.Equal("w2", ids[0]!.GetValue<string>());
        Assert.Equal("w1", ids[1]!.GetValue<string>());
        Assert.Equal("w3", blockParams["before_workspace_id"]!.GetValue<string>());

        var report = ControlPlaneCliCommands.Parse(
            ["workspace", "report-metadata", "w1", "--source", "git", "--token", "jj_status=working", "--ttl-ms", "1000", "--seq", "2"]);
        var reportParams = ControlPlaneCliCommands.BuildWorkspaceReportMetadataParams(
            report.Tokens.Skip(1).ToArray(),
            report,
            ["workspace", "report-metadata", "w1", "--source", "git", "--token", "jj_status=working", "--clear-token", "old", "--ttl-ms", "1000", "--seq", "2"]);
        Assert.Equal("w1", reportParams["workspace_id"]!.GetValue<string>());
        Assert.Equal("git", reportParams["source"]!.GetValue<string>());
        Assert.Equal("working", reportParams["tokens"]!["jj_status"]!.GetValue<string>());
        Assert.Null(reportParams["tokens"]!["old"]);
        Assert.Equal(1000, reportParams["ttl_ms"]!.GetValue<int>());
        Assert.Equal(2, reportParams["seq"]!.GetValue<long>());

        var help = ControlPlaneCliCommands.GetHelpText();
        Assert.Contains("workspace move", help, StringComparison.Ordinal);
        Assert.Contains("workspace move-block", help, StringComparison.Ordinal);
        Assert.Contains("workspace report-metadata", help, StringComparison.Ordinal);

        var missingIndex = ControlPlaneCliCommands.Parse(["workspace", "move", "w1"]);
        var ex = await Assert.ThrowsAsync<ControlPlaneCliUsageException>(() =>
            ControlPlaneCliCommands.WorkspaceAsync(
                null!, missingIndex.Tokens.Skip(1).ToArray(), missingIndex, ["workspace", "move", "w1"]));
        Assert.Contains("--index", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pane_report_agent_session_metadata_release_clear_parse()
    {
        var report = ControlPlaneCliCommands.Parse(
            ["pane", "report-agent", "p1", "--source", "plugin:claude", "--agent", "claude", "--state", "blocked", "--message", "wait", "--agent-session-id", "sess_1", "--session-start-source", "startup", "--seq", "2"]);
        var reportParams = ControlPlaneCliCommands.BuildPaneReportAgentParams(
            report.Tokens.Skip(1).ToArray(), report);
        Assert.Equal("p1", reportParams["pane_id"]!.GetValue<string>());
        Assert.Equal("plugin:claude", reportParams["source"]!.GetValue<string>());
        Assert.Equal("claude", reportParams["agent"]!.GetValue<string>());
        Assert.Equal("blocked", reportParams["state"]!.GetValue<string>());
        Assert.Equal("wait", reportParams["message"]!.GetValue<string>());
        Assert.Equal("sess_1", reportParams["agent_session_id"]!.GetValue<string>());
        Assert.Equal("startup", reportParams["session_start_source"]!.GetValue<string>());
        Assert.Equal(2, reportParams["seq"]!.GetValue<long>());

        var session = ControlPlaneCliCommands.Parse(
            ["pane", "report-agent-session", "p1", "--source", "plugin:claude", "--agent", "claude", "--agent-session-id", "sess_1", "--session-start-source", "startup"]);
        var sessionParams = ControlPlaneCliCommands.BuildPaneReportAgentSessionParams(
            session.Tokens.Skip(1).ToArray(), session);
        Assert.Equal("sess_1", sessionParams["agent_session_id"]!.GetValue<string>());
        Assert.Equal("startup", sessionParams["session_start_source"]!.GetValue<string>());

        var metadata = ControlPlaneCliCommands.Parse(
            ["pane", "report-metadata", "p1", "--source", "git", "--token", "name=claude", "--ttl-ms", "1000", "--seq", "3"]);
        var metadataParams = ControlPlaneCliCommands.BuildPaneReportMetadataParams(
            metadata.Tokens.Skip(1).ToArray(),
            metadata,
            ["pane", "report-metadata", "p1", "--source", "git", "--token", "name=claude", "--clear-token", "old", "--ttl-ms", "1000", "--seq", "3"]);
        Assert.Equal("claude", metadataParams["tokens"]!["name"]!.GetValue<string>());
        Assert.Null(metadataParams["tokens"]!["old"]);
        Assert.Equal(1000, metadataParams["ttl_ms"]!.GetValue<int>());

        var release = ControlPlaneCliCommands.Parse(
            ["pane", "release-agent", "p1", "--source", "plugin:claude", "--agent", "claude"]);
        var releaseParams = ControlPlaneCliCommands.BuildPaneReleaseAgentParams(
            release.Tokens.Skip(1).ToArray(), release);
        Assert.Equal("claude", releaseParams["agent"]!.GetValue<string>());

        var clear = ControlPlaneCliCommands.Parse(
            ["pane", "clear-agent-authority", "p1", "--source", "plugin:claude"]);
        var clearParams = ControlPlaneCliCommands.BuildPaneClearAgentAuthorityParams(
            clear.Tokens.Skip(1).ToArray(), clear);
        Assert.Equal("plugin:claude", clearParams["source"]!.GetValue<string>());

        var help = ControlPlaneCliCommands.GetHelpText();
        Assert.Contains("pane report-agent", help, StringComparison.Ordinal);
        Assert.Contains("pane report-agent-session", help, StringComparison.Ordinal);
        Assert.Contains("pane report-metadata", help, StringComparison.Ordinal);
        Assert.Contains("pane release-agent", help, StringComparison.Ordinal);
        Assert.Contains("pane clear-agent-authority", help, StringComparison.Ordinal);
        Assert.Contains("integration install", help, StringComparison.Ordinal);
        Assert.Contains("integration uninstall", help, StringComparison.Ordinal);
        Assert.Contains("integration uninstall --all", help, StringComparison.Ordinal);
        Assert.Contains("integration status", help, StringComparison.Ordinal);
        Assert.DoesNotContain("--detected", help, StringComparison.Ordinal);
    }

    [Fact]
    public void Tab_create_parses_workspace_label_and_no_pane()
    {
        var parsed = ControlPlaneCliCommands.Parse(
            ["tab", "create", "--workspace", "w1", "--label", "editor", "--no-pane", "--no-focus"]);
        var obj = ControlPlaneCliCommands.BuildTabCreateParams(parsed);
        Assert.Equal("w1", obj["workspace_id"]!.GetValue<string>());
        Assert.Equal("editor", obj["label"]!.GetValue<string>());
        Assert.False(obj["create_pane"]!.GetValue<bool>());
        Assert.False(obj["focus"]!.GetValue<bool>());
    }

    [Fact]
    public void Tab_move_parses_index()
    {
        var parsed = ControlPlaneCliCommands.Parse(["tab", "move", "tab_1", "--index", "2"]);
        Assert.Equal("2", parsed.Flags["--index"]);
        Assert.Equal("tab_1", parsed.Tokens[2]);
    }

    [Fact]
    public void Pane_split_parses_direction_ratio_and_args()
    {
        var parsed = ControlPlaneCliCommands.Parse(
            ["pane", "split", "p1", "--direction", "right", "--ratio", "0.25", "--command", "/bin/echo", "--args", "ok"]);
        var obj = ControlPlaneCliCommands.BuildPaneSplitParams(parsed, ["split", "p1"]);
        Assert.Equal("p1", obj["pane_id"]!.GetValue<string>());
        Assert.Equal("right", obj["direction"]!.GetValue<string>());
        Assert.Equal(0.25, obj["ratio"]!.GetValue<double>());
        Assert.Equal("/bin/echo", obj["command"]!.GetValue<string>());
        var args = Assert.IsType<JsonArray>(obj["args"]);
        Assert.Equal("ok", args[0]!.GetValue<string>());
    }

    [Fact]
    public void Agent_start_parses_occupant_flag()
    {
        var parsed = ControlPlaneCliCommands.Parse(["agent", "start", "p9", "--occupant", "pi"]);
        var obj = ControlPlaneCliCommands.BuildAgentStartParams(parsed, ["start", "p9"]);
        Assert.Equal("p9", obj["pane_id"]!.GetValue<string>());
        Assert.Equal("pi", obj["occupant"]!.GetValue<string>());
        Assert.False(obj.ContainsKey("command"));
    }

    [Fact]
    public void Agent_start_parses_positional_pane_id_and_omits_command_when_absent()
    {
        var parsed = ControlPlaneCliCommands.Parse(["agent", "start", "p9", "--args", "-lc", "--args", "echo hi"]);
        var obj = ControlPlaneCliCommands.BuildAgentStartParams(parsed, ["start", "p9"]);
        Assert.Equal("p9", obj["pane_id"]!.GetValue<string>());
        Assert.False(obj.ContainsKey("command"));
        var args = Assert.IsType<JsonArray>(obj["args"]);
        Assert.Equal("-lc", args[0]!.GetValue<string>());
        Assert.Equal("echo hi", args[1]!.GetValue<string>());
    }

    [Fact]
    public void Agent_explain_view_cli_builds_wire_params()
    {
        var explain = ControlPlaneCliCommands.Parse(["agent", "explain", "p1"]);
        var explainParams = ControlPlaneCliCommands.BuildAgentTargetParams(
            explain, ["explain", "p1"]);
        Assert.Equal("p1", explainParams["pane_id"]!.GetValue<string>());

        var rename = ControlPlaneCliCommands.Parse(["agent", "rename", "p1", "reviewer"]);
        var renameParams = ControlPlaneCliCommands.BuildAgentRenameParams(
            rename, ["rename", "p1", "reviewer"]);
        Assert.Equal("p1", renameParams["pane_id"]!.GetValue<string>());
        Assert.Equal("reviewer", renameParams["name"]!.GetValue<string>());

        var clear = ControlPlaneCliCommands.Parse(["agent", "rename", "p1", "--clear"]);
        var clearParams = ControlPlaneCliCommands.BuildAgentRenameParams(
            clear, ["rename", "p1"]);
        Assert.Equal("p1", clearParams["pane_id"]!.GetValue<string>());
        Assert.False(clearParams.ContainsKey("name"));

        var keys = ControlPlaneCliCommands.Parse(["agent", "send-keys", "p1", "up", "enter"]);
        var keyParams = ControlPlaneCliCommands.BuildAgentSendKeysParams(
            keys, ["send-keys", "p1", "up", "enter"], ["agent", "send-keys", "p1", "up", "enter"]);
        Assert.Equal("p1", keyParams["pane_id"]!.GetValue<string>());
        var keyArr = Assert.IsType<JsonArray>(keyParams["keys"]);
        Assert.Equal("up", keyArr[0]!.GetValue<string>());
        Assert.Equal("enter", keyArr[1]!.GetValue<string>());

        var view = ControlPlaneCliCommands.Parse(
            ["agent", "view", "set", "--source", "example.views", "--label", "working"]);
        var viewParams = ControlPlaneCliCommands.BuildAgentViewSetParams(view);
        Assert.Equal("example.views", viewParams["source"]!.GetValue<string>());
        Assert.Equal("working", viewParams["label"]!.GetValue<string>());

        var control = ControlPlaneCliCommands.BuildTerminalControlParams("p1", "lease_1", "sub_1");
        Assert.Equal("p1", control["pane_id"]!.GetValue<string>());
        Assert.Equal("lease_1", control["lease_id"]!.GetValue<string>());
        Assert.Equal("sub_1", control["subscription_id"]!.GetValue<string>());
        Assert.Contains(ProtocolMethods.TerminalControl, ControlPlaneCliCommands.AgentAttachMethodSequence);
        Assert.DoesNotContain("agent.attach", ControlPlaneCliCommands.AgentAttachMethodSequence);
        Assert.DoesNotContain("agent.attach", ProtocolMethods.All);

        var help = ControlPlaneCliCommands.GetHelpText();
        Assert.Contains("agent explain", help, StringComparison.Ordinal);
        Assert.Contains("agent attach", help, StringComparison.Ordinal);
        Assert.Contains("agent view set", help, StringComparison.Ordinal);
        Assert.Contains("events wait", help, StringComparison.Ordinal);
        Assert.Contains("timed out waiting for event match", help, StringComparison.Ordinal);

        var waitParams = ControlPlaneCliCommands.BuildEventsWaitParams(
            ControlPlaneCliCommands.Parse(["events", "wait", "p1", "--agent-status", "blocked", "--timeout", "1500"]),
            ["wait", "p1"]);
        Assert.Equal("p1", waitParams["match_event"]!["pane_id"]!.GetValue<string>());
        Assert.Equal("blocked", waitParams["match_event"]!["agent_status"]!.GetValue<string>());
        Assert.Equal(
            EventsWaitMatch.PaneAgentStatusChanged,
            waitParams["match_event"]!["event"]!.GetValue<string>());
        Assert.Equal(1500, waitParams["timeout_ms"]!.GetValue<int>());
    }

    [Fact]
    public async Task Agent_attach_enters_live_attach_with_resolved_pane_and_does_not_print_control_json()
    {
        using var doc = JsonDocument.Parse("""{"pane_id":"pane-live","state":"working"}""");
        var paneId = ControlPlaneCliCommands.ReadAgentAttachPaneId(doc.RootElement);
        Assert.Equal("pane-live", paneId);

        var driver = new RecordingLiveAttachDriver();
        var supervisor = Substitute.For<IMuxSupervisor>();
        supervisor.EnsureReadyAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new MuxReadyInfo("default", "/tmp/hypa-agent-attach.sock", """{"ok":true}"""));
        ILiveAttachHost attach = new MuxAttachService(supervisor, driver);
        var parsed = ControlPlaneCliCommands.Parse(["agent", "attach", "pane-live"]);
        var captured = new StringWriter();
        var previous = Console.Out;
        Console.SetOut(captured);
        int code;
        try
        {
            code = await ControlPlaneCliCommands.EnterLiveAgentAttachAsync(
                paneId, parsed, attach, CancellationToken.None);
        }
        finally
        {
            Console.SetOut(previous);
        }

        Assert.Equal(0, code);
        Assert.Equal("pane-live", driver.Last?.FocusPaneId);
        Assert.DoesNotContain("lease_id", captured.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("subscription_id", captured.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ProtocolMethods.TerminalControl, captured.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("agent.attach", ProtocolMethods.All);
    }

    private sealed class RecordingLiveAttachDriver : IMuxAttachDriver
    {
        public MuxAttachRequest? Last { get; private set; }

        public Task<int> RunAsync(MuxReadyInfo ready, MuxAttachRequest request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(0);
        }
    }

    [Fact]
    public async Task Layout_apply_is_usage_error_pointing_at_rpc()
    {
        var help = ControlPlaneCliCommands.GetHelpText();
        Assert.Contains("tab create", help, StringComparison.Ordinal);
        Assert.Contains("layout export", help, StringComparison.Ordinal);
        Assert.Contains("pane split", help, StringComparison.Ordinal);
        Assert.Contains("agent start", help, StringComparison.Ordinal);
        Assert.Contains("rpc layout.apply", help, StringComparison.Ordinal);

        var parsed = ControlPlaneCliCommands.Parse(["layout", "apply"]);
        Assert.Equal("apply", parsed.Tokens[1]);
        var ex = await Assert.ThrowsAsync<ControlPlaneCliUsageException>(() =>
            ControlPlaneCliCommands.LayoutAsync(
                null!, parsed.Tokens.Skip(1).ToArray(), parsed, ["layout", "apply"]));
        Assert.Contains("hypa rpc layout.apply", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_flag_on_new_verbs_is_usage_error()
    {
        Assert.Throws<ControlPlaneCliUsageException>(() =>
            ControlPlaneCliCommands.Parse(["tab", "create", "--workspace", "w1", "--bogus", "x"]));
        Assert.Throws<ControlPlaneCliUsageException>(() =>
            ControlPlaneCliCommands.Parse(["pane", "split", "p1", "--direction", "right", "--unknown"]));
        Assert.Throws<ControlPlaneCliUsageException>(() =>
            ControlPlaneCliCommands.Parse(["agent", "start", "p1", "--nope"]));
    }

    [Fact]
    public async Task Integration_help_lists_bare_install_and_uninstall_all()
    {
        var parsed = ControlPlaneCliCommands.Parse(["integration", "--help"]);
        Assert.Contains("--help", parsed.Switches);
        var help = ControlPlaneCliCommands.FormatMuxGroupHelp(["integration"]);
        Assert.Contains("integration install [--dry-run]", help, StringComparison.Ordinal);
        Assert.Contains("integration uninstall --all", help, StringComparison.Ordinal);
        Assert.DoesNotContain("--detected", help, StringComparison.Ordinal);

        var bare = ControlPlaneCliCommands.Parse(["integration", "install", "--dry-run"]);
        Assert.Contains("--dry-run", bare.Switches);
        Assert.Equal("install", bare.Tokens[1]);
        Assert.Equal(2, bare.Tokens.Count);

        var removeAll = ControlPlaneCliCommands.Parse(["integration", "uninstall", "--all"]);
        Assert.Contains("--all", removeAll.Switches);
        Assert.Equal(["integration", "uninstall"], removeAll.Tokens);

        var stderr = new StringWriter();
        var previous = Console.Error;
        Console.SetError(stderr);
        try
        {
            Assert.Equal(4, await ControlPlaneCliCommands.Run(["integration", "install", "--detected"]));
            Assert.Equal(4, await ControlPlaneCliCommands.Run(["integration", "uninstall", "--detected"]));
            Assert.Equal(4, await ControlPlaneCliCommands.Run(["integration", "install", "--all"]));
            Assert.Equal(4, await ControlPlaneCliCommands.Run(["integration", "uninstall", "claude", "--all"]));
        }
        finally
        {
            Console.SetError(previous);
        }

        var errors = stderr.ToString();
        Assert.Contains("Unknown flag: --detected", errors, StringComparison.Ordinal);
        Assert.Contains("--all applies to integration uninstall", errors, StringComparison.Ordinal);
        Assert.Contains("usage: hypa integration uninstall --all", errors, StringComparison.Ordinal);
    }
}
