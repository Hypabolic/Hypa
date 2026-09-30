using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class ControlPlanePlacementAuthorityTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public ControlPlanePlacementAuthorityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-auth-t-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose() => SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);

    [Fact]
    public async Task Owner_lease_creates_child_foreign_same_lease_is_rejected()
    {
        var factory = TestPaneFactories.Capturing();
        var (cp, state, store, _) = await NewAsync(factory);
        try
        {
            var (parentId, workspaceId, owner, leaseId) = await SeedParentWithLeaseAsync(cp, "input");
            var before = state.ListPanes().Count;

            var child = await cp.DispatchAsync(
                ProtocolMethods.PaneCreate,
                Json(new JsonObject
                {
                    ["workspace_id"] = workspaceId,
                    ["command"] = "/bin/echo",
                    ["placement"] = "hidden",
                    ["parent_pane_id"] = parentId,
                    ["lease_id"] = leaseId,
                }),
                owner,
                CancellationToken.None);
            Assert.Equal(parentId, child.GetProperty("parent_pane_id").GetString());
            Assert.StartsWith("par_", child.GetProperty("parent_capability").GetString());
            var childId = child.GetProperty("pane_id").GetString()!;

            var foreign = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneCreate,
                    Json(new JsonObject
                    {
                        ["workspace_id"] = workspaceId,
                        ["command"] = "/bin/echo",
                        ["placement"] = "hidden",
                        ["parent_pane_id"] = parentId,
                        ["lease_id"] = leaseId,
                    }),
                    new FakeConnection("conn_foreign"),
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.CapabilityInvalid, foreign.Code);
            Assert.Equal(before + 1, state.ListPanes().Count);
            Assert.False(child.TryGetProperty("occupant_token", out _));

            var loaded = (await store.TryLoadAsync("pane-auth")).Value!;
            Assert.Equal(parentId, loaded.Panes[childId].ParentPaneId?.Value);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Occupant_token_still_creates_and_resize_lease_does_not_prove_parent()
    {
        var factory = TestPaneFactories.Capturing();
        var (cp, state, _, _) = await NewAsync(factory);
        try
        {
            var (parentId, workspaceId, owner, _) = await SeedParentWithLeaseAsync(cp, "input");
            var token = RequireOccupantToken(factory.LastOptions);
            Assert.StartsWith("occ_", token);

            var child = await cp.DispatchAsync(
                ProtocolMethods.PaneCreate,
                Json(new JsonObject
                {
                    ["workspace_id"] = workspaceId,
                    ["command"] = "/bin/echo",
                    ["placement"] = "hidden",
                    ["parent_pane_id"] = parentId,
                    ["occupant_token"] = token,
                }),
                new FakeConnection("conn_other"),
                CancellationToken.None);
            Assert.Equal(parentId, child.GetProperty("parent_pane_id").GetString());

            var resizeClaim = await cp.DispatchAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                Json(new JsonObject { ["pane_id"] = parentId, ["scope"] = "resize" }),
                owner,
                CancellationToken.None);
            var resizeId = resizeClaim.GetProperty("lease_id").GetString()!;
            var before = state.ListPanes().Count;
            var denied = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneCreate,
                    Json(new JsonObject
                    {
                        ["workspace_id"] = workspaceId,
                        ["command"] = "/bin/echo",
                        ["placement"] = "hidden",
                        ["parent_pane_id"] = parentId,
                        ["lease_id"] = resizeId,
                    }),
                    owner,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.CapabilityInvalid, denied.Code);
            Assert.Equal(before, state.ListPanes().Count);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Occupant_and_parent_show_hide_and_forged_or_status_fail()
    {
        var factory = TestPaneFactories.Capturing();
        var (cp, state, _, _) = await NewAsync(factory);
        try
        {
            var seeded = await SeedHiddenChildAsync(cp, factory);
            var forged = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneShow,
                    Json(new JsonObject
                    {
                        ["pane_id"] = seeded.ChildId,
                        ["occupant_token"] = "occ_forged",
                        ["seq"] = 1,
                    }),
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.CapabilityInvalid, forged.Code);

            var forgedId = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneShow,
                    Json(new JsonObject { ["pane_id"] = seeded.ChildId }),
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.LeaseRequired, forgedId.Code);

            var shown = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                Json(new JsonObject
                {
                    ["pane_id"] = seeded.ChildId,
                    ["occupant_token"] = seeded.OccupantToken,
                    ["seq"] = 1,
                }),
                CancellationToken.None);
            Assert.True(shown.GetProperty("changed").GetBoolean());
            Assert.Equal("tiled", shown.GetProperty("placement").GetString());
            Assert.NotEqual(seeded.OwnerTabId, shown.GetProperty("tab_id").GetString());

            var hidden = await cp.DispatchAsync(
                ProtocolMethods.PaneHide,
                Json(new JsonObject
                {
                    ["pane_id"] = seeded.ChildId,
                    ["parent_capability"] = seeded.ParentCapability,
                    ["seq"] = 1,
                }),
                CancellationToken.None);
            Assert.True(hidden.GetProperty("changed").GetBoolean());
            Assert.Equal(PanePlacement.Hidden, state.GetPane(new PaneId(seeded.ChildId))!.Placement);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Repeated_default_show_does_not_create_another_tab()
    {
        var factory = TestPaneFactories.Capturing();
        var (cp, state, _, sink) = await NewAsync(factory);
        try
        {
            var seeded = await SeedHiddenChildAsync(cp, factory);
            var workspaceId = state.GetPane(new PaneId(seeded.ChildId))!.WorkspaceId;
            var shown = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                Json(new JsonObject
                {
                    ["pane_id"] = seeded.ChildId,
                    ["occupant_token"] = seeded.OccupantToken,
                    ["seq"] = 1,
                }),
                CancellationToken.None);
            Assert.True(shown.GetProperty("changed").GetBoolean());
            var tabId = shown.GetProperty("tab_id").GetString();
            var tabsAfterReveal = state.ListTabs(workspaceId).Count;
            var focusAfterReveal = state.GetWorkspace(workspaceId)!.FocusedTabId?.Value;
            sink.Lines.Clear();

            var again = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                Json(new JsonObject
                {
                    ["pane_id"] = seeded.ChildId,
                    ["occupant_token"] = seeded.OccupantToken,
                    ["seq"] = 2,
                }),
                CancellationToken.None);
            Assert.False(again.GetProperty("changed").GetBoolean());
            Assert.Equal(tabId, again.GetProperty("tab_id").GetString());
            Assert.Equal(tabsAfterReveal, state.ListTabs(workspaceId).Count);
            Assert.Equal(focusAfterReveal, state.GetWorkspace(workspaceId)!.FocusedTabId?.Value);
            Assert.DoesNotContain(ProtocolEventTypes.PanePlacementChanged, EventTypes(sink));

            var owner = new FakeConnection("conn_repeat_lease");
            var claim = await cp.DispatchAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                Json(new JsonObject { ["pane_id"] = seeded.ChildId, ["scope"] = "input" }),
                owner,
                CancellationToken.None);
            var human = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                Json(new JsonObject
                {
                    ["pane_id"] = seeded.ChildId,
                    ["lease_id"] = claim.GetProperty("lease_id").GetString(),
                }),
                owner,
                CancellationToken.None);
            Assert.False(human.GetProperty("changed").GetBoolean());
            Assert.Equal(tabId, human.GetProperty("tab_id").GetString());
            Assert.Equal(tabsAfterReveal, state.ListTabs(workspaceId).Count);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Invalid_ratio_restores_nonadjacent_focus_and_creates_no_tab()
    {
        var factory = TestPaneFactories.Capturing();
        var (cp, state, _, _) = await NewAsync(factory);
        try
        {
            var seeded = await SeedHiddenChildAsync(cp, factory);
            var workspaceId = state.GetPane(new PaneId(seeded.ChildId))!.WorkspaceId.Value;
            await cp.DispatchAsync(
                ProtocolMethods.TabCreate,
                Json(new JsonObject
                {
                    ["workspace_id"] = workspaceId,
                    ["create_pane"] = false,
                    ["focus"] = true,
                    ["label"] = "mid",
                }),
                CancellationToken.None);
            var far = await cp.DispatchAsync(
                ProtocolMethods.TabCreate,
                Json(new JsonObject
                {
                    ["workspace_id"] = workspaceId,
                    ["create_pane"] = false,
                    ["focus"] = true,
                    ["label"] = "far",
                }),
                CancellationToken.None);
            var farTab = far.GetProperty("tab_id").GetString();
            var tabsBefore = state.ListTabs(new WorkspaceId(workspaceId)).Count;
            var panesBefore = state.ListPanes().Count;

            var invalid = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneShow,
                    Json(new JsonObject
                    {
                        ["pane_id"] = seeded.ChildId,
                        ["occupant_token"] = seeded.OccupantToken,
                        ["ratio"] = 1.5,
                        ["seq"] = 1,
                    }),
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidParams, invalid.Code);
            Assert.Equal(farTab, state.GetWorkspace(new WorkspaceId(workspaceId))!.FocusedTabId?.Value);
            Assert.Equal(tabsBefore, state.ListTabs(new WorkspaceId(workspaceId)).Count);
            Assert.Equal(panesBefore, state.ListPanes().Count);
            Assert.Equal(PanePlacement.Hidden, state.GetPane(new PaneId(seeded.ChildId))!.Placement);
            Assert.Equal(seeded.OwnerTabId, state.GetPane(new PaneId(seeded.ChildId))!.TabId.Value);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Durable_persist_failure_keeps_seq_and_retries()
    {
        var factory = TestPaneFactories.Capturing();
        var sqlite = await OpenStoreAsync();
        var store = new ControllableRuntimeStore(sqlite);
        var (cp, state, _, sink) = await NewAsync(factory, persistStore: store);
        try
        {
            var seeded = await SeedHiddenChildAsync(cp, factory);
            var workspaceId = state.GetPane(new PaneId(seeded.ChildId))!.WorkspaceId;
            var focusBefore = state.GetWorkspace(workspaceId)!.FocusedTabId?.Value;
            var tabsBefore = state.ListTabs(workspaceId).Count;
            sink.Lines.Clear();
            store.FailSaves = true;

            var failed = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneShow,
                    Json(new JsonObject
                    {
                        ["pane_id"] = seeded.ChildId,
                        ["occupant_token"] = seeded.OccupantToken,
                        ["seq"] = 11,
                    }),
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.PersistenceUnavailable, failed.Code);
            Assert.Equal(PanePlacement.Hidden, state.GetPane(new PaneId(seeded.ChildId))!.Placement);
            Assert.Equal(seeded.OwnerTabId, state.GetPane(new PaneId(seeded.ChildId))!.TabId.Value);
            Assert.Equal(focusBefore, state.GetWorkspace(workspaceId)!.FocusedTabId?.Value);
            Assert.Equal(tabsBefore, state.ListTabs(workspaceId).Count);
            Assert.DoesNotContain(ProtocolEventTypes.PanePlacementChanged, EventTypes(sink));
            Assert.DoesNotContain(ProtocolEventTypes.LayoutUpdated, EventTypes(sink));

            store.FailSaves = false;
            var shown = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                Json(new JsonObject
                {
                    ["pane_id"] = seeded.ChildId,
                    ["occupant_token"] = seeded.OccupantToken,
                    ["seq"] = 11,
                }),
                CancellationToken.None);
            Assert.True(shown.GetProperty("changed").GetBoolean());
            Assert.Equal("tiled", shown.GetProperty("placement").GetString());
            Assert.NotEqual(seeded.OwnerTabId, shown.GetProperty("tab_id").GetString());
        }
        finally
        {
            store.FailSaves = false;
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Durable_hide_restores_nested_down_split_zoom_and_focus()
    {
        var factory = TestPaneFactories.Capturing();
        var sqlite = await OpenStoreAsync();
        var store = new ControllableRuntimeStore(sqlite);
        var (cp, state, _, sink) = await NewAsync(factory, persistStore: store);
        try
        {
            var seeded = await SeedHiddenChildAsync(cp, factory);
            var shown = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                Json(new JsonObject
                {
                    ["pane_id"] = seeded.ChildId,
                    ["occupant_token"] = seeded.OccupantToken,
                    ["direction"] = "down",
                    ["target_pane_id"] = seeded.TiledId,
                    ["ratio"] = 0.3,
                    ["seq"] = 1,
                }),
                CancellationToken.None);
            Assert.True(shown.GetProperty("changed").GetBoolean());
            await cp.DispatchAsync(
                ProtocolMethods.PaneZoom,
                Json(new JsonObject { ["pane_id"] = seeded.ChildId, ["mode"] = "on" }),
                CancellationToken.None);
            var tabId = shown.GetProperty("tab_id").GetString()!;
            var before = state.GetTab(new TabId(tabId))!;
            var beforeJson = before.LayoutRoot!.ToCanonicalJson(true);
            Assert.Equal(seeded.ChildId, before.FocusedPaneId?.Value);
            Assert.True(before.Zoomed);
            sink.Lines.Clear();
            store.FailSaves = true;

            var failed = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneHide,
                    Json(new JsonObject
                    {
                        ["pane_id"] = seeded.ChildId,
                        ["occupant_token"] = seeded.OccupantToken,
                        ["seq"] = 2,
                    }),
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.PersistenceUnavailable, failed.Code);
            var after = state.GetTab(new TabId(tabId))!;
            Assert.Equal(beforeJson, after.LayoutRoot!.ToCanonicalJson(true));
            var split = Assert.IsType<LayoutSplitNode>(after.LayoutRoot);
            Assert.Equal(LayoutNode.DirectionDown, split.Direction);
            Assert.Equal(0.3, split.Ratio, 6);
            Assert.Equal(PanePlacement.Tiled, state.GetPane(new PaneId(seeded.ChildId))!.Placement);
            Assert.Equal(seeded.ChildId, after.FocusedPaneId?.Value);
            Assert.True(after.Zoomed);
            Assert.Equal(seeded.ChildId, after.ZoomedPaneId?.Value);
            Assert.DoesNotContain(ProtocolEventTypes.PanePlacementChanged, EventTypes(sink));

            store.FailSaves = false;
            var hidden = await cp.DispatchAsync(
                ProtocolMethods.PaneHide,
                Json(new JsonObject
                {
                    ["pane_id"] = seeded.ChildId,
                    ["occupant_token"] = seeded.OccupantToken,
                    ["seq"] = 2,
                }),
                CancellationToken.None);
            Assert.True(hidden.GetProperty("changed").GetBoolean());
            Assert.Equal("hidden", hidden.GetProperty("placement").GetString());
            sink.Lines.Clear();
            var again = await cp.DispatchAsync(
                ProtocolMethods.PaneHide,
                Json(new JsonObject
                {
                    ["pane_id"] = seeded.ChildId,
                    ["occupant_token"] = seeded.OccupantToken,
                    ["seq"] = 3,
                }),
                CancellationToken.None);
            Assert.False(again.GetProperty("changed").GetBoolean());
            Assert.DoesNotContain(ProtocolEventTypes.PanePlacementChanged, EventTypes(sink));
        }
        finally
        {
            store.FailSaves = false;
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Durable_cross_workspace_show_restores_session_focus()
    {
        var factory = TestPaneFactories.Capturing();
        var sqlite = await OpenStoreAsync();
        var store = new ControllableRuntimeStore(sqlite);
        var (cp, state, _, sink) = await NewAsync(factory, persistStore: store);
        try
        {
            var seeded = await SeedHiddenChildAsync(cp, factory);
            var ownerWs = state.GetPane(new PaneId(seeded.ChildId))!.WorkspaceId;
            var other = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                Json(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["create_pane"] = false,
                    ["label"] = "other",
                }),
                CancellationToken.None);
            var otherWs = other.GetProperty("workspace_id").GetString()!;
            await cp.DispatchAsync(
                ProtocolMethods.TabCreate,
                Json(new JsonObject
                {
                    ["workspace_id"] = otherWs,
                    ["create_pane"] = false,
                    ["focus"] = true,
                    ["label"] = "mid",
                }),
                CancellationToken.None);
            var far = await cp.DispatchAsync(
                ProtocolMethods.TabCreate,
                Json(new JsonObject
                {
                    ["workspace_id"] = otherWs,
                    ["create_pane"] = false,
                    ["focus"] = true,
                    ["label"] = "far",
                }),
                CancellationToken.None);
            var farTab = far.GetProperty("tab_id").GetString()!;
            await cp.DispatchAsync(
                ProtocolMethods.WorkspaceFocus,
                Json(new JsonObject { ["workspace_id"] = otherWs }),
                CancellationToken.None);
            await cp.DispatchAsync(
                ProtocolMethods.TabFocus,
                Json(new JsonObject { ["tab_id"] = farTab }),
                CancellationToken.None);
            Assert.Equal(otherWs, state.Snapshot().FocusedWorkspaceId?.Value);
            Assert.Equal(farTab, state.GetWorkspace(new WorkspaceId(otherWs))!.FocusedTabId?.Value);
            var tabsBefore = state.ListTabs().Count;
            sink.Lines.Clear();
            store.FailSaves = true;

            var failed = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneShow,
                    Json(new JsonObject
                    {
                        ["pane_id"] = seeded.ChildId,
                        ["occupant_token"] = seeded.OccupantToken,
                        ["seq"] = 4,
                    }),
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.PersistenceUnavailable, failed.Code);
            Assert.Equal(PanePlacement.Hidden, state.GetPane(new PaneId(seeded.ChildId))!.Placement);
            Assert.Equal(seeded.OwnerTabId, state.GetPane(new PaneId(seeded.ChildId))!.TabId.Value);
            Assert.Equal(otherWs, state.Snapshot().FocusedWorkspaceId?.Value);
            Assert.Equal(farTab, state.GetWorkspace(new WorkspaceId(otherWs))!.FocusedTabId?.Value);
            Assert.Equal(tabsBefore, state.ListTabs().Count);
            AssertFocusedTabsBelongToTheirWorkspaces(state);
            Assert.DoesNotContain(ProtocolEventTypes.PanePlacementChanged, EventTypes(sink));

            store.FailSaves = false;
            var shown = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                Json(new JsonObject
                {
                    ["pane_id"] = seeded.ChildId,
                    ["occupant_token"] = seeded.OccupantToken,
                    ["seq"] = 4,
                }),
                CancellationToken.None);
            Assert.True(shown.GetProperty("changed").GetBoolean());
            Assert.Equal("tiled", shown.GetProperty("placement").GetString());
            Assert.Equal(ownerWs.Value, shown.GetProperty("workspace_id").GetString());
        }
        finally
        {
            store.FailSaves = false;
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Failed_show_into_existing_tab_restores_prior_focus()
    {
        var factory = TestPaneFactories.Capturing();
        var sqlite = await OpenStoreAsync();
        var store = new ControllableRuntimeStore(sqlite);
        var (cp, state, _, _) = await NewAsync(factory, persistStore: store);
        try
        {
            var seeded = await SeedHiddenChildAsync(cp, factory);
            var workspaceId = state.GetPane(new PaneId(seeded.ChildId))!.WorkspaceId.Value;
            var dest = await cp.DispatchAsync(
                ProtocolMethods.TabCreate,
                Json(new JsonObject
                {
                    ["workspace_id"] = workspaceId,
                    ["create_pane"] = true,
                    ["command"] = "/bin/echo",
                    ["focus"] = true,
                    ["label"] = "dest",
                }),
                CancellationToken.None);
            var destTab = dest.GetProperty("tab_id").GetString()!;
            var destPane = dest.GetProperty("pane")!.GetProperty("pane_id").GetString()!;
            var far = await cp.DispatchAsync(
                ProtocolMethods.TabCreate,
                Json(new JsonObject
                {
                    ["workspace_id"] = workspaceId,
                    ["create_pane"] = false,
                    ["focus"] = true,
                    ["label"] = "far",
                }),
                CancellationToken.None);
            var farTab = far.GetProperty("tab_id").GetString()!;
            Assert.Equal(farTab, state.GetWorkspace(new WorkspaceId(workspaceId))!.FocusedTabId?.Value);
            store.FailSaves = true;

            var failed = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneShow,
                    Json(new JsonObject
                    {
                        ["pane_id"] = seeded.ChildId,
                        ["occupant_token"] = seeded.OccupantToken,
                        ["tab_id"] = destTab,
                        ["target_pane_id"] = destPane,
                        ["direction"] = "right",
                        ["seq"] = 21,
                    }),
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.PersistenceUnavailable, failed.Code);
            Assert.Equal(PanePlacement.Hidden, state.GetPane(new PaneId(seeded.ChildId))!.Placement);
            Assert.Equal(farTab, state.GetWorkspace(new WorkspaceId(workspaceId))!.FocusedTabId?.Value);
        }
        finally
        {
            store.FailSaves = false;
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Failed_hide_keeps_later_focus_change()
    {
        var factory = TestPaneFactories.Capturing();
        var sqlite = await OpenStoreAsync();
        var store = new ControllableRuntimeStore(sqlite);
        var (cp, state, _, _) = await NewAsync(factory, persistStore: store);
        try
        {
            var seeded = await SeedHiddenChildAsync(cp, factory);
            var shown = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                Json(new JsonObject
                {
                    ["pane_id"] = seeded.ChildId,
                    ["occupant_token"] = seeded.OccupantToken,
                    ["direction"] = "right",
                    ["target_pane_id"] = seeded.TiledId,
                    ["seq"] = 1,
                }),
                CancellationToken.None);
            Assert.True(shown.GetProperty("changed").GetBoolean());
            var tabId = shown.GetProperty("tab_id").GetString()!;
            var extra = await cp.DispatchAsync(
                ProtocolMethods.PaneSplit,
                Json(new JsonObject
                {
                    ["pane_id"] = seeded.TiledId,
                    ["direction"] = "down",
                    ["command"] = "/bin/echo",
                }),
                CancellationToken.None);
            var extraPane = extra.GetProperty("pane_id").GetString()!;
            await cp.DispatchAsync(
                ProtocolMethods.PaneFocus,
                Json(new JsonObject { ["pane_id"] = seeded.ChildId }),
                CancellationToken.None);
            Assert.Equal(seeded.ChildId, state.GetTab(new TabId(tabId))!.FocusedPaneId?.Value);

            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cp.VisibilityAfterMutationBeforePublishForTests = async () =>
            {
                started.TrySetResult();
                await hold.Task;
            };
            var hide = cp.DispatchAsync(
                ProtocolMethods.PaneHide,
                Json(new JsonObject
                {
                    ["pane_id"] = seeded.ChildId,
                    ["occupant_token"] = seeded.OccupantToken,
                    ["seq"] = 2,
                }),
                CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cp.DispatchAsync(
                ProtocolMethods.PaneFocus,
                Json(new JsonObject { ["pane_id"] = extraPane }),
                CancellationToken.None);
            store.FailSaves = true;
            hold.TrySetResult();

            var failed = await Assert.ThrowsAsync<ControlPlaneException>(() => hide);
            Assert.Equal(ProtocolErrorCodes.PersistenceUnavailable, failed.Code);
            Assert.Equal(PanePlacement.Tiled, state.GetPane(new PaneId(seeded.ChildId))!.Placement);
            Assert.Equal(extraPane, state.GetTab(new TabId(tabId))!.FocusedPaneId?.Value);
        }
        finally
        {
            store.FailSaves = false;
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Failed_hide_keeps_sibling_shown_during_persist()
    {
        var factory = TestPaneFactories.Capturing();
        var sqlite = await OpenStoreAsync();
        var store = new ControllableRuntimeStore(sqlite);
        var (cp, state, _, _) = await NewAsync(factory, persistStore: store);
        try
        {
            var seeded = await SeedHiddenChildAsync(cp, factory);
            var shown = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                Json(new JsonObject
                {
                    ["pane_id"] = seeded.ChildId,
                    ["occupant_token"] = seeded.OccupantToken,
                    ["seq"] = 1,
                }),
                CancellationToken.None);
            Assert.True(shown.GetProperty("changed").GetBoolean());
            var questionTab = shown.GetProperty("tab_id").GetString()!;
            Assert.NotEqual(seeded.OwnerTabId, questionTab);

            var parentToken = RequireOccupantToken(
                factory.Options.Last(o => o.Id.Value == seeded.ParentId));
            var sibling = await cp.DispatchAsync(
                ProtocolMethods.PaneCreate,
                Json(new JsonObject
                {
                    ["workspace_id"] = state.GetPane(new PaneId(seeded.ChildId))!.WorkspaceId.Value,
                    ["command"] = "/bin/echo",
                    ["placement"] = "hidden",
                    ["parent_pane_id"] = seeded.ParentId,
                    ["occupant_token"] = parentToken,
                }),
                CancellationToken.None);
            var siblingId = sibling.GetProperty("pane_id").GetString()!;
            var siblingToken = RequireOccupantToken(
                factory.Options.Last(o => o.Id.Value == siblingId));
            Assert.Equal(PanePlacement.Hidden, state.GetPane(new PaneId(siblingId))!.Placement);
            Assert.Equal(questionTab, state.GetPane(new PaneId(siblingId))!.TabId.Value);

            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Hold only the first mutation (the hide). The sibling show runs the
            // same hook and must pass through, or the test waits on itself.
            var holds = 0;
            cp.VisibilityAfterMutationBeforePublishForTests = async () =>
            {
                if (Interlocked.Increment(ref holds) != 1)
                    return;
                started.TrySetResult();
                await hold.Task;
            };
            var hide = cp.DispatchAsync(
                ProtocolMethods.PaneHide,
                Json(new JsonObject
                {
                    ["pane_id"] = seeded.ChildId,
                    ["occupant_token"] = seeded.OccupantToken,
                    ["seq"] = 2,
                }),
                CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var siblingShown = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                Json(new JsonObject
                {
                    ["pane_id"] = siblingId,
                    ["occupant_token"] = siblingToken,
                    ["seq"] = 1,
                }),
                CancellationToken.None);
            var siblingTab = siblingShown.GetProperty("tab_id").GetString()!;
            Assert.NotEqual(questionTab, siblingTab);
            store.FailSaves = true;
            hold.TrySetResult();

            var failed = await Assert.ThrowsAsync<ControlPlaneException>(() => hide);
            Assert.Equal(ProtocolErrorCodes.PersistenceUnavailable, failed.Code);
            var child = state.GetPane(new PaneId(seeded.ChildId))!;
            Assert.Equal(PanePlacement.Tiled, child.Placement);
            Assert.Equal(questionTab, child.TabId.Value);
            Assert.NotNull(state.GetTab(new TabId(questionTab)));
            var kept = state.GetPane(new PaneId(siblingId))!;
            Assert.Equal(PanePlacement.Tiled, kept.Placement);
            Assert.Equal(siblingTab, kept.TabId.Value);
            Assert.NotNull(state.GetTab(new TabId(siblingTab)));
        }
        finally
        {
            store.FailSaves = false;
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Durable_failure_preserves_unrelated_tab_mutations()
    {
        var factory = TestPaneFactories.Capturing();
        var sqlite = await OpenStoreAsync();
        var store = new ControllableRuntimeStore(sqlite);
        var (cp, state, _, _) = await NewAsync(factory, persistStore: store);
        try
        {
            var first = await SeedHiddenChildAsync(cp, factory);
            var workspaceId = state.GetPane(new PaneId(first.ChildId))!.WorkspaceId.Value;
            var extra = await cp.DispatchAsync(
                ProtocolMethods.TabCreate,
                Json(new JsonObject
                {
                    ["workspace_id"] = workspaceId,
                    ["create_pane"] = true,
                    ["command"] = "/bin/echo",
                    ["focus"] = false,
                    ["label"] = "side",
                }),
                CancellationToken.None);
            var extraTab = extra.GetProperty("tab_id").GetString()!;
            var extraPane = extra.GetProperty("pane")!.GetProperty("pane_id").GetString()!;
            await cp.DispatchAsync(
                ProtocolMethods.PaneSplit,
                Json(new JsonObject
                {
                    ["pane_id"] = extraPane,
                    ["direction"] = "right",
                    ["command"] = "/bin/echo",
                }),
                CancellationToken.None);
            var extraConn = new FakeConnection("conn_extra");
            var ratioPane = state.GetTab(new TabId(extraTab))!.FocusedPaneId?.Value ?? extraPane;
            var extraLease = await cp.DispatchAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                Json(new JsonObject { ["pane_id"] = ratioPane, ["scope"] = "input" }),
                extraConn,
                CancellationToken.None);
            var extraLeaseId = extraLease.GetProperty("lease_id").GetString()!;

            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cp.VisibilityAfterMutationBeforePublishForTests = async () =>
            {
                started.TrySetResult();
                await hold.Task;
            };

            var showFirst = cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                Json(new JsonObject
                {
                    ["pane_id"] = first.ChildId,
                    ["occupant_token"] = first.OccupantToken,
                    ["seq"] = 1,
                }),
                CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cp.DispatchAsync(
                ProtocolMethods.TabRename,
                Json(new JsonObject { ["tab_id"] = extraTab, ["label"] = "renamed-side" }),
                CancellationToken.None);
            await cp.DispatchAsync(
                ProtocolMethods.LayoutSetSplitRatio,
                Json(new JsonObject
                {
                    ["tab_id"] = extraTab,
                    ["path"] = new JsonArray(),
                    ["ratio"] = 0.35,
                    ["lease_id"] = extraLeaseId,
                }),
                extraConn,
                CancellationToken.None);
            await cp.DispatchAsync(
                ProtocolMethods.PaneZoom,
                Json(new JsonObject { ["pane_id"] = extraPane, ["mode"] = "on" }),
                CancellationToken.None);
            await cp.DispatchAsync(
                ProtocolMethods.TabFocus,
                Json(new JsonObject { ["tab_id"] = extraTab }),
                CancellationToken.None);
            store.FailSaves = true;
            hold.TrySetResult();

            var failed = await Assert.ThrowsAsync<ControlPlaneException>(() => showFirst);
            Assert.Equal(ProtocolErrorCodes.PersistenceUnavailable, failed.Code);
            Assert.Equal(PanePlacement.Hidden, state.GetPane(new PaneId(first.ChildId))!.Placement);
            Assert.Equal(first.OwnerTabId, state.GetPane(new PaneId(first.ChildId))!.TabId.Value);
            var kept = state.GetTab(new TabId(extraTab))!;
            Assert.Equal("renamed-side", kept.Label);
            Assert.True(kept.Zoomed);
            Assert.Equal(extraPane, kept.ZoomedPaneId?.Value);
            Assert.Equal(0.35, Assert.IsType<LayoutSplitNode>(kept.LayoutRoot).Ratio, 3);
            Assert.Equal(extraTab, state.GetWorkspace(new WorkspaceId(workspaceId))!.FocusedTabId?.Value);
        }
        finally
        {
            store.FailSaves = false;
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Source_tab_close_during_failed_show_keeps_pane_on_a_live_tab()
    {
        var factory = TestPaneFactories.Capturing();
        var sqlite = await OpenStoreAsync();
        var store = new ControllableRuntimeStore(sqlite);
        var (cp, state, _, _) = await NewAsync(factory, persistStore: store);
        try
        {
            var seeded = await SeedHiddenChildAsync(cp, factory);
            var workspaceId = state.GetPane(new PaneId(seeded.ChildId))!.WorkspaceId.Value;
            await cp.DispatchAsync(
                ProtocolMethods.TabCreate,
                Json(new JsonObject
                {
                    ["workspace_id"] = workspaceId,
                    ["create_pane"] = false,
                    ["focus"] = false,
                    ["label"] = "spare",
                }),
                CancellationToken.None);

            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cp.VisibilityAfterMutationBeforePublishForTests = async () =>
            {
                started.TrySetResult();
                await hold.Task;
            };
            var show = cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                Json(new JsonObject
                {
                    ["pane_id"] = seeded.ChildId,
                    ["occupant_token"] = seeded.OccupantToken,
                    ["seq"] = 1,
                }),
                CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cp.DispatchAsync(
                ProtocolMethods.TabClose,
                Json(new JsonObject { ["tab_id"] = seeded.OwnerTabId }),
                CancellationToken.None);
            store.FailSaves = true;
            hold.TrySetResult();

            var failed = await Assert.ThrowsAsync<ControlPlaneException>(() => show);
            Assert.Equal(ProtocolErrorCodes.PersistenceUnavailable, failed.Code);
            var pane = state.GetPane(new PaneId(seeded.ChildId));
            Assert.NotNull(pane);
            Assert.NotNull(state.GetTab(pane.TabId));
            Assert.Contains(
                AppState.OccupantPaneIds(state.GetTab(pane.TabId)!),
                id => id.Value == seeded.ChildId);
        }
        finally
        {
            store.FailSaves = false;
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Cancelled_show_after_mutate_still_publishes()
    {
        var factory = TestPaneFactories.Capturing();
        var (cp, _, _, sink) = await NewAsync(factory);
        try
        {
            var seeded = await SeedHiddenChildAsync(cp, factory);
            var cts = new CancellationTokenSource();
            cp.VisibilityAfterMutationBeforePublishForTests = () =>
            {
                cts.Cancel();
                return Task.CompletedTask;
            };
            sink.Lines.Clear();
            var shown = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                Json(new JsonObject
                {
                    ["pane_id"] = seeded.ChildId,
                    ["occupant_token"] = seeded.OccupantToken,
                    ["seq"] = 1,
                }),
                cts.Token);
            Assert.True(shown.GetProperty("changed").GetBoolean());
            await WaitUntilAsync(() => EventTypes(sink).Contains(ProtocolEventTypes.PanePlacementChanged));
            Assert.Contains(ProtocolEventTypes.LayoutUpdated, EventTypes(sink));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Tab_close_during_delayed_show_does_not_emit_stale_placement()
    {
        var factory = TestPaneFactories.Capturing();
        var (cp, state, _, sink) = await NewAsync(factory);
        try
        {
            var seeded = await SeedHiddenChildAsync(cp, factory);
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cp.VisibilityAfterMutationBeforePublishForTests = async () =>
            {
                started.TrySetResult();
                await hold.Task;
            };
            sink.Lines.Clear();

            var show = cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                Json(new JsonObject
                {
                    ["pane_id"] = seeded.ChildId,
                    ["occupant_token"] = seeded.OccupantToken,
                    ["seq"] = 1,
                }),
                CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var destTab = state.GetPane(new PaneId(seeded.ChildId))!.TabId.Value;
            Assert.NotEqual(seeded.OwnerTabId, destTab);
            var close = cp.DispatchAsync(
                ProtocolMethods.TabClose,
                Json(new JsonObject { ["tab_id"] = destTab }),
                CancellationToken.None);
            await Task.Delay(50);
            hold.TrySetResult();
            await Assert.ThrowsAsync<ControlPlaneException>(() => show);
            await close;
            Assert.Null(state.GetPane(new PaneId(seeded.ChildId)));
            await WaitUntilAsync(() => EventTypes(sink).Contains(ProtocolEventTypes.PaneLifecycle));
            var types = EventTypes(sink);
            Assert.DoesNotContain(ProtocolEventTypes.PanePlacementChanged, types);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Failed_graph_retry_same_seq_applies_and_stale_is_ignored()
    {
        var factory = TestPaneFactories.Capturing();
        var (cp, _, _, sink) = await NewAsync(factory);
        try
        {
            var seeded = await SeedHiddenChildAsync(cp, factory);
            var missing = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneShow,
                    Json(new JsonObject
                    {
                        ["pane_id"] = seeded.ChildId,
                        ["occupant_token"] = seeded.OccupantToken,
                        ["direction"] = "right",
                        ["target_pane_id"] = "missing",
                        ["seq"] = 7,
                    }),
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidParams, missing.Code);

            var shown = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                Json(new JsonObject
                {
                    ["pane_id"] = seeded.ChildId,
                    ["occupant_token"] = seeded.OccupantToken,
                    ["direction"] = "right",
                    ["target_pane_id"] = seeded.TiledId,
                    ["seq"] = 7,
                }),
                CancellationToken.None);
            Assert.True(shown.GetProperty("changed").GetBoolean());

            sink.Lines.Clear();
            var stale = await cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                Json(new JsonObject
                {
                    ["pane_id"] = seeded.ChildId,
                    ["occupant_token"] = seeded.OccupantToken,
                    ["direction"] = "right",
                    ["target_pane_id"] = seeded.TiledId,
                    ["seq"] = 7,
                }),
                CancellationToken.None);
            Assert.False(stale.GetProperty("changed").GetBoolean());
            Assert.DoesNotContain(ProtocolEventTypes.PanePlacementChanged, EventTypes(sink));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Secrets_stay_off_get_list_snapshot_layout_and_persist()
    {
        var factory = TestPaneFactories.Capturing();
        var (cp, _, store, _) = await NewAsync(factory);
        try
        {
            var seeded = await SeedHiddenChildAsync(cp, factory);
            var created = await cp.DispatchAsync(
                ProtocolMethods.PaneGet,
                Json(new JsonObject { ["pane_id"] = seeded.ChildId }),
                CancellationToken.None);
            Assert.Equal(seeded.ParentId, created.GetProperty("parent_pane_id").GetString());
            Assert.False(created.TryGetProperty("parent_capability", out _));
            Assert.False(created.TryGetProperty("occupant_token", out _));

            var list = await cp.DispatchAsync(ProtocolMethods.PaneList, null, CancellationToken.None);
            var listText = list.GetRawText();
            Assert.DoesNotContain(seeded.OccupantToken, listText, StringComparison.Ordinal);
            Assert.DoesNotContain(seeded.ParentCapability, listText, StringComparison.Ordinal);

            var snap = await cp.DispatchAsync(ProtocolMethods.SessionSnapshot, null, CancellationToken.None);
            Assert.True(snap.TryGetProperty("attach_client_ids", out var ids));
            Assert.Equal(JsonValueKind.Array, ids.ValueKind);
            var snapText = snap.GetRawText();
            Assert.DoesNotContain(seeded.OccupantToken, snapText, StringComparison.Ordinal);
            Assert.DoesNotContain(seeded.ParentCapability, snapText, StringComparison.Ordinal);

            var layout = await cp.DispatchAsync(
                ProtocolMethods.LayoutExport,
                Json(new JsonObject { ["tab_id"] = seeded.OwnerTabId }),
                CancellationToken.None);
            var layoutText = layout.GetRawText();
            Assert.DoesNotContain(seeded.OccupantToken, layoutText, StringComparison.Ordinal);
            Assert.DoesNotContain(seeded.ParentCapability, layoutText, StringComparison.Ordinal);

            var loaded = (await store.TryLoadAsync("pane-auth")).Value!;
            Assert.Equal(seeded.ParentId, loaded.Panes[seeded.ChildId].ParentPaneId?.Value);
            var db = await File.ReadAllBytesAsync(_paths.DatabasePath);
            var dbText = System.Text.Encoding.UTF8.GetString(db);
            Assert.DoesNotContain(seeded.OccupantToken, dbText, StringComparison.Ordinal);
            Assert.DoesNotContain(seeded.ParentCapability, dbText, StringComparison.Ordinal);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Close_and_exit_revoke_tokens_and_snapshot_lists_attach_owners()
    {
        var scripted = TestPaneFactories.Scripted();
        var (cp, _, _, _) = await NewAsync(scripted);
        try
        {
            var ws = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                Json(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["create_pane"] = true,
                    ["command"] = "/bin/echo",
                }),
                CancellationToken.None);
            var parentId = ws.GetProperty("pane").GetProperty("pane_id").GetString()!;
            var workspaceId = ws.GetProperty("workspace_id").GetString()!;
            var child = await cp.DispatchAsync(
                ProtocolMethods.PaneCreate,
                Json(new JsonObject
                {
                    ["workspace_id"] = workspaceId,
                    ["command"] = "/bin/echo",
                    ["placement"] = "hidden",
                }),
                CancellationToken.None);
            var childId = child.GetProperty("pane_id").GetString()!;
            var capability = child.GetProperty("parent_capability").GetString()!;
            Assert.True(cp.Placement.HasOccupant(new PaneId(childId)));
            Assert.True(cp.Placement.HasParentCapability(new PaneId(childId)));

            scripted.Created.First(r => r.Id.Value == childId).FireExited(0);
            Assert.False(cp.Placement.HasOccupant(new PaneId(childId)));
            Assert.True(cp.Placement.HasParentCapability(new PaneId(childId)));

            await cp.DispatchAsync(
                ProtocolMethods.PaneClose,
                Json(new JsonObject { ["pane_id"] = childId }),
                CancellationToken.None);
            Assert.False(cp.Placement.HasParentCapability(new PaneId(childId)));

            var created = cp.Attachments.Create(
                parentId,
                "conn_attach",
                "sub_attach",
                AttachmentModes.Observe,
                leaseId: null);
            Assert.True(created.Ok);
            var snap = await cp.DispatchAsync(ProtocolMethods.SessionSnapshot, null, CancellationToken.None);
            var owners = snap.GetProperty("attach_client_ids").EnumerateArray()
                .Select(e => e.GetString())
                .ToArray();
            Assert.Contains("conn_attach", owners);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Reset_runs_before_delayed_placement_events()
    {
        var factory = TestPaneFactories.Capturing();
        var (cp, _, _, sink) = await NewAsync(factory);
        try
        {
            var seeded = await SeedHiddenChildAsync(cp, factory);
            sink.Lines.Clear();
            var resetPanes = new List<string>();
            cp.VisibilityResetObservedForTests = resetPanes.Add;
            var resetBeforeEvents = false;
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cp.VisibilityAfterMutationBeforePublishForTests = async () =>
            {
                resetBeforeEvents = resetPanes.Contains(seeded.ChildId)
                    && !EventTypes(sink).Contains(ProtocolEventTypes.PanePlacementChanged)
                    && !EventTypes(sink).Contains(ProtocolEventTypes.LayoutUpdated);
                await hold.Task;
            };

            var show = cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                Json(new JsonObject
                {
                    ["pane_id"] = seeded.ChildId,
                    ["occupant_token"] = seeded.OccupantToken,
                    ["seq"] = 1,
                }),
                CancellationToken.None);
            await WaitUntilAsync(() => resetBeforeEvents);
            Assert.DoesNotContain(ProtocolEventTypes.PanePlacementChanged, EventTypes(sink));
            hold.TrySetResult();
            var shown = await show;
            Assert.True(shown.GetProperty("changed").GetBoolean());
            Assert.True(resetBeforeEvents);
            await WaitUntilAsync(() => EventTypes(sink).Contains(ProtocolEventTypes.PanePlacementChanged));
            Assert.Contains(seeded.ChildId, resetPanes);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Concurrent_delayed_show_hide_and_close_keep_publication_order()
    {
        var factory = TestPaneFactories.Capturing();
        var (cp, state, _, sink) = await NewAsync(factory);
        try
        {
            var seeded = await SeedHiddenChildAsync(cp, factory);
            sink.Lines.Clear();
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var seen = 0;
            cp.VisibilityAfterMutationBeforePublishForTests = async () =>
            {
                if (Interlocked.Increment(ref seen) == 1)
                {
                    first.TrySetResult();
                    await release.Task;
                }
            };

            var show = cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                Json(new JsonObject
                {
                    ["pane_id"] = seeded.ChildId,
                    ["occupant_token"] = seeded.OccupantToken,
                    ["seq"] = 1,
                }),
                CancellationToken.None);
            await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var hide = cp.DispatchAsync(
                ProtocolMethods.PaneHide,
                Json(new JsonObject
                {
                    ["pane_id"] = seeded.ChildId,
                    ["parent_capability"] = seeded.ParentCapability,
                    ["seq"] = 1,
                }),
                CancellationToken.None);
            await Task.Delay(50);
            release.TrySetResult();
            Assert.True((await show).GetProperty("changed").GetBoolean());
            Assert.True((await hide).GetProperty("changed").GetBoolean());
            Assert.Equal(PanePlacement.Hidden, state.GetPane(new PaneId(seeded.ChildId))!.Placement);

            var types = EventTypes(sink)
                .Where(t => t is ProtocolEventTypes.PanePlacementChanged or ProtocolEventTypes.LayoutUpdated)
                .ToList();
            Assert.Equal(4, types.Count);
            Assert.Equal(ProtocolEventTypes.PanePlacementChanged, types[0]);
            Assert.Equal(ProtocolEventTypes.LayoutUpdated, types[1]);
            Assert.Equal(ProtocolEventTypes.PanePlacementChanged, types[2]);
            Assert.Equal(ProtocolEventTypes.LayoutUpdated, types[3]);

            var delayed = await SeedHiddenChildAsync(cp, factory);
            first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            seen = 0;
            cp.VisibilityAfterMutationBeforePublishForTests = async () =>
            {
                if (Interlocked.Increment(ref seen) == 1)
                {
                    first.TrySetResult();
                    await release.Task;
                }
            };
            sink.Lines.Clear();
            var show2 = cp.DispatchAsync(
                ProtocolMethods.PaneShow,
                Json(new JsonObject
                {
                    ["pane_id"] = delayed.ChildId,
                    ["occupant_token"] = delayed.OccupantToken,
                    ["seq"] = 1,
                }),
                CancellationToken.None);
            await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var close = cp.DispatchAsync(
                ProtocolMethods.PaneClose,
                Json(new JsonObject { ["pane_id"] = delayed.ChildId }),
                CancellationToken.None);
            await Task.Delay(50);
            release.TrySetResult();
            await show2;
            await close;
            Assert.Null(state.GetPane(new PaneId(delayed.ChildId)));
            await WaitUntilAsync(() =>
            {
                var types = EventTypes(sink);
                return types.Contains(ProtocolEventTypes.PanePlacementChanged)
                    && types.Contains(ProtocolEventTypes.PaneLifecycle);
            });
            var closeTypes = EventTypes(sink);
            Assert.True(
                closeTypes.IndexOf(ProtocolEventTypes.PanePlacementChanged)
                < closeTypes.IndexOf(ProtocolEventTypes.PaneLifecycle));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private async Task<(string ParentId, string WorkspaceId, FakeConnection Owner, string LeaseId)>
        SeedParentWithLeaseAsync(ControlPlaneService cp, string scope)
    {
        var ws = await cp.DispatchAsync(
            ProtocolMethods.WorkspaceCreate,
            Json(new JsonObject
            {
                ["cwd"] = Path.GetTempPath(),
                ["create_pane"] = true,
                ["command"] = "/bin/echo",
            }),
            CancellationToken.None);
        var parentId = ws.GetProperty("pane").GetProperty("pane_id").GetString()!;
        var workspaceId = ws.GetProperty("workspace_id").GetString()!;
        var owner = new FakeConnection("conn_owner");
        var claim = await cp.DispatchAsync(
            ProtocolMethods.RuntimeLeaseClaim,
            Json(new JsonObject { ["pane_id"] = parentId, ["scope"] = scope }),
            owner,
            CancellationToken.None);
        Assert.Equal(LeaseOutcomes.Granted, claim.GetProperty("outcome").GetString());
        return (parentId, workspaceId, owner, claim.GetProperty("lease_id").GetString()!);
    }

    private async Task<SeededChild> SeedHiddenChildAsync(
        ControlPlaneService cp,
        TestPaneFactories.CapturingPaneFactory factory)
    {
        var (parentId, workspaceId, _, _) = await SeedParentWithLeaseAsync(cp, "input");
        var parentToken = RequireOccupantToken(
            factory.Options.Last(o => o.Id.Value == parentId));
        var parent = await cp.DispatchAsync(
            ProtocolMethods.PaneGet,
            Json(new JsonObject { ["pane_id"] = parentId }),
            CancellationToken.None);
        var child = await cp.DispatchAsync(
            ProtocolMethods.PaneCreate,
            Json(new JsonObject
            {
                ["workspace_id"] = workspaceId,
                ["command"] = "/bin/echo",
                ["placement"] = "hidden",
                ["parent_pane_id"] = parentId,
                ["occupant_token"] = parentToken,
            }),
            CancellationToken.None);
        return new SeededChild(
            parentId,
            child.GetProperty("pane_id").GetString()!,
            parent.GetProperty("tab_id").GetString()!,
            parentId,
            RequireOccupantToken(factory.LastOptions),
            child.GetProperty("parent_capability").GetString()!);
    }

    private async Task<SqliteRuntimeSessionStore> OpenStoreAsync()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        return new SqliteRuntimeSessionStore(_paths);
    }

    private async Task<(ControlPlaneService Cp, AppState State, SqliteRuntimeSessionStore Store, CapturingSink Sink)>
        NewAsync(
            IPaneRuntimeFactory factory,
            IEventSubscriptionHub? hub = null,
            IRuntimeSessionStore? persistStore = null)
    {
        var state = new AppState(SessionId.New("pane-auth"));
        state.UpdateSession(s => s with { Name = "pane-auth", LifecycleState = SessionLifecycle.Ready });
        var store = persistStore as SqliteRuntimeSessionStore
            ?? (persistStore as ControllableRuntimeStore)?.Inner
            ?? await OpenStoreAsync();
        var usedStore = persistStore ?? store;
        Assert.True((await usedStore.SaveAsync(state.Snapshot())).IsOk);
        var manifests = new SqliteJournalManifestStore(_paths);
        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);
        hub ??= new EventSubscriptionHub();
        var cp = new ControlPlaneService(
            state,
            factory,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: usedStore,
            journal: journal,
            subscriptions: hub);
        var sink = new CapturingSink();
        var sub = await cp.DispatchAsync(
            ProtocolMethods.EventsSubscribe,
            Json(new JsonObject
            {
                ["from_seq"] = 0,
                ["types"] = new JsonArray(
                    "lifecycle",
                    ProtocolEventTypes.PanePlacementChanged,
                    ProtocolEventTypes.LayoutUpdated),
                ["live"] = true,
            }),
            sink,
            CancellationToken.None);
        await cp.CompleteEventsSubscribeAsync(
            sub.GetProperty("subscription_id").GetString()!,
            [],
            sink,
            CancellationToken.None);
        return (cp, state, store, sink);
    }

    private static string RequireOccupantToken(PaneSpawnOptions? options)
    {
        Assert.NotNull(options);
        Assert.NotNull(options.Env);
        Assert.True(options.Env.TryGetValue(PaneIdEnvironment.HypaPaneToken, out var token));
        Assert.False(string.IsNullOrWhiteSpace(token));
        return token;
    }

    private static void AssertFocusedTabsBelongToTheirWorkspaces(AppState state)
    {
        var snap = state.Snapshot();
        foreach (var workspace in snap.Workspaces.Values)
        {
            if (workspace.FocusedTabId is not { } focused)
                continue;
            Assert.Contains(workspace.TabIds, id => id.Value == focused.Value);
            Assert.True(snap.Tabs.TryGetValue(focused.Value, out var tab));
            Assert.Equal(workspace.Id.Value, tab.WorkspaceId.Value);
        }
    }

    private static JsonElement Json(JsonObject obj) =>
        JsonDocument.Parse(obj.ToJsonString()).RootElement.Clone();

    private static List<string> EventTypes(CapturingSink sink) =>
        sink.Parsed
            .Where(ev => ev.TryGetProperty("params", out var p)
                && p.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String)
            .Select(ev => ev.GetProperty("params").GetProperty("type").GetString()!)
            .ToList();

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        var start = DateTime.UtcNow;
        while (!ready())
        {
            if (DateTime.UtcNow - start > TimeSpan.FromSeconds(5))
                throw new TimeoutException("condition was not met");
            await Task.Delay(10);
        }
    }

    private sealed record SeededChild(
        string ParentId,
        string ChildId,
        string OwnerTabId,
        string TiledId,
        string OccupantToken,
        string ParentCapability);

    private sealed class ControllableRuntimeStore(SqliteRuntimeSessionStore inner) : IRuntimeSessionStore
    {
        public SqliteRuntimeSessionStore Inner { get; } = inner;
        public bool FailSaves { get; set; }

        public Task<RuntimeResult<SessionState?>> TryLoadAsync(
            string sessionName,
            CancellationToken ct = default) =>
            Inner.TryLoadAsync(sessionName, ct);

        public Task<RuntimeResult<RuntimeUnit>> SaveAsync(
            SessionState state,
            CancellationToken ct = default,
            bool removeMissingPanes = false) =>
            FailSaves
                ? Task.FromResult(RuntimeResult<RuntimeUnit>.Fail(
                    RuntimePersistenceError.Io("injected store failure")))
                : Inner.SaveAsync(state, ct, removeMissingPanes);

        public Task<RuntimeResult<RuntimeUnit>> DeletePaneAsync(
            PaneId paneId,
            CancellationToken ct = default) =>
            Inner.DeletePaneAsync(paneId, ct);

        public Task<RuntimeResult<string>> GetRuntimeSessionIdAsync(CancellationToken ct = default) =>
            Inner.GetRuntimeSessionIdAsync(ct);
    }

    private sealed class FakeConnection(string id) : IClientConnection
    {
        public string ConnectionId { get; } = id;
        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CapturingSink : IClientConnection
    {
        private readonly object _gate = new();
        public string ConnectionId { get; } = "c_auth";
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
