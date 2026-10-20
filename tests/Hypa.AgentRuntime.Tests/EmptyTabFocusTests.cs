using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// A tab whose last pane closes has no focus. The next pane and the next
/// split must not see an empty pane id as a focus.
/// </summary>
public sealed class EmptyTabFocusTests
{
    [Fact]
    public void Removing_the_last_pane_of_a_tab_leaves_no_focus()
    {
        var state = new AppState(SessionId.New("empty-focus"));
        var ws = state.CreateWorkspace("/seed");
        var tab = state.CreateTab(ws.Id, focus: true);
        var pane = state.RegisterPane(new PaneState
        {
            Id = PaneId.New(),
            WorkspaceId = ws.Id,
            TabId = tab.Id,
            Cwd = "/seed",
        });
        Assert.Equal(pane.Id, state.GetTab(tab.Id)!.FocusedPaneId);

        Assert.True(state.RemovePane(pane.Id));

        var after = state.GetTab(tab.Id)!;
        Assert.Empty(after.PaneIds);
        Assert.Null(after.FocusedPaneId);
        Assert.Null(after.ZoomedPaneId);
    }

    [Fact]
    public void A_saved_empty_focus_is_replaced_by_the_next_pane()
    {
        var state = new AppState(SessionId.New("empty-focus-heal"));
        var ws = state.CreateWorkspace("/seed");
        var tab = state.CreateTab(ws.Id, focus: true);
        // Older builds saved this value for a tab with no pane.
        state.UpdateSession(s => s with
        {
            Tabs = new Dictionary<string, TabState>(s.Tabs)
            {
                [tab.Id.Value] = s.Tabs[tab.Id.Value] with { FocusedPaneId = default(PaneId) },
            },
        });

        var pane = state.RegisterPane(new PaneState
        {
            Id = PaneId.New(),
            WorkspaceId = ws.Id,
            TabId = tab.Id,
            Cwd = "/seed",
        });

        Assert.Equal(pane.Id, state.GetTab(tab.Id)!.FocusedPaneId);
    }

    [Fact]
    public async Task A_split_without_focus_after_the_last_pane_closed_keeps_the_source_focused()
    {
        var state = new AppState(SessionId.New("empty-focus-split"));
        state.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector());
        try
        {
            var ws = await CallAsync(cp, ProtocolMethods.WorkspaceCreate, new JsonObject
            {
                ["cwd"] = Path.GetTempPath(),
                ["create_pane"] = true,
                ["command"] = "/bin/sh",
            });
            var workspaceId = ws.GetProperty("workspace_id").GetString()!;
            var first = new PaneId(ws.GetProperty("pane").GetProperty("pane_id").GetString()!);
            var tabId = state.GetPane(first)!.TabId;

            await CallAsync(cp, ProtocolMethods.PaneClose, new JsonObject { ["pane_id"] = first.Value });
            Assert.NotNull(state.GetTab(tabId));
            Assert.Null(state.GetTab(tabId)!.FocusedPaneId);

            var created = await CallAsync(cp, ProtocolMethods.PaneCreate, new JsonObject
            {
                ["workspace_id"] = workspaceId,
                ["command"] = "/bin/sh",
            });
            var source = new PaneId(created.GetProperty("pane_id").GetString()!);
            Assert.Equal(tabId, state.GetPane(source)!.TabId);

            var split = await CallAsync(cp, ProtocolMethods.PaneSplit, new JsonObject
            {
                ["pane_id"] = source.Value,
                ["direction"] = "right",
                ["focus"] = false,
                ["command"] = "/bin/sh",
            });

            var added = new PaneId(split.GetProperty("pane_id").GetString()!);
            Assert.NotNull(state.GetPane(added));
            var tab = state.GetTab(tabId)!;
            Assert.Equal(source, tab.FocusedPaneId);
            Assert.Equal([source, added], tab.PaneIds.OrderBy(p => p == source ? 0 : 1).ToList());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_split_without_focus_from_a_saved_empty_focus_focuses_the_source()
    {
        var state = new AppState(SessionId.New("empty-focus-saved-split"));
        state.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector());
        try
        {
            var ws = await CallAsync(cp, ProtocolMethods.WorkspaceCreate, new JsonObject
            {
                ["cwd"] = Path.GetTempPath(),
                ["create_pane"] = true,
                ["command"] = "/bin/sh",
            });
            var source = new PaneId(ws.GetProperty("pane").GetProperty("pane_id").GetString()!);
            var tabId = state.GetPane(source)!.TabId;
            // Older builds saved an empty focus beside a tiled pane.
            state.UpdateSession(s => s with
            {
                Tabs = new Dictionary<string, TabState>(s.Tabs)
                {
                    [tabId.Value] = s.Tabs[tabId.Value] with { FocusedPaneId = default(PaneId) },
                },
            });

            var split = await CallAsync(cp, ProtocolMethods.PaneSplit, new JsonObject
            {
                ["pane_id"] = source.Value,
                ["direction"] = "down",
                ["focus"] = false,
                ["command"] = "/bin/sh",
            });

            var added = new PaneId(split.GetProperty("pane_id").GetString()!);
            Assert.NotNull(state.GetPane(added));
            Assert.Equal(source, state.GetTab(tabId)!.FocusedPaneId);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private static Task<JsonElement> CallAsync(ControlPlaneService cp, string method, JsonObject args) =>
        cp.DispatchAsync(method, JsonDocument.Parse(args.ToJsonString()).RootElement, CancellationToken.None);
}
