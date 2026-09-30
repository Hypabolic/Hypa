using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class AgentExplainViewTests
{
    private const string MuseWorking =
        "⟩ hello\n\n◆ Working (0s · esc to interrupt)\n\n────────────────\n⟩\n────────────────\ngpt-5.4 · minimal · /workspace";

    [Fact]
    public void Wire_catalog_does_not_include_agent_attach()
    {
        Assert.DoesNotContain("agent.attach", ProtocolMethods.All);
        Assert.Equal(
            [
                ProtocolMethods.AgentExplain,
                ProtocolMethods.AgentRename,
                ProtocolMethods.AgentFocus,
                ProtocolMethods.AgentSendKeys,
                ProtocolMethods.AgentViewSet,
                ProtocolMethods.AgentViewClear,
            ],
            ProtocolMethods.AgentExplainView);
    }

    [Fact]
    public void Protocol_fixtures_round_trip()
    {
        foreach (var method in ProtocolMethods.AgentExplainView)
        {
            var reqJson = FixtureCatalog.Load(FixtureCatalog.MethodRequestPath(method));
            var req = JsonSerializer.Deserialize(reqJson, ProtocolJsonContext.Default.RpcRequest);
            Assert.NotNull(req);
            Assert.Equal(method, req.Method);
            Assert.True(
                ProtocolEnvelopeValidator.TryValidateMethodParams(method, req.Params, out var err),
                err);
            var resJson = FixtureCatalog.Load(FixtureCatalog.MethodResponsePath(method));
            var res = JsonSerializer.Deserialize(resJson, ProtocolJsonContext.Default.RpcResponse);
            Assert.NotNull(res);
            Assert.True(res.Result.HasValue);
        }
    }

    [Fact]
    public async Task Explain_reports_source_version_and_matched_rule()
    {
        await using var session = await Session.CreateAsync();
        var paneId = session.FirstPaneId;
        session.Runtime.FireOutput(MuseWorking);

        var result = await session.Cp.DispatchAsync(
            ProtocolMethods.AgentExplain,
            Params(new JsonObject { ["pane_id"] = paneId }),
            CancellationToken.None);

        Assert.Equal("muse", result.GetProperty("agent").GetString());
        Assert.Equal("working", result.GetProperty("state").GetString());
        Assert.Equal("bundled", result.GetProperty("manifest_source_kind").GetString());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("manifest_version").GetString()));
        Assert.Equal("working_esc_interrupt", result.GetProperty("matched_rule").GetProperty("id").GetString());
        Assert.False(result.GetProperty("screen_detection_skipped").GetBoolean());
        Assert.Equal("", result.GetProperty("osc_title").GetString());
        Assert.Equal("", result.GetProperty("osc_progress").GetString());
    }

    [Fact]
    public async Task Explain_reports_osc_title_from_the_runtime()
    {
        await using var session = await Session.CreateAsync();
        session.Runtime.DetectionOscTitle = "grok";
        session.Runtime.FireOutput("$ ");

        var result = await session.Cp.DispatchAsync(
            ProtocolMethods.AgentExplain,
            Params(new JsonObject { ["pane_id"] = session.FirstPaneId }),
            CancellationToken.None);

        Assert.Equal("grok", result.GetProperty("osc_title").GetString());
        Assert.Equal("", result.GetProperty("osc_progress").GetString());
    }

    [Fact]
    public async Task Explain_does_not_use_stale_kind_when_foreground_is_a_shell()
    {
        await using var session = await Session.CreateAsync(command: "zsh");
        session.State.UpdatePane(
            new PaneId(session.FirstPaneId),
            p => p with { AgentKind = "grok" });
        session.Runtime.FireOutput("Grok\n$ ");

        var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            session.Cp.DispatchAsync(
                ProtocolMethods.AgentExplain,
                Params(new JsonObject { ["pane_id"] = session.FirstPaneId }),
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.InvalidState, ex.Code);
        Assert.Contains("does not have a detected agent label", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explain_skips_screen_when_semantic_authority_holds()
    {
        await using var session = await Session.CreateAsync();
        var paneId = session.FirstPaneId;
        session.Runtime.FireOutput(MuseWorking);
        await session.Cp.DispatchAsync(
            ProtocolMethods.PaneReportAgent,
            Params(new JsonObject
            {
                ["pane_id"] = paneId,
                ["source"] = "git",
                ["agent"] = "muse",
                ["state"] = "blocked",
            }),
            CancellationToken.None);

        var result = await session.Cp.DispatchAsync(
            ProtocolMethods.AgentExplain,
            Params(new JsonObject { ["pane_id"] = paneId }),
            CancellationToken.None);

        Assert.Equal("muse", result.GetProperty("agent").GetString());
        Assert.Equal("blocked", result.GetProperty("state").GetString());
        Assert.True(result.GetProperty("screen_detection_skipped").GetBoolean());
        Assert.Equal("full_lifecycle_hook_authority", result.GetProperty("screen_detection_skip_reason").GetString());
        Assert.False(result.TryGetProperty("matched_rule", out var rule) && rule.ValueKind is JsonValueKind.Object);
    }

    [Fact]
    public async Task Rename_and_clear_keep_the_pane_label()
    {
        await using var session = await Session.CreateAsync();
        var paneId = session.FirstPaneId;
        session.Runtime.FireOutput(MuseWorking);
        session.Cp.ScanDetectionNowForTests(paneId);
        await session.Cp.DispatchAsync(
            ProtocolMethods.PaneRename,
            Params(new JsonObject { ["pane_id"] = paneId, ["label"] = "shell-pane" }),
            CancellationToken.None);

        var renamed = await session.Cp.DispatchAsync(
            ProtocolMethods.AgentRename,
            Params(new JsonObject { ["pane_id"] = paneId, ["name"] = "reviewer" }),
            CancellationToken.None);
        Assert.Equal("reviewer", renamed.GetProperty("name").GetString());
        Assert.Equal("shell-pane", session.State.GetPane(new PaneId(paneId))!.Label);

        var listed = await session.Cp.DispatchAsync(ProtocolMethods.AgentList, parameters: null, CancellationToken.None);
        Assert.Equal("reviewer", listed[0].GetProperty("name").GetString());
        Assert.Equal("shell-pane", listed[0].GetProperty("label").GetString());

        var snapshot = await session.Cp.DispatchAsync(
            ProtocolMethods.SessionSnapshot, parameters: null, CancellationToken.None);
        Assert.Equal("shell-pane", snapshot.GetProperty("panes")[0].GetProperty("label").GetString());
        Assert.Equal("reviewer", snapshot.GetProperty("panes")[0].GetProperty("agent_name").GetString());

        await session.Cp.DispatchAsync(
            ProtocolMethods.AgentRename,
            Params(new JsonObject { ["pane_id"] = paneId }),
            CancellationToken.None);
        Assert.Null(session.State.GetPane(new PaneId(paneId))!.AgentName);
        Assert.Equal("shell-pane", session.State.GetPane(new PaneId(paneId))!.Label);
    }

    [Fact]
    public async Task Rename_invalid_and_duplicate_names_fail_closed()
    {
        await using var session = await Session.CreateAsync();
        var first = session.FirstPaneId;
        session.Runtime.FireOutput(MuseWorking);
        session.Cp.ScanDetectionNowForTests(first);
        await session.Cp.DispatchAsync(
            ProtocolMethods.AgentRename,
            Params(new JsonObject { ["pane_id"] = first, ["name"] = "reviewer" }),
            CancellationToken.None);

        var invalid = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            session.Cp.DispatchAsync(
                ProtocolMethods.AgentRename,
                Params(new JsonObject { ["pane_id"] = first, ["name"] = "Reviewer" }),
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.InvalidParams, invalid.Code);
        Assert.Contains("agent name", invalid.Message, StringComparison.Ordinal);

        var split = await session.Cp.DispatchAsync(
            ProtocolMethods.PaneSplit,
            Params(new JsonObject { ["pane_id"] = first, ["direction"] = "right", ["command"] = "muse" }),
            CancellationToken.None);
        var second = split.GetProperty("pane_id").GetString()!;
        session.Factory.Created[1].FireOutput(MuseWorking);
        session.Cp.ScanDetectionNowForTests(second);

        var taken = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            session.Cp.DispatchAsync(
                ProtocolMethods.AgentRename,
                Params(new JsonObject { ["pane_id"] = second, ["name"] = "reviewer" }),
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.InvalidState, taken.Code);
        Assert.Contains("already used", taken.Message, StringComparison.Ordinal);
        Assert.Equal("reviewer", session.State.GetPane(new PaneId(first))!.AgentName);
        Assert.Null(session.State.GetPane(new PaneId(second))!.AgentName);
    }

    [Fact]
    public async Task Focus_marks_every_pane_on_the_tab_seen()
    {
        await using var session = await Session.CreateAsync();
        var first = session.FirstPaneId;
        session.Runtime.FireOutput(MuseWorking);
        session.Cp.ScanDetectionNowForTests(first);
        var split = await session.Cp.DispatchAsync(
            ProtocolMethods.PaneSplit,
            Params(new JsonObject { ["pane_id"] = first, ["direction"] = "right", ["command"] = "muse" }),
            CancellationToken.None);
        var second = split.GetProperty("pane_id").GetString()!;
        session.Factory.Created[1].FireOutput(MuseWorking);
        session.Cp.ScanDetectionNowForTests(second);

        session.State.UpdatePane(new PaneId(first), p => p with { Seen = false });
        session.State.UpdatePane(new PaneId(second), p => p with { Seen = false });

        await session.Cp.DispatchAsync(
            ProtocolMethods.AgentFocus,
            Params(new JsonObject { ["pane_id"] = first }),
            CancellationToken.None);

        Assert.True(session.State.GetPane(new PaneId(first))!.Seen);
        Assert.True(session.State.GetPane(new PaneId(second))!.Seen);
    }

    [Fact]
    public async Task Send_keys_validates_all_logical_keys_before_write()
    {
        await using var session = await Session.CreateAsync();
        var paneId = session.FirstPaneId;
        session.Runtime.FireOutput(MuseWorking);
        session.Cp.ScanDetectionNowForTests(paneId);

        var rejected = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            session.Cp.DispatchAsync(
                ProtocolMethods.AgentSendKeys,
                Params(new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["keys"] = new JsonArray(JsonValue.Create("up"), JsonValue.Create("not-a-key")),
                }),
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.InvalidParams, rejected.Code);
        Assert.Contains("unsupported key", rejected.Message, StringComparison.Ordinal);
        Assert.Contains("not-a-key", rejected.Message, StringComparison.Ordinal);
        Assert.Empty(session.Runtime.Writes);

        var sent = await session.Cp.DispatchAsync(
            ProtocolMethods.AgentSendKeys,
            Params(new JsonObject
            {
                ["pane_id"] = paneId,
                ["keys"] = new JsonArray(JsonValue.Create("up"), JsonValue.Create("enter")),
            }),
            CancellationToken.None);
        Assert.True(sent.GetProperty("ok").GetBoolean());
        var bytes = Assert.Single(session.Runtime.Writes);
        Assert.Equal(new byte[] { 0x1B, (byte)'[', (byte)'A', 0x0D }, bytes);
    }

    [Fact]
    public async Task Missing_agent_attach_method_fails_closed()
    {
        await using var session = await Session.CreateAsync();
        var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            session.Cp.DispatchAsync("agent.attach", Params(new JsonObject { ["pane_id"] = session.FirstPaneId }), CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.MethodNotFound, ex.Code);
    }

    [Fact]
    public async Task View_set_and_clear_replace_or_leave_without_changing_list_or_detection()
    {
        await using var session = await Session.CreateAsync();
        var paneId = session.FirstPaneId;
        session.Runtime.FireOutput(MuseWorking);
        session.Cp.ScanDetectionNowForTests(paneId);
        var listBefore = (await session.Cp.DispatchAsync(
            ProtocolMethods.AgentList, parameters: null, CancellationToken.None)).GetRawText();
        var statusBefore = session.State.GetPane(new PaneId(paneId))!.AgentStatus;

        using var filter = JsonDocument.Parse("""{"op":"eq","field":"status","value":"working"}""");
        var set = await session.Cp.DispatchAsync(
            ProtocolMethods.AgentViewSet,
            Params(new JsonObject
            {
                ["source"] = "example.views",
                ["label"] = "working",
                ["filter"] = JsonNode.Parse(filter.RootElement.GetRawText()),
            }),
            CancellationToken.None);
        Assert.True(set.GetProperty("active").GetBoolean());
        Assert.Equal("example.views", set.GetProperty("source").GetString());

        var pluginSet = await session.Cp.DispatchAsync(
            ProtocolMethods.AgentViewSet,
            Params(new JsonObject { ["source"] = "plugin:example.agent-views" }),
            CancellationToken.None);
        Assert.True(pluginSet.GetProperty("active").GetBoolean());
        Assert.Equal("plugin:example.agent-views", pluginSet.GetProperty("source").GetString());
        await session.Cp.DispatchAsync(
            ProtocolMethods.AgentViewSet,
            Params(new JsonObject
            {
                ["source"] = "example.views",
                ["label"] = "working",
                ["filter"] = JsonNode.Parse(filter.RootElement.GetRawText()),
            }),
            CancellationToken.None);

        var snapshot = await session.Cp.DispatchAsync(
            ProtocolMethods.SessionSnapshot, parameters: null, CancellationToken.None);
        Assert.True(snapshot.GetProperty("agent_view").GetProperty("active").GetBoolean());
        Assert.Equal("working", snapshot.GetProperty("agent_view").GetProperty("label").GetString());
        Assert.Equal(paneId, snapshot.GetProperty("agent_order")[0].GetString());
        Assert.Equal(listBefore, (await session.Cp.DispatchAsync(
            ProtocolMethods.AgentList, parameters: null, CancellationToken.None)).GetRawText());

        var leave = await session.Cp.DispatchAsync(
            ProtocolMethods.AgentViewClear,
            Params(new JsonObject { ["source"] = "other.views" }),
            CancellationToken.None);
        Assert.True(leave.GetProperty("active").GetBoolean());
        Assert.Equal("example.views", leave.GetProperty("source").GetString());

        using var invalid = JsonDocument.Parse("""{"op":"any","filters":[]}""");
        var invalidSet = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            session.Cp.DispatchAsync(
                ProtocolMethods.AgentViewSet,
                Params(new JsonObject
                {
                    ["source"] = "example.other",
                    ["filter"] = JsonNode.Parse(invalid.RootElement.GetRawText()),
                }),
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.InvalidParams, invalidSet.Code);
        var still = await session.Cp.DispatchAsync(
            ProtocolMethods.SessionSnapshot, parameters: null, CancellationToken.None);
        Assert.Equal("example.views", still.GetProperty("agent_view").GetProperty("source").GetString());

        var blank = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            session.Cp.DispatchAsync(
                ProtocolMethods.AgentViewClear,
                Params(new JsonObject { ["source"] = " " }),
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.InvalidParams, blank.Code);
        var afterBlank = await session.Cp.DispatchAsync(
            ProtocolMethods.SessionSnapshot, parameters: null, CancellationToken.None);
        Assert.True(afterBlank.GetProperty("agent_view").GetProperty("active").GetBoolean());
        Assert.Equal("example.views", afterBlank.GetProperty("agent_view").GetProperty("source").GetString());

        var empty = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            session.Cp.DispatchAsync(
                ProtocolMethods.AgentViewClear,
                Params(new JsonObject { ["source"] = "" }),
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.InvalidParams, empty.Code);
        var afterEmpty = await session.Cp.DispatchAsync(
            ProtocolMethods.SessionSnapshot, parameters: null, CancellationToken.None);
        Assert.Equal("example.views", afterEmpty.GetProperty("agent_view").GetProperty("source").GetString());

        var cleared = await session.Cp.DispatchAsync(
            ProtocolMethods.AgentViewClear,
            Params(new JsonObject { ["source"] = "example.views" }),
            CancellationToken.None);
        Assert.False(cleared.GetProperty("active").GetBoolean());

        session.Runtime.FireOutput(MuseWorking);
        session.Cp.ScanDetectionNowForTests(paneId);
        Assert.Equal(statusBefore, session.State.GetPane(new PaneId(paneId))!.AgentStatus);
        Assert.Equal(listBefore, (await session.Cp.DispatchAsync(
            ProtocolMethods.AgentList, parameters: null, CancellationToken.None)).GetRawText());
    }

    [Fact]
    public void Agents_chrome_uses_view_order_and_hides_sort()
    {
        var strategy = new AgentsChromeSectionStrategy();
        var resolved = new ResolvedSidebarSection
        {
            Id = SidebarTokenGrammar.AgentsId,
            Title = "agents",
            Order = 0,
            Collapsed = false,
            Config = new AttachSidebarSectionConfig(),
        };
        var view = strategy.Compose(
            new SidebarComposeInput
            {
                Ui = AttachUiConfig.Default,
                Expanded = true,
                RequestedWidth = 26,
                AgentViewLabel = "working",
                AgentOrder = ["p2", "p1"],
                AgentViewHasSort = true,
                Panes =
                [
                    new SidebarPaneItem
                    {
                        Id = "p1",
                        TabId = "t1",
                        WorkspaceId = "w1",
                        Agent = "muse",
                        State = "working",
                    },
                    new SidebarPaneItem
                    {
                        Id = "p2",
                        TabId = "t1",
                        WorkspaceId = "w1",
                        Agent = "pi",
                        State = "idle",
                    },
                ],
            },
            resolved,
            collapsed: false,
            visible: true,
            width: 26,
            display: SidebarCollapseDisplay.Expanded);

        Assert.Equal("No matching agents", view.EmptyText);
        Assert.Empty(view.Actions);
        Assert.Equal(["p2", "p1"], view.Rows.Select(r => r.Id).Distinct(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Filter_only_chrome_keeps_configured_priority_order()
    {
        var strategy = new AgentsChromeSectionStrategy();
        var resolved = new ResolvedSidebarSection
        {
            Id = SidebarTokenGrammar.AgentsId,
            Title = "agents",
            Order = 0,
            Collapsed = false,
            Config = new AttachSidebarSectionConfig(),
        };
        var view = strategy.Compose(
            new SidebarComposeInput
            {
                Ui = AttachUiConfig.Default with { AgentPanelSort = AgentPanelSort.Priority },
                Expanded = true,
                RequestedWidth = 26,
                AgentViewLabel = "mixed",
                AgentOrder = ["p-idle", "p-blocked"],
                AgentViewHasSort = false,
                Panes =
                [
                    new SidebarPaneItem
                    {
                        Id = "p-idle",
                        TabId = "t1",
                        WorkspaceId = "w1",
                        Agent = "muse",
                        State = "idle",
                    },
                    new SidebarPaneItem
                    {
                        Id = "p-blocked",
                        TabId = "t1",
                        WorkspaceId = "w1",
                        Agent = "pi",
                        State = "blocked",
                    },
                ],
            },
            resolved,
            collapsed: false,
            visible: true,
            width: 26,
            display: SidebarCollapseDisplay.Expanded);

        Assert.Equal(["p-blocked", "p-idle"], view.Rows.Select(r => r.Id).Distinct(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task View_state_change_seq_tracks_two_agent_transitions()
    {
        await using var session = await Session.CreateAsync();
        var first = session.FirstPaneId;
        session.Runtime.FireOutput(MuseWorking);
        session.Cp.ScanDetectionNowForTests(first);
        var split = await session.Cp.DispatchAsync(
            ProtocolMethods.PaneSplit,
            Params(new JsonObject { ["pane_id"] = first, ["direction"] = "right", ["command"] = "muse" }),
            CancellationToken.None);
        var second = split.GetProperty("pane_id").GetString()!;
        session.Factory.Created[1].FireOutput(MuseWorking);
        session.Cp.ScanDetectionNowForTests(second);

        session.Cp.ForceAgentStatusForTests(first, AgentStatus.Idle);
        session.Cp.ForceAgentStatusForTests(second, AgentStatus.Blocked);
        var firstSeq = session.State.GetPane(new PaneId(first))!.LastAgentStateChangeSeq;
        var secondSeq = session.State.GetPane(new PaneId(second))!.LastAgentStateChangeSeq;
        Assert.NotNull(firstSeq);
        Assert.NotNull(secondSeq);
        Assert.NotEqual(firstSeq, secondSeq);

        using var filter = JsonDocument.Parse("""{"op":"exists","field":"state_change_seq"}""");
        using var sort = JsonDocument.Parse("""[{"field":"state_change_seq","order":"desc"}]""");
        await session.Cp.DispatchAsync(
            ProtocolMethods.AgentViewSet,
            Params(new JsonObject
            {
                ["source"] = "example.seq",
                ["filter"] = JsonNode.Parse(filter.RootElement.GetRawText()),
                ["sort"] = JsonNode.Parse(sort.RootElement.GetRawText()),
            }),
            CancellationToken.None);
        var snapshot = await session.Cp.DispatchAsync(
            ProtocolMethods.SessionSnapshot, parameters: null, CancellationToken.None);
        var order = snapshot.GetProperty("agent_order");
        Assert.Equal(2, order.GetArrayLength());
        var later = secondSeq > firstSeq ? second : first;
        var earlier = secondSeq > firstSeq ? first : second;
        Assert.Equal(later, order[0].GetString());
        Assert.Equal(earlier, order[1].GetString());
        Assert.True(snapshot.GetProperty("agent_view").GetProperty("has_sort").GetBoolean());
    }

    [Fact]
    public async Task Filter_only_view_orders_blocked_ahead_of_idle()
    {
        await using var session = await Session.CreateAsync();
        var first = session.FirstPaneId;
        session.Runtime.FireOutput(MuseWorking);
        session.Cp.ScanDetectionNowForTests(first);
        var split = await session.Cp.DispatchAsync(
            ProtocolMethods.PaneSplit,
            Params(new JsonObject { ["pane_id"] = first, ["direction"] = "right", ["command"] = "muse" }),
            CancellationToken.None);
        var second = split.GetProperty("pane_id").GetString()!;
        session.Factory.Created[1].FireOutput(MuseWorking);
        session.Cp.ScanDetectionNowForTests(second);

        session.Cp.ForceAgentStatusForTests(first, AgentStatus.Idle);
        session.Cp.ForceAgentStatusForTests(second, AgentStatus.Blocked);

        using var filter = JsonDocument.Parse("""{"op":"exists","field":"agent"}""");
        await session.Cp.DispatchAsync(
            ProtocolMethods.AgentViewSet,
            Params(new JsonObject
            {
                ["source"] = "example.priority",
                ["filter"] = JsonNode.Parse(filter.RootElement.GetRawText()),
            }),
            CancellationToken.None);
        var snapshot = await session.Cp.DispatchAsync(
            ProtocolMethods.SessionSnapshot, parameters: null, CancellationToken.None);
        Assert.False(snapshot.GetProperty("agent_view").GetProperty("has_sort").GetBoolean());
        var order = snapshot.GetProperty("agent_order");
        Assert.Equal(second, order[0].GetString());
        Assert.Equal(first, order[1].GetString());
    }

    private static JsonElement Params(JsonObject obj)
    {
        using var doc = JsonDocument.Parse(obj.ToJsonString());
        return doc.RootElement.Clone();
    }

    private sealed class Session : IAsyncDisposable
    {
        private Session(
            AppState state,
            TestPaneFactories.ScriptedPaneFactory factory,
            ControlPlaneService cp)
        {
            State = state;
            Factory = factory;
            Cp = cp;
        }

        public AppState State { get; }
        public TestPaneFactories.ScriptedPaneFactory Factory { get; }
        public ControlPlaneService Cp { get; }
        public TestPaneFactories.ScriptedPaneRuntime Runtime => Factory.Created[0];
        public string FirstPaneId => Runtime.Id.Value;

        public static async Task<Session> CreateAsync(string command = "muse")
        {
            var state = new AppState(SessionId.New("explain-view"));
            state.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready, Placement = "local" });
            var factory = TestPaneFactories.Scripted();
            var intel = new PaneIntelligencePipeline();
            var cp = new ControlPlaneService(state, factory, intel, new HeuristicAgentDetector());
            await cp.DispatchAsync(
                "workspace.create",
                Params(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["command"] = command,
                    ["create_pane"] = true,
                }),
                CancellationToken.None);
            return new Session(state, factory, cp);
        }

        public async ValueTask DisposeAsync() =>
            await Cp.ShutdownAsync(CancellationToken.None);
    }
}
