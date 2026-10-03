using System.Text.Json;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentServer;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

[Collection("ProcessSpawnTests")]
public class RuntimeRestartRestoreTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public RuntimeRestartRestoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-h02-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose()
    {
        SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);
    }

    private async Task EnsureMigratedAsync()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        var r = await migrator.MigrateAsync();
        Assert.True(r.IsOk, r.IsOk ? null : r.Error.Message);
    }

    [Fact]
    public async Task Restore_marks_missing_pid_as_orphaned_or_exited_and_clears_alive()
    {
        await EnsureMigratedAsync();
        var store = new SqliteRuntimeSessionStore(_paths);

        var sessionId = SessionId.New("orphan");
        var wsId = WorkspaceId.New();
        var tabId = TabId.New();
        var liveMissing = PaneId.New();
        var alreadyExited = PaneId.New();

        var state = new SessionState
        {
            Id = sessionId,
            Name = "orphan",
            FocusedWorkspaceId = wsId,
            Workspaces = new Dictionary<string, WorkspaceState>
            {
                [wsId.Value] = new WorkspaceState
                {
                    Id = wsId,
                    Label = "ws",
                    Cwd = "/tmp",
                    FocusedTabId = tabId,
                    TabIds = [tabId],
                },
            },
            Tabs = new Dictionary<string, TabState>
            {
                [tabId.Value] = new TabState
                {
                    Id = tabId,
                    WorkspaceId = wsId,
                    Label = "main",
                    Ordinal = 0,
                    PaneIds = [liveMissing, alreadyExited],
                },
            },
            Panes = new Dictionary<string, PaneState>
            {
                [liveMissing.Value] = new PaneState
                {
                    Id = liveMissing,
                    TabId = tabId,
                    WorkspaceId = wsId,
                    Label = "a",
                    Cwd = "/tmp",
                    Command = "sleep",
                    LifecycleState = PaneLifecycle.Running,
                    Pid = 999_001,
                    IsAlive = true,
                    AgentStatus = AgentStatus.Working,
                },
                [alreadyExited.Value] = new PaneState
                {
                    Id = alreadyExited,
                    TabId = tabId,
                    WorkspaceId = wsId,
                    Label = "b",
                    Cwd = "/tmp",
                    Command = "true",
                    LifecycleState = PaneLifecycle.Running,
                    Pid = 999_002,
                    ExitCode = 7,
                    IsAlive = true,
                    AgentStatus = AgentStatus.Done,
                },
            },
        };

        Assert.True((await store.SaveAsync(state)).IsOk);
        var loaded = (await store.TryLoadAsync("orphan")).Value!;
        var probe = new FixedLivenessProbe(alive: false);
        var reconciled = SessionGraphRestorer.ReconcileDeadPids(loaded, probe);

        var a = reconciled.Panes[liveMissing.Value];
        Assert.Null(a.Pid);
        Assert.False(a.IsAlive);
        Assert.Equal(PaneLifecycle.Orphaned, a.LifecycleState);
        // Crash reconcile demotes Working/Unknown → Done (mirrors graceful shutdown).
        Assert.Equal(AgentStatus.Done, a.AgentStatus);

        var b = reconciled.Panes[alreadyExited.Value];
        Assert.Null(b.Pid);
        Assert.False(b.IsAlive);
        Assert.Equal(PaneLifecycle.Exited, b.LifecycleState);
        Assert.Equal(7, b.ExitCode);
        Assert.Equal(AgentStatus.Done, b.AgentStatus);
    }

    [Fact]
    public void Reconcile_clamps_occupant_generation_zero_to_one()
    {
        var wsId = WorkspaceId.New();
        var tabId = TabId.New();
        var paneId = PaneId.New();
        var state = new SessionState
        {
            Id = SessionId.New("gen0"),
            Name = "gen0",
            FocusedWorkspaceId = wsId,
            Workspaces = new Dictionary<string, WorkspaceState>
            {
                [wsId.Value] = new WorkspaceState
                {
                    Id = wsId,
                    Label = "ws",
                    Cwd = "/tmp",
                    FocusedTabId = tabId,
                    TabIds = [tabId],
                },
            },
            Tabs = new Dictionary<string, TabState>
            {
                [tabId.Value] = new TabState
                {
                    Id = tabId,
                    WorkspaceId = wsId,
                    Label = "main",
                    Ordinal = 0,
                    PaneIds = [paneId],
                },
            },
            Panes = new Dictionary<string, PaneState>
            {
                [paneId.Value] = new PaneState
                {
                    Id = paneId,
                    TabId = tabId,
                    WorkspaceId = wsId,
                    Label = "shell",
                    Cwd = "/tmp",
                    Command = "bash",
                    LifecycleState = PaneLifecycle.Orphaned,
                    OccupantGeneration = 0,
                    IsAlive = false,
                    AgentStatus = AgentStatus.Done,
                },
            },
        };

        var reconciled = SessionGraphRestorer.ReconcileDeadPids(state, new FixedLivenessProbe(false));
        Assert.Equal(1, reconciled.Panes[paneId.Value].OccupantGeneration);
        Assert.Equal(1, SessionGraphRestorer.NormalizeOccupantGeneration(0));
        Assert.Equal(3, SessionGraphRestorer.NormalizeOccupantGeneration(3));
    }

    [Fact]
    public async Task Hidden_column_restore_stays_hidden()
    {
        await EnsureMigratedAsync();
        var store = new SqliteRuntimeSessionStore(_paths);

        var sessionId = SessionId.New("hidden-restore");
        var wsId = WorkspaceId.New();
        var tabId = TabId.New();
        var tiled = PaneId.New();
        var hidden = PaneId.New();

        var state = new SessionState
        {
            Id = sessionId,
            Name = "hidden-restore",
            FocusedWorkspaceId = wsId,
            Workspaces = new Dictionary<string, WorkspaceState>
            {
                [wsId.Value] = new WorkspaceState
                {
                    Id = wsId,
                    Label = "ws",
                    Cwd = "/tmp",
                    FocusedTabId = tabId,
                    TabIds = [tabId],
                },
            },
            Tabs = new Dictionary<string, TabState>
            {
                [tabId.Value] = new TabState
                {
                    Id = tabId,
                    WorkspaceId = wsId,
                    Label = "main",
                    Ordinal = 0,
                    PaneIds = [tiled],
                    HiddenPaneIds = [hidden],
                    FocusedPaneId = hidden,
                    LayoutRoot = LayoutTreeOperations.FromPane(new PaneState
                    {
                        Id = tiled,
                        TabId = tabId,
                        WorkspaceId = wsId,
                        Label = "shell",
                        Cwd = "/tmp",
                    }),
                },
            },
            Panes = new Dictionary<string, PaneState>
            {
                [tiled.Value] = new PaneState
                {
                    Id = tiled,
                    TabId = tabId,
                    WorkspaceId = wsId,
                    Label = "shell",
                    Cwd = "/tmp",
                    Command = "bash",
                    LifecycleState = PaneLifecycle.Running,
                    Placement = PanePlacement.Tiled,
                },
                [hidden.Value] = new PaneState
                {
                    Id = hidden,
                    TabId = tabId,
                    WorkspaceId = wsId,
                    Label = "agent",
                    Cwd = "/tmp",
                    Command = "pi",
                    LifecycleState = PaneLifecycle.Running,
                    Placement = PanePlacement.Hidden,
                },
            },
        };

        Assert.True((await store.SaveAsync(state)).IsOk);
        var loaded = (await store.TryLoadAsync("hidden-restore")).Value!;
        var tab = loaded.Tabs[tabId.Value];
        Assert.Contains(tab.PaneIds, p => p.Value == tiled.Value);
        Assert.DoesNotContain(tab.PaneIds, p => p.Value == hidden.Value);
        Assert.Contains(tab.HiddenPaneIds, p => p.Value == hidden.Value);
        Assert.Equal(tiled.Value, tab.FocusedPaneId?.Value);
        Assert.Equal(PanePlacement.Hidden, loaded.Panes[hidden.Value].Placement);
        Assert.Equal(PanePlacement.Tiled, loaded.Panes[tiled.Value].Placement);
        Assert.True(LayoutTreeOperations.ContainsPane(tab.LayoutRoot, tiled));
        Assert.False(LayoutTreeOperations.ContainsPane(tab.LayoutRoot, hidden));
    }

    [Fact]
    public async Task Hidden_stored_layout_leaf_is_stripped_on_load()
    {
        await EnsureMigratedAsync();
        var store = new SqliteRuntimeSessionStore(_paths);

        var sessionId = SessionId.New("hidden-layout-strip");
        var wsId = WorkspaceId.New();
        var tabId = TabId.New();
        var tiled = PaneId.New();
        var hidden = PaneId.New();
        var tiledPane = new PaneState
        {
            Id = tiled,
            TabId = tabId,
            WorkspaceId = wsId,
            Label = "shell",
            Cwd = "/tmp",
            Command = "bash",
            LifecycleState = PaneLifecycle.Running,
            Placement = PanePlacement.Tiled,
        };
        var hiddenPane = new PaneState
        {
            Id = hidden,
            TabId = tabId,
            WorkspaceId = wsId,
            Label = "agent",
            Cwd = "/tmp",
            Command = "pi",
            LifecycleState = PaneLifecycle.Running,
            Placement = PanePlacement.Hidden,
        };

        var state = new SessionState
        {
            Id = sessionId,
            Name = "hidden-layout-strip",
            FocusedWorkspaceId = wsId,
            Workspaces = new Dictionary<string, WorkspaceState>
            {
                [wsId.Value] = new WorkspaceState
                {
                    Id = wsId,
                    Label = "ws",
                    Cwd = "/tmp",
                    FocusedTabId = tabId,
                    TabIds = [tabId],
                },
            },
            Tabs = new Dictionary<string, TabState>
            {
                [tabId.Value] = new TabState
                {
                    Id = tabId,
                    WorkspaceId = wsId,
                    Label = "main",
                    Ordinal = 0,
                    PaneIds = [tiled, hidden],
                    HiddenPaneIds = [hidden],
                    FocusedPaneId = hidden,
                    LayoutRoot = new LayoutSplitNode
                    {
                        Direction = LayoutNode.DirectionRight,
                        Ratio = 0.5,
                        First = LayoutTreeOperations.FromPane(tiledPane),
                        Second = LayoutTreeOperations.FromPane(hiddenPane),
                    },
                },
            },
            Panes = new Dictionary<string, PaneState>
            {
                [tiled.Value] = tiledPane,
                [hidden.Value] = hiddenPane,
            },
        };

        Assert.True((await store.SaveAsync(state)).IsOk);
        var loaded = (await store.TryLoadAsync("hidden-layout-strip")).Value!;
        var tab = loaded.Tabs[tabId.Value];
        Assert.DoesNotContain(tab.PaneIds, p => p.Value == hidden.Value);
        Assert.Contains(tab.HiddenPaneIds, p => p.Value == hidden.Value);
        Assert.True(LayoutTreeOperations.ContainsPane(tab.LayoutRoot, tiled));
        Assert.False(LayoutTreeOperations.ContainsPane(tab.LayoutRoot, hidden));
        Assert.Equal(tiled.Value, tab.FocusedPaneId?.Value);
        Assert.Equal(PanePlacement.Hidden, loaded.Panes[hidden.Value].Placement);
    }

    [Fact]
    public async Task Hidden_cross_tab_stored_layout_leaf_is_stripped_on_load()
    {
        await EnsureMigratedAsync();
        var store = new SqliteRuntimeSessionStore(_paths);

        var sessionId = SessionId.New("hidden-cross-tab-strip");
        var wsId = WorkspaceId.New();
        var tabA = TabId.New();
        var tabB = TabId.New();
        var keep = PaneId.New();
        var hidden = PaneId.New();
        var keepPane = new PaneState
        {
            Id = keep,
            TabId = tabA,
            WorkspaceId = wsId,
            Label = "shell",
            Cwd = "/tmp",
            Command = "bash",
            LifecycleState = PaneLifecycle.Running,
            Placement = PanePlacement.Tiled,
        };
        var hiddenPane = new PaneState
        {
            Id = hidden,
            TabId = tabB,
            WorkspaceId = wsId,
            Label = "agent",
            Cwd = "/tmp",
            Command = "pi",
            LifecycleState = PaneLifecycle.Running,
            Placement = PanePlacement.Hidden,
        };

        var state = new SessionState
        {
            Id = sessionId,
            Name = "hidden-cross-tab-strip",
            FocusedWorkspaceId = wsId,
            Workspaces = new Dictionary<string, WorkspaceState>
            {
                [wsId.Value] = new WorkspaceState
                {
                    Id = wsId,
                    Label = "ws",
                    Cwd = "/tmp",
                    FocusedTabId = tabA,
                    TabIds = [tabA, tabB],
                },
            },
            Tabs = new Dictionary<string, TabState>
            {
                [tabA.Value] = new TabState
                {
                    Id = tabA,
                    WorkspaceId = wsId,
                    Label = "main",
                    Ordinal = 0,
                    PaneIds = [keep, hidden],
                    HiddenPaneIds = [],
                    FocusedPaneId = hidden,
                    LayoutRoot = new LayoutSplitNode
                    {
                        Direction = LayoutNode.DirectionRight,
                        Ratio = 0.5,
                        First = LayoutTreeOperations.FromPane(keepPane),
                        Second = LayoutTreeOperations.FromPane(hiddenPane),
                    },
                },
                [tabB.Value] = new TabState
                {
                    Id = tabB,
                    WorkspaceId = wsId,
                    Label = "other",
                    Ordinal = 1,
                    PaneIds = [],
                    HiddenPaneIds = [hidden],
                    FocusedPaneId = null,
                    LayoutRoot = null,
                },
            },
            Panes = new Dictionary<string, PaneState>
            {
                [keep.Value] = keepPane,
                [hidden.Value] = hiddenPane,
            },
        };

        Assert.True((await store.SaveAsync(state)).IsOk);
        var loaded = (await store.TryLoadAsync("hidden-cross-tab-strip")).Value!;
        var loadedA = loaded.Tabs[tabA.Value];
        var loadedB = loaded.Tabs[tabB.Value];
        Assert.Contains(loadedA.PaneIds, p => p.Value == keep.Value);
        Assert.DoesNotContain(loadedA.PaneIds, p => p.Value == hidden.Value);
        Assert.DoesNotContain(loadedA.HiddenPaneIds, p => p.Value == hidden.Value);
        Assert.True(LayoutTreeOperations.ContainsPane(loadedA.LayoutRoot, keep));
        Assert.False(LayoutTreeOperations.ContainsPane(loadedA.LayoutRoot, hidden));
        Assert.Equal(keep.Value, loadedA.FocusedPaneId?.Value);
        Assert.DoesNotContain(loadedB.PaneIds, p => p.Value == hidden.Value);
        Assert.Contains(loadedB.HiddenPaneIds, p => p.Value == hidden.Value);
        Assert.False(LayoutTreeOperations.ContainsPane(loadedB.LayoutRoot, hidden));
        Assert.Equal(tabB.Value, loaded.Panes[hidden.Value].TabId.Value);
        Assert.Equal(PanePlacement.Hidden, loaded.Panes[hidden.Value].Placement);
        Assert.DoesNotContain(AppState.OccupantPaneIds(loadedA), p => p.Value == hidden.Value);
        Assert.Contains(AppState.OccupantPaneIds(loadedB), p => p.Value == hidden.Value);
    }

    [Fact]
    public async Task Restore_never_sets_IsAlive_true_without_live_probe()
    {
        await EnsureMigratedAsync();
        var store = new SqliteRuntimeSessionStore(_paths);

        var sessionId = SessionId.New("never-alive");
        var wsId = WorkspaceId.New();
        var tabId = TabId.New();
        var paneId = PaneId.New();

        var state = new SessionState
        {
            Id = sessionId,
            Name = "never-alive",
            FocusedWorkspaceId = wsId,
            Workspaces = new Dictionary<string, WorkspaceState>
            {
                [wsId.Value] = new WorkspaceState
                {
                    Id = wsId,
                    Label = "ws",
                    Cwd = "/tmp",
                    FocusedTabId = tabId,
                    TabIds = [tabId],
                },
            },
            Tabs = new Dictionary<string, TabState>
            {
                [tabId.Value] = new TabState
                {
                    Id = tabId,
                    WorkspaceId = wsId,
                    Label = "main",
                    Ordinal = 0,
                    PaneIds = [paneId],
                },
            },
            Panes = new Dictionary<string, PaneState>
            {
                [paneId.Value] = new PaneState
                {
                    Id = paneId,
                    TabId = tabId,
                    WorkspaceId = wsId,
                    Label = "p",
                    Cwd = "/tmp",
                    Command = "sleep",
                    LifecycleState = PaneLifecycle.Running,
                    Pid = 12345,
                    IsAlive = true,
                },
            },
        };

        Assert.True((await store.SaveAsync(state)).IsOk);
        var loaded = (await store.TryLoadAsync("never-alive")).Value!;
        Assert.False(loaded.Panes[paneId.Value].IsAlive);

        // Even if probe claims process is live, does not reattach → not alive.
        // Pid is retained for diagnostics / future reattach when the OS reports alive.
        var withLiveProbe = SessionGraphRestorer.ReconcileDeadPids(loaded, new FixedLivenessProbe(alive: true));
        Assert.False(withLiveProbe.Panes[paneId.Value].IsAlive);
        Assert.Equal(12345, withLiveProbe.Panes[paneId.Value].Pid);
        Assert.Equal(PaneLifecycle.Orphaned, withLiveProbe.Panes[paneId.Value].LifecycleState);

        var withDeadProbe = SessionGraphRestorer.ReconcileDeadPids(loaded, new FixedLivenessProbe(alive: false));
        Assert.False(withDeadProbe.Panes[paneId.Value].IsAlive);
        Assert.Null(withDeadProbe.Panes[paneId.Value].Pid);
        Assert.Equal(PaneLifecycle.Orphaned, withDeadProbe.Panes[paneId.Value].LifecycleState);
    }

    [Fact]
    public void Reconcile_null_pid_non_terminal_becomes_orphaned_or_exited()
    {
        var paneRunning = PaneId.New();
        var paneStarting = PaneId.New();
        var paneExited = PaneId.New();
        var wsId = WorkspaceId.New();
        var tabId = TabId.New();

        var state = new SessionState
        {
            Id = SessionId.New("null-pid"),
            Name = "null-pid",
            FocusedWorkspaceId = wsId,
            Workspaces = new Dictionary<string, WorkspaceState>
            {
                [wsId.Value] = new WorkspaceState
                {
                    Id = wsId,
                    Cwd = "/tmp",
                    FocusedTabId = tabId,
                    TabIds = [tabId],
                },
            },
            Tabs = new Dictionary<string, TabState>
            {
                [tabId.Value] = new TabState
                {
                    Id = tabId,
                    WorkspaceId = wsId,
                    Ordinal = 0,
                    PaneIds = [paneRunning, paneStarting, paneExited],
                },
            },
            Panes = new Dictionary<string, PaneState>
            {
                [paneRunning.Value] = new PaneState
                {
                    Id = paneRunning,
                    TabId = tabId,
                    WorkspaceId = wsId,
                    Label = "r",
                    Cwd = "/tmp",
                    Command = "x",
                    LifecycleState = PaneLifecycle.Running,
                    Pid = null,
                    IsAlive = true,
                },
                [paneStarting.Value] = new PaneState
                {
                    Id = paneStarting,
                    TabId = tabId,
                    WorkspaceId = wsId,
                    Label = "s",
                    Cwd = "/tmp",
                    Command = "x",
                    LifecycleState = PaneLifecycle.Starting,
                    Pid = null,
                    IsAlive = false,
                },
                [paneExited.Value] = new PaneState
                {
                    Id = paneExited,
                    TabId = tabId,
                    WorkspaceId = wsId,
                    Label = "e",
                    Cwd = "/tmp",
                    Command = "x",
                    LifecycleState = PaneLifecycle.Running,
                    Pid = null,
                    ExitCode = 3,
                    IsAlive = false,
                },
            },
        };

        var reconciled = SessionGraphRestorer.ReconcileDeadPids(state, new FixedLivenessProbe(false));
        Assert.Equal(PaneLifecycle.Orphaned, reconciled.Panes[paneRunning.Value].LifecycleState);
        Assert.False(reconciled.Panes[paneRunning.Value].IsAlive);
        Assert.Equal(AgentStatus.Done, reconciled.Panes[paneRunning.Value].AgentStatus);
        Assert.Equal(PaneLifecycle.Orphaned, reconciled.Panes[paneStarting.Value].LifecycleState);
        Assert.Equal(PaneLifecycle.Exited, reconciled.Panes[paneExited.Value].LifecycleState);
        Assert.Equal(3, reconciled.Panes[paneExited.Value].ExitCode);
    }

    [Fact]
    public void Reconcile_demotes_working_agent_status_on_orphaned_panes()
    {
        var paneId = PaneId.New();
        var wsId = WorkspaceId.New();
        var tabId = TabId.New();

        var state = new SessionState
        {
            Id = SessionId.New("agent-status"),
            Name = "agent-status",
            FocusedWorkspaceId = wsId,
            Workspaces = new Dictionary<string, WorkspaceState>
            {
                [wsId.Value] = new WorkspaceState
                {
                    Id = wsId,
                    Cwd = "/tmp",
                    FocusedTabId = tabId,
                    TabIds = [tabId],
                },
            },
            Tabs = new Dictionary<string, TabState>
            {
                [tabId.Value] = new TabState
                {
                    Id = tabId,
                    WorkspaceId = wsId,
                    Ordinal = 0,
                    PaneIds = [paneId],
                },
            },
            Panes = new Dictionary<string, PaneState>
            {
                [paneId.Value] = new PaneState
                {
                    Id = paneId,
                    TabId = tabId,
                    WorkspaceId = wsId,
                    Label = "agent",
                    Cwd = "/tmp",
                    Command = "pi",
                    LifecycleState = PaneLifecycle.Running,
                    Pid = 999_999,
                    IsAlive = true,
                    AgentStatus = AgentStatus.Working,
                },
            },
        };

        var reconciled = SessionGraphRestorer.ReconcileDeadPids(state, new FixedLivenessProbe(false));
        var pane = reconciled.Panes[paneId.Value];
        Assert.False(pane.IsAlive);
        Assert.Equal(PaneLifecycle.Orphaned, pane.LifecycleState);
        Assert.Equal(AgentStatus.Done, pane.AgentStatus);
    }

    [Fact]
    public void MarkReadyForLiveHost_promotes_stopped_to_ready()
    {
        var stopped = new SessionState
        {
            Id = SessionId.New("ready"),
            Name = "ready",
            LifecycleState = SessionLifecycle.Stopped,
        };
        var ready = SessionGraphRestorer.MarkReadyForLiveHost(stopped);
        Assert.Equal(SessionLifecycle.Ready, ready.LifecycleState);
        Assert.Equal(SessionLifecycle.Ready,
            SessionGraphRestorer.MarkReadyForLiveHost(ready).LifecycleState);

        var stopping = stopped with { LifecycleState = SessionLifecycle.Stopping };
        Assert.Equal(SessionLifecycle.Ready,
            SessionGraphRestorer.MarkReadyForLiveHost(stopping).LifecycleState);
    }

    [Fact]
    public void MarkReadyForLiveHost_preserves_frozen_read_only()
    {
        // G1: durable freeze from runtime.checkpoint.prepare must survive host restart.
        // Export/abort remain the only exits; never silent Ready wipe.
        var frozen = new SessionState
        {
            Id = SessionId.New("frozen"),
            Name = "frozen",
            LifecycleState = SessionLifecycle.FrozenReadOnly,
            Placement = "local",
            PlacementGeneration = 3,
            ReplayComplete = true,
        };
        var restored = SessionGraphRestorer.MarkReadyForLiveHost(frozen);
        Assert.Equal(SessionLifecycle.FrozenReadOnly, restored.LifecycleState);
        Assert.Equal(3, restored.PlacementGeneration);

        // Also preserve other non-shutdown states.
        Assert.Equal(SessionLifecycle.Failed,
            SessionGraphRestorer.MarkReadyForLiveHost(
                frozen with { LifecycleState = SessionLifecycle.Failed }).LifecycleState);
        Assert.Equal(SessionLifecycle.HandoffPrepared,
            SessionGraphRestorer.MarkReadyForLiveHost(
                frozen with { LifecycleState = SessionLifecycle.HandoffPrepared }).LifecycleState);
    }

    [Fact]
    public void Drop_pending_empty_workspace_keeps_valid_and_workspace_only()
    {
        var keepWs = WorkspaceId.New();
        var keepTab = TabId.New();
        var keepPane = PaneId.New();
        var pendingWs = WorkspaceId.New();
        var pendingTab = TabId.New();
        var workspaceOnly = WorkspaceId.New();
        var workspaceOnlyTab = TabId.New();

        var state = new SessionState
        {
            Id = SessionId.New("drop-pending"),
            Name = "drop-pending",
            FocusedWorkspaceId = pendingWs,
            Workspaces = new Dictionary<string, WorkspaceState>
            {
                [keepWs.Value] = new WorkspaceState
                {
                    Id = keepWs,
                    Label = "keep",
                    Ordinal = 0,
                    Cwd = "/keep",
                    FocusedTabId = keepTab,
                    TabIds = [keepTab],
                },
                [pendingWs.Value] = new WorkspaceState
                {
                    Id = pendingWs,
                    Label = "phantom",
                    Ordinal = 1,
                    Cwd = "/phantom",
                    FocusedTabId = pendingTab,
                    TabIds = [pendingTab],
                    DefaultPanePending = true,
                },
                [workspaceOnly.Value] = new WorkspaceState
                {
                    Id = workspaceOnly,
                    Label = "empty",
                    Ordinal = 2,
                    Cwd = "/empty",
                    FocusedTabId = workspaceOnlyTab,
                    TabIds = [workspaceOnlyTab],
                },
            },
            Tabs = new Dictionary<string, TabState>
            {
                [keepTab.Value] = new TabState
                {
                    Id = keepTab,
                    WorkspaceId = keepWs,
                    Label = "main",
                    PaneIds = [keepPane],
                    FocusedPaneId = keepPane,
                },
                [pendingTab.Value] = new TabState
                {
                    Id = pendingTab,
                    WorkspaceId = pendingWs,
                    Label = "main",
                },
                [workspaceOnlyTab.Value] = new TabState
                {
                    Id = workspaceOnlyTab,
                    WorkspaceId = workspaceOnly,
                    Label = "main",
                },
            },
            Panes = new Dictionary<string, PaneState>
            {
                [keepPane.Value] = new PaneState
                {
                    Id = keepPane,
                    TabId = keepTab,
                    WorkspaceId = keepWs,
                    Label = "shell",
                    Cwd = "/keep",
                    Command = "true",
                    LifecycleState = PaneLifecycle.Exited,
                    IsAlive = false,
                },
            },
        };

        var dropped = SessionGraphRestorer.DropFailedDefaultPaneOrphans(state);

        Assert.Equal(2, dropped.Workspaces.Count);
        Assert.True(dropped.Workspaces.ContainsKey(keepWs.Value));
        Assert.True(dropped.Workspaces.ContainsKey(workspaceOnly.Value));
        Assert.False(dropped.Workspaces.ContainsKey(pendingWs.Value));
        Assert.False(dropped.Tabs.ContainsKey(pendingTab.Value));
        Assert.Equal(keepWs, dropped.FocusedWorkspaceId);
        Assert.False(dropped.Workspaces[keepWs.Value].DefaultPanePending);
        Assert.False(dropped.Workspaces[workspaceOnly.Value].DefaultPanePending);
    }

    [Fact]
    public void Drop_pending_workspace_with_pane_clears_marker()
    {
        var ws = WorkspaceId.New();
        var tab = TabId.New();
        var pane = PaneId.New();
        var state = new SessionState
        {
            Id = SessionId.New("pending-pane"),
            Name = "pending-pane",
            FocusedWorkspaceId = ws,
            Workspaces = new Dictionary<string, WorkspaceState>
            {
                [ws.Value] = new WorkspaceState
                {
                    Id = ws,
                    Label = "ws",
                    Cwd = "/tmp",
                    FocusedTabId = tab,
                    TabIds = [tab],
                    DefaultPanePending = true,
                },
            },
            Tabs = new Dictionary<string, TabState>
            {
                [tab.Value] = new TabState
                {
                    Id = tab,
                    WorkspaceId = ws,
                    Label = "main",
                    PaneIds = [pane],
                    FocusedPaneId = pane,
                },
            },
            Panes = new Dictionary<string, PaneState>
            {
                [pane.Value] = new PaneState
                {
                    Id = pane,
                    TabId = tab,
                    WorkspaceId = ws,
                    Label = "shell",
                    Cwd = "/tmp",
                    Command = "true",
                    LifecycleState = PaneLifecycle.Starting,
                    IsAlive = false,
                },
            },
        };

        var kept = SessionGraphRestorer.DropFailedDefaultPaneOrphans(state);

        Assert.True(kept.Workspaces.ContainsKey(ws.Value));
        Assert.False(kept.Workspaces[ws.Value].DefaultPanePending);
        Assert.Equal(ws, kept.FocusedWorkspaceId);
    }

    [Fact]
    public async Task Restore_preserves_frozen_read_only_with_prepared_checkpoint_row()
    {
        // Crash mid-export: session durable freeze + prepared row must stay consistent.
        await EnsureMigratedAsync();
        await using var store = new SqliteRuntimeSessionStore(_paths);

        var sessionId = SessionId.New("freeze-restore");
        var frozen = new SessionState
        {
            Id = sessionId,
            Name = "freeze-restore",
            LifecycleState = SessionLifecycle.FrozenReadOnly,
            Placement = "local",
            PlacementGeneration = 1,
            ReplayComplete = true,
        };
        Assert.True((await store.SaveAsync(frozen)).IsOk);

        // Simulate AgentServer StartAsync restore path.
        var load = await store.TryLoadAsync("freeze-restore");
        Assert.True(load.IsOk);
        Assert.NotNull(load.Value);
        Assert.Equal(SessionLifecycle.FrozenReadOnly, load.Value!.LifecycleState);

        var restored = SessionGraphRestorer.ReconcileDeadPids(load.Value, new FixedLivenessProbe(false));
        restored = SessionGraphRestorer.MarkReadyForLiveHost(restored);
        Assert.Equal(SessionLifecycle.FrozenReadOnly, restored.LifecycleState);

        // Prepared checkpoint row remains valid under preserved freeze (export/abort only exits).
        var cpPaths = _paths;
        var cpStore = new FileCheckpointStore(cpPaths);
        var prepared = new CheckpointRecord
        {
            CheckpointId = "cp_freeze_restore",
            SessionId = sessionId.Value,
            State = CheckpointStates.Prepared,
            BarrierSeq = 0,
            NextSeqAtPrepare = 1,
            SessionFingerprint = SessionGraphFingerprint.Compute(restored),
            CreatedAt = DateTimeOffset.UtcNow,
            IncludeWorkspaceFiles = true,
            ProjectRootDevice = 1,
            ProjectRootInode = 2,
            ProjectRootWasSymlink = false,
        };
        Assert.True((await cpStore.SaveAsync(prepared)).IsOk);
        var loadedCp = await cpStore.GetAsync(prepared.CheckpointId);
        Assert.True(loadedCp.IsOk);
        Assert.Equal(CheckpointStates.Prepared, loadedCp.Value!.State);
        Assert.True(loadedCp.Value.HasProjectRootPin);
        Assert.Equal(1ul, loadedCp.Value.ProjectRootDevice);
        Assert.Equal(2ul, loadedCp.Value.ProjectRootInode);
        Assert.False(loadedCp.Value.ProjectRootWasSymlink);

        var sidecar = Path.Combine(cpStore.GetCheckpointDirectory(prepared.CheckpointId), "record.json");
        if (File.Exists(sidecar))
            File.Delete(sidecar);
        var fromIndex = await cpStore.GetAsync(prepared.CheckpointId);
        Assert.True(fromIndex.IsOk);
        Assert.True(fromIndex.Value!.HasProjectRootPin);
        Assert.Equal(1ul, fromIndex.Value.ProjectRootDevice);
        Assert.Equal(2ul, fromIndex.Value.ProjectRootInode);
        Assert.Equal(SessionLifecycle.FrozenReadOnly, restored.LifecycleState);
    }

    [Fact(Timeout = 20000)]
    public async Task Graceful_shutdown_after_prepare_stays_frozen_and_export_still_works()
    {
        await EnsureMigratedAsync();
        var sessionName = "freeze-shutdown";
        var app = new AppState(SessionId.New(sessionName));
        app.UpdateSession(s => s with
        {
            Name = sessionName,
            LifecycleState = SessionLifecycle.Ready,
            Binding = new AtomicBinding { ProjectRoot = _dir, RunId = "run_sd" },
        });

        var store = new SqliteRuntimeSessionStore(_paths);
        Assert.True((await store.SaveAsync(app.Snapshot())).IsOk);

        var manifests = new SqliteJournalManifestStore(_paths);
        var journal = new FileRuntimeEventJournal(_paths, manifests, app.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);

        var cpStore = new FileCheckpointStore(_paths);
        var checkpoints = new DefaultCheckpointService(
            cpStore, new DefaultCheckpointArtifactBuilder(new CompleteGitWorkspaceProbe()));
        var intel = new PaneIntelligencePipeline();
        var cp = new ControlPlaneService(
            app,
            TestPaneFactories.Create(intel),
            intel,
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: new EventSubscriptionHub(),
            redactor: new DefaultEventPayloadRedactor(),
            checkpoints: checkpoints);

        var prep = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
            CancellationToken.None);
        var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;
        Assert.Equal(SessionLifecycle.FrozenReadOnly, app.Snapshot().LifecycleState);

        await cp.ShutdownAsync(CancellationToken.None);
        await journal.DisposeAsync();

        Assert.Equal(SessionLifecycle.FrozenReadOnly, app.Snapshot().LifecycleState);

        var load = await store.TryLoadAsync(sessionName);
        Assert.True(load.IsOk);
        Assert.NotNull(load.Value);
        Assert.Equal(SessionLifecycle.FrozenReadOnly, load.Value!.LifecycleState);

        var restored = SessionGraphRestorer.ReconcileDeadPids(load.Value, new FixedLivenessProbe(false));
        restored = SessionGraphRestorer.MarkReadyForLiveHost(restored);
        Assert.Equal(SessionLifecycle.FrozenReadOnly, restored.LifecycleState);

        var app2 = new AppState(SessionId.New(sessionName));
        app2.Replace(restored);
        var journal2 = new FileRuntimeEventJournal(_paths, manifests, app2.SessionId.Value);
        Assert.True((await journal2.RecoverAsync()).IsOk);
        var intel2 = new PaneIntelligencePipeline();
        var cp2 = new ControlPlaneService(
            app2,
            TestPaneFactories.Create(intel2),
            intel2,
            new HeuristicAgentDetector(),
            store: store,
            journal: journal2,
            subscriptions: new EventSubscriptionHub(),
            redactor: new DefaultEventPayloadRedactor(),
            checkpoints: checkpoints);

        var exp = await cp2.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointExport,
            JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
            CancellationToken.None);
        Assert.Equal(CheckpointStates.Exported, exp.GetProperty("state").GetString());
        Assert.Equal(SessionLifecycle.FrozenReadOnly, app2.Snapshot().LifecycleState);

        var durable = await cpStore.GetAsync(checkpointId);
        Assert.True(durable.IsOk);
        Assert.Equal(CheckpointStates.Exported, durable.Value!.State);

        await journal2.DisposeAsync();
    }

    [Fact]
    public async Task Persist_then_kill_then_restore_metadata()
    {
        await EnsureMigratedAsync();
        var sessionId = SessionId.New("persist-restore");
        var wsId = WorkspaceId.New();
        var tabId = TabId.New();
        var paneId = PaneId.New();

        var state = new SessionState
        {
            Id = sessionId,
            Name = "persist-restore",
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
            PlacementGeneration = 0,
            ReplayComplete = true,
            FocusedWorkspaceId = wsId,
            Workspaces = new Dictionary<string, WorkspaceState>
            {
                [wsId.Value] = new WorkspaceState
                {
                    Id = wsId,
                    Label = "default",
                    Cwd = "/work",
                    FocusedTabId = tabId,
                    TabIds = [tabId],
                },
            },
            Tabs = new Dictionary<string, TabState>
            {
                [tabId.Value] = new TabState
                {
                    Id = tabId,
                    WorkspaceId = wsId,
                    Label = "main",
                    Ordinal = 0,
                    PaneIds = [paneId],
                },
            },
            Panes = new Dictionary<string, PaneState>
            {
                [paneId.Value] = new PaneState
                {
                    Id = paneId,
                    TabId = tabId,
                    WorkspaceId = wsId,
                    Label = "shell",
                    Cwd = "/work",
                    Command = "bash",
                    LifecycleState = PaneLifecycle.Running,
                    Pid = 55555,
                    IsAlive = true,
                    AgentStatus = AgentStatus.Working,
                },
            },
        };

        await using (var store = new SqliteRuntimeSessionStore(_paths))
        {
            Assert.True((await store.SaveAsync(state)).IsOk);
        }

        SqliteTestCleanup.ReleaseDatabase(_paths.DatabasePath);

        // Simulate restart: new AppState + store + reconcile.
        var app = new AppState(SessionId.New("persist-restore"));
        await using var store2 = new SqliteRuntimeSessionStore(_paths);
        var loaded = (await store2.TryLoadAsync("persist-restore")).Value;
        Assert.NotNull(loaded);
        var reconciled = SessionGraphRestorer.ReconcileDeadPids(loaded!, new FixedLivenessProbe(false));
        app.Replace(reconciled);

        var snap = app.Snapshot();
        Assert.Equal(wsId.Value, snap.Workspaces.Keys.Single());
        Assert.Equal("default", snap.Workspaces[wsId.Value].Label);
        Assert.Equal(paneId.Value, snap.Panes.Keys.Single());
        Assert.Equal("shell", snap.Panes[paneId.Value].Label);
        Assert.False(snap.Panes[paneId.Value].IsAlive);
        Assert.Equal(PaneLifecycle.Orphaned, snap.Panes[paneId.Value].LifecycleState);
        Assert.Null(snap.Panes[paneId.Value].Pid);
    }

    [SkippableFact]
    public async Task Persist_then_kill_then_restore_starts_a_live_pane_for_every_workspace()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        await EnsureMigratedAsync();
        var cwd1 = Path.Combine(_dir, "ws-a");
        var cwd2 = Path.Combine(_dir, "ws-b");
        Directory.CreateDirectory(cwd1);
        Directory.CreateDirectory(cwd2);

        var sessionName = "restore-spawn";
        var attach = RestoreShellAttach;
        var app = new AppState(SessionId.New(sessionName));
        app.UpdateSession(s => s with
        {
            Name = sessionName,
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
            ReplayComplete = true,
        });
        var wsA = app.CreateWorkspace(cwd1, "alpha");
        var wsB = app.CreateWorkspace(cwd2, "beta");
        var paneA = app.RegisterPane(new PaneState
        {
            Id = PaneId.New(),
            TabId = wsA.FocusedTabId!.Value,
            WorkspaceId = wsA.Id,
            Label = "shell-a",
            Cwd = cwd1,
            Command = "pi",
            Args = ["--resume", "alpha"],
            LifecycleState = PaneLifecycle.Running,
            Pid = 111,
            IsAlive = true,
            OccupantGeneration = 3,
            AgentKind = "pi",
            AgentSession = new NativeAgentSessionRef
            {
                Kind = NativeAgentSessionRef.KindId,
                Value = "sess-alpha",
                Source = "pane",
                Agent = "pi",
            },
        });
        var paneB = app.RegisterPane(new PaneState
        {
            Id = PaneId.New(),
            TabId = wsB.FocusedTabId!.Value,
            WorkspaceId = wsB.Id,
            Label = "shell-b",
            Cwd = cwd2,
            Command = "/bin/bash",
            Args = [],
            LifecycleState = PaneLifecycle.Running,
            Pid = 222,
            IsAlive = true,
            OccupantGeneration = 1,
        });
        var snap1 = app.Snapshot();
        var layoutA = snap1.Tabs[paneA.TabId.Value].LayoutRoot!.ToCanonicalJson(includePaneId: true);
        var layoutB = snap1.Tabs[paneB.TabId.Value].LayoutRoot!.ToCanonicalJson(includePaneId: true);

        await using (var store = new SqliteRuntimeSessionStore(_paths))
        {
            Assert.True((await store.SaveAsync(snap1)).IsOk);
        }

        SqliteTestCleanup.ReleaseDatabase(_paths.DatabasePath);

        var sockDir = Path.Combine("/tmp", "hypa-rs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sockDir);
#pragma warning disable CA1416
        File.SetUnixFileMode(
            sockDir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        var sock = Path.Combine(sockDir, "s.sock");
        try
        {
            var capturing1 = TestPaneFactories.Capturing();
            var host1 = await StartHostedRestoreAsync(sessionName, capturing1, attach, sock);
            try
            {
                AssertRestoredLivePane(host1.App, paneA, wsA.Id, paneA.TabId, cwd1, "pi", ["--resume", "alpha"], layoutA);
                AssertRestoredLivePane(host1.App, paneB, wsB.Id, paneB.TabId, cwd2, "/bin/bash", [], layoutB);
                Assert.Equal("sess-alpha", host1.App.GetPane(paneA.Id)!.AgentSession?.Value);
                Assert.Null(host1.App.GetPane(paneA.Id)!.AgentKind);
            }
            finally
            {
                await host1.DisposeAsync();
            }

            SqliteTestCleanup.ReleaseDatabase(_paths.DatabasePath);

            var capturing2 = TestPaneFactories.Capturing();
            var host2 = await StartHostedRestoreAsync(sessionName, capturing2, attach, sock);
            try
            {
                AssertRestoredLivePane(host2.App, paneA, wsA.Id, paneA.TabId, cwd1, "pi", ["--resume", "alpha"], layoutA);
                AssertRestoredLivePane(host2.App, paneB, wsB.Id, paneB.TabId, cwd2, "/bin/bash", [], layoutB);
                Assert.Equal(2, capturing2.Options.Count);
                Assert.All(capturing2.Options, o =>
                {
                    Assert.Equal("/bin/sh", o.Command);
                    Assert.Empty(o.Args);
                });
                Assert.Contains(capturing2.Options, o => o.Id == paneA.Id && o.Cwd == cwd1);
                Assert.Contains(capturing2.Options, o => o.Id == paneB.Id && o.Cwd == cwd2);
                Assert.Equal("sess-alpha", host2.App.GetPane(paneA.Id)!.AgentSession?.Value);
                Assert.Null(host2.App.GetPane(paneA.Id)!.AgentKind);
                Assert.Equal(3, host2.App.GetPane(paneA.Id)!.OccupantGeneration);

                var durable = (await host2.Store.TryLoadAsync(sessionName)).Value;
                Assert.NotNull(durable);
                Assert.Equal(PaneLifecycle.Running, durable!.Panes[paneA.Id.Value].LifecycleState);
                Assert.Equal(PaneLifecycle.Running, durable.Panes[paneB.Id.Value].LifecycleState);
                Assert.NotNull(durable.Panes[paneA.Id.Value].Pid);
                Assert.NotNull(durable.Panes[paneB.Id.Value].Pid);
                Assert.Equal(cwd1, durable.Panes[paneA.Id.Value].Cwd);
                Assert.Equal(cwd2, durable.Panes[paneB.Id.Value].Cwd);
                Assert.Equal("pi", durable.Panes[paneA.Id.Value].Command);
                Assert.Equal(["--resume", "alpha"], durable.Panes[paneA.Id.Value].Args);
                Assert.Equal("/bin/bash", durable.Panes[paneB.Id.Value].Command);
                Assert.Equal("sess-alpha", durable.Panes[paneA.Id.Value].AgentSession?.Value);
                Assert.Null(durable.Panes[paneA.Id.Value].AgentKind);
            }
            finally
            {
                await host2.DisposeAsync();
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(sockDir))
                    Directory.Delete(sockDir, recursive: true);
            }
            catch
            {
                // teardown
            }
        }
    }

    [Fact]
    public async Task Restore_spawn_keeps_pane_id_tab_workspace_layout_command_args_and_cwd()
    {
        var cwd = Path.Combine(_dir, "keep-cwd");
        Directory.CreateDirectory(cwd);
        var app = new AppState(SessionId.New("restore-keep"));
        app.UpdateSession(s => s with
        {
            Name = "restore-keep",
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var ws = app.CreateWorkspace(cwd, "keep");
        var pane = app.RegisterPane(new PaneState
        {
            Id = PaneId.New(),
            TabId = ws.FocusedTabId!.Value,
            WorkspaceId = ws.Id,
            Label = "kept",
            Cwd = cwd,
            Command = "/usr/bin/env",
            Args = ["-i"],
            Cols = 80,
            Rows = 24,
            LifecycleState = PaneLifecycle.Orphaned,
            IsAlive = false,
            OccupantGeneration = 1,
            AgentKind = "grok",
        });
        var layout = app.Snapshot().Tabs[pane.TabId.Value].LayoutRoot!.ToCanonicalJson(includePaneId: true);
        var capturing = TestPaneFactories.Capturing();
        var intel = new PaneIntelligencePipeline();
        var cp = new ControlPlaneService(
            app,
            capturing,
            intel,
            new HeuristicAgentDetector(),
            attachConfig: RestoreShellAttach);
        try
        {
            await cp.RestoreSpawnAsync(CancellationToken.None);
            Assert.NotNull(capturing.LastOptions);
            Assert.Equal(pane.Id, capturing.LastOptions!.Id);
            Assert.Equal(cwd, capturing.LastOptions.Cwd);
            Assert.Equal("/bin/sh", capturing.LastOptions.Command);
            Assert.Empty(capturing.LastOptions.Args);
            Assert.Equal(80, capturing.LastOptions.Cols);
            Assert.Equal(24, capturing.LastOptions.Rows);
            AssertRestoredLivePane(app, pane, ws.Id, pane.TabId, cwd, "/usr/bin/env", ["-i"], layout);
            Assert.Null(app.GetPane(pane.Id)!.AgentKind);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private static readonly AttachClientConfig RestoreShellAttach = AttachClientConfig.Default with
    {
        Terminal = AttachTerminalConfig.Default with
        {
            DefaultShell = "/bin/sh",
            ShellMode = TerminalShellMode.NonLogin,
        },
    };

    private async Task<HostedRestore> StartHostedRestoreAsync(
        string sessionName,
        IPaneRuntimeFactory factory,
        AttachClientConfig attach,
        string socketPath)
    {
        var app = new AppState(SessionId.New(sessionName));
        var store = new SqliteRuntimeSessionStore(_paths);
        var manifests = new SqliteJournalManifestStore(_paths);
        var journal = new FileRuntimeEventJournal(_paths, manifests, app.SessionId.Value);
        var intel = new PaneIntelligencePipeline();
        var cp = new ControlPlaneService(
            app,
            factory,
            intel,
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            attachConfig: attach);
        var socket = new UnixSocketServer(cp, socketPath);
        var hosted = new AgentRuntimeHostedService(
            socket,
            app,
            cp,
            new SqliteRuntimeSchemaMigrator(_paths),
            store,
            journal,
            new FixedLivenessProbe(false),
            new RuntimeOptions(sessionName, _dir, socketPath, _dir),
            attach,
            NullLogger<AgentRuntimeHostedService>.Instance);
        await hosted.StartAsync(CancellationToken.None);
        return new HostedRestore(hosted, app, store, journal);
    }

    private sealed class HostedRestore(
        AgentRuntimeHostedService hosted,
        AppState app,
        SqliteRuntimeSessionStore store,
        FileRuntimeEventJournal journal) : IAsyncDisposable
    {
        public AppState App { get; } = app;
        public SqliteRuntimeSessionStore Store { get; } = store;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await hosted.StopAsync(CancellationToken.None);
            }
            catch
            {
                // teardown
            }

            try
            {
                await journal.DisposeAsync();
            }
            catch
            {
                // teardown
            }

            try
            {
                await Store.DisposeAsync();
            }
            catch
            {
                // teardown
            }
        }
    }

    private static void AssertRestoredLivePane(
        AppState app,
        PaneState original,
        WorkspaceId workspaceId,
        TabId tabId,
        string cwd,
        string command,
        IReadOnlyList<string> args,
        string layoutJson)
    {
        var pane = app.GetPane(original.Id);
        Assert.NotNull(pane);
        Assert.Equal(original.Id, pane!.Id);
        Assert.Equal(tabId, pane.TabId);
        Assert.Equal(workspaceId, pane.WorkspaceId);
        Assert.Equal(cwd, pane.Cwd);
        Assert.Equal(command, pane.Command);
        Assert.Equal(args, pane.Args);
        Assert.True(pane.IsAlive);
        Assert.Equal(PaneLifecycle.Running, pane.LifecycleState);
        Assert.True(pane.OccupantGeneration > 0);
        var tab = app.GetTab(tabId);
        Assert.NotNull(tab);
        Assert.Equal(layoutJson, tab!.LayoutRoot!.ToCanonicalJson(includePaneId: true));
        Assert.Contains(original.Id, tab.PaneIds);
    }

    [Fact]
    public async Task Hosted_start_without_existing_db_creates_default_workspace_and_persists()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        var store = new SqliteRuntimeSessionStore(_paths);
        var app = new AppState(SessionId.New("fresh"));
        var load = await store.TryLoadAsync("fresh");
        Assert.True(load.IsOk);
        Assert.Null(load.Value);

        app.CreateWorkspace("/cwd", label: "default");
        app.UpdateSession(s => s with
        {
            Name = "fresh",
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
            PlacementGeneration = 0,
            ReplayComplete = true,
        });
        Assert.True((await store.SaveAsync(app.Snapshot())).IsOk);

        var reloaded = (await store.TryLoadAsync("fresh")).Value;
        Assert.NotNull(reloaded);
        Assert.Single(reloaded!.Workspaces);
        Assert.Equal("default", reloaded.Workspaces.Values.Single().Label);
        Assert.Equal("/cwd", reloaded.Workspaces.Values.Single().Cwd);
        Assert.Equal(SessionLifecycle.Ready, reloaded.LifecycleState);
        Assert.True(reloaded.ReplayComplete);
    }

    [Fact]
    public async Task Graceful_shutdown_persists_exited_panes_and_does_not_wipe_workspaces()
    {
        await EnsureMigratedAsync();
        var store = new SqliteRuntimeSessionStore(_paths);
        var app = new AppState(SessionId.New("shutdown"));
        app.UpdateSession(s => s with { Name = "shutdown" });
        var ws = app.CreateWorkspace("/tmp/sd", "ws");
        var pane = app.RegisterPane(new PaneState
        {
            Id = PaneId.New(),
            TabId = ws.FocusedTabId!.Value,
            WorkspaceId = ws.Id,
            Label = "p",
            Cwd = "/tmp/sd",
            Command = "sleep 60",
            LifecycleState = PaneLifecycle.Running,
            Pid = 88888,
            IsAlive = true,
            AgentStatus = AgentStatus.Working,
        });
        Assert.True((await store.SaveAsync(app.Snapshot())).IsOk);

        var intel = new PaneIntelligencePipeline();
        var cp = new ControlPlaneService(
            app,
            TestPaneFactories.Create(intel),
            intel,
            new HeuristicAgentDetector(),
            store: store);

        await cp.ShutdownAsync(CancellationToken.None);

        var after = app.Snapshot();
        Assert.Contains(ws.Id.Value, after.Workspaces.Keys);
        Assert.Contains(pane.Id.Value, after.Panes.Keys);
        Assert.False(after.Panes[pane.Id.Value].IsAlive);
        // Metadata-only pane (no runtime admitted) becomes orphaned on shutdown.
        Assert.Equal(PaneLifecycle.Orphaned, after.Panes[pane.Id.Value].LifecycleState);
        Assert.Equal(SessionLifecycle.Stopped, after.LifecycleState);

        var reloaded = (await store.TryLoadAsync("shutdown")).Value;
        Assert.NotNull(reloaded);
        Assert.Contains(ws.Id.Value, reloaded!.Workspaces.Keys);
        Assert.Contains(pane.Id.Value, reloaded.Panes.Keys);
        Assert.False(reloaded.Panes[pane.Id.Value].IsAlive);
    }

    [Fact]
    public async Task Restart_after_graceful_shutdown_sets_session_state_ready_and_keeps_graph()
    {
        await EnsureMigratedAsync();
        await using var store = new SqliteRuntimeSessionStore(_paths);
        var app = new AppState(SessionId.New("restart-ready"));
        app.UpdateSession(s => s with { Name = "restart-ready" });
        var ws = app.CreateWorkspace("/tmp/rr", "ws");
        var pane = app.RegisterPane(new PaneState
        {
            Id = PaneId.New(),
            TabId = ws.FocusedTabId!.Value,
            WorkspaceId = ws.Id,
            Label = "shell",
            Cwd = "/tmp/rr",
            Command = "bash",
            LifecycleState = PaneLifecycle.Running,
            Pid = 424242,
            IsAlive = true,
            AgentStatus = AgentStatus.Working,
        });
        Assert.True((await store.SaveAsync(app.Snapshot())).IsOk);

        var intel = new PaneIntelligencePipeline();
        var cp = new ControlPlaneService(
            app,
            TestPaneFactories.Create(intel),
            intel,
            new HeuristicAgentDetector(),
            store: store);
        await cp.ShutdownAsync(CancellationToken.None);
        Assert.Equal(SessionLifecycle.Stopped, app.Snapshot().LifecycleState);

        // Simulate host StartAsync restore path on a fresh AppState.
        var load = await store.TryLoadAsync("restart-ready");
        Assert.True(load.IsOk);
        Assert.NotNull(load.Value);
        Assert.Equal(SessionLifecycle.Stopped, load.Value!.LifecycleState);

        var restored = SessionGraphRestorer.ReconcileDeadPids(load.Value, new FixedLivenessProbe(false));
        restored = SessionGraphRestorer.MarkReadyForLiveHost(restored);
        var app2 = new AppState(SessionId.New("restart-ready"));
        app2.Replace(restored);
        Assert.True((await store.SaveAsync(app2.Snapshot())).IsOk);

        var snap = app2.Snapshot();
        Assert.Equal(SessionLifecycle.Ready, snap.LifecycleState);
        Assert.Contains(ws.Id.Value, snap.Workspaces.Keys);
        Assert.Contains(pane.Id.Value, snap.Panes.Keys);
        Assert.Equal(PaneLifecycle.Orphaned, snap.Panes[pane.Id.Value].LifecycleState);

        var reloaded = (await store.TryLoadAsync("restart-ready")).Value;
        Assert.NotNull(reloaded);
        Assert.Equal(SessionLifecycle.Ready, reloaded!.LifecycleState);
        Assert.Contains(ws.Id.Value, reloaded.Workspaces.Keys);
        Assert.Contains(pane.Id.Value, reloaded.Panes.Keys);
    }

    [Fact]
    public async Task Focused_workspace_id_round_trips_across_restart()
    {
        await EnsureMigratedAsync();
        await using var store = new SqliteRuntimeSessionStore(_paths);

        // Lexicographically larger id first so non-deterministic "first row" would flip focus.
        var wsA = new WorkspaceId("ws_zzzz");
        var wsB = new WorkspaceId("ws_aaaa");
        var tabA = TabId.New();
        var tabB = TabId.New();
        var sessionId = SessionId.New("focus");

        var state = new SessionState
        {
            Id = sessionId,
            Name = "focus",
            LifecycleState = SessionLifecycle.Ready,
            FocusedWorkspaceId = wsA, // not the lexicographically first workspace
            Workspaces = new Dictionary<string, WorkspaceState>
            {
                [wsA.Value] = new WorkspaceState
                {
                    Id = wsA,
                    Label = "second",
                    Cwd = "/a",
                    FocusedTabId = tabA,
                    TabIds = [tabA],
                },
                [wsB.Value] = new WorkspaceState
                {
                    Id = wsB,
                    Label = "first",
                    Cwd = "/b",
                    FocusedTabId = tabB,
                    TabIds = [tabB],
                },
            },
            Tabs = new Dictionary<string, TabState>
            {
                [tabA.Value] = new TabState
                {
                    Id = tabA,
                    WorkspaceId = wsA,
                    Ordinal = 0,
                    PaneIds = [],
                },
                [tabB.Value] = new TabState
                {
                    Id = tabB,
                    WorkspaceId = wsB,
                    Ordinal = 0,
                    PaneIds = [],
                },
            },
            Panes = new Dictionary<string, PaneState>(),
        };

        Assert.True((await store.SaveAsync(state)).IsOk);
        var loaded = (await store.TryLoadAsync("focus")).Value;
        Assert.NotNull(loaded);
        Assert.Equal(wsA.Value, loaded!.FocusedWorkspaceId?.Value);
        Assert.Equal(2, loaded.Workspaces.Count);
    }

    [Fact]
    public async Task Best_effort_exit_persist_and_durable_create_keep_latest_graph()
    {
        await EnsureMigratedAsync();
        await using var store = new SqliteRuntimeSessionStore(_paths);
        var app = new AppState(SessionId.New("race"));
        app.UpdateSession(s => s with { Name = "race", LifecycleState = SessionLifecycle.Ready });
        app.CreateWorkspace("/tmp/race", "ws");

        var intel = new PaneIntelligencePipeline();
        // Factory produces runtimes that start then exit, firing OnRuntimeExited best-effort
        // PersistGraphAsync (requireDurable:false single-flight + dirty-bit path).
        var factory = new ImmediateExitPaneFactory();
        var cp = new ControlPlaneService(
            app,
            factory,
            intel,
            new HeuristicAgentDetector(),
            store: store);

        var tasks = new List<Task>();
        // Concurrent best-effort exit persists via pane.create → Start → Exited.
        for (var i = 0; i < 12; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                var createPane = JsonDocument.Parse(
                    """{"command":"true","label":"exit-racer","create_pane":true}""")
                    .RootElement.Clone();
                // pane.create through workspace path is durable after start; exit also fires
                // best-effort PersistGraphAsync. Interleave with pure pane.create.
                try
                {
                    await cp.DispatchAsync("pane.create", createPane, CancellationToken.None);
                }
                catch (ControlPlaneException)
                {
                    // Shutdown or transient — ignore under race pressure.
                }
            }));
        }

        // Concurrent durable workspace.create under the same PersistGraph gate.
        for (var i = 0; i < 10; i++)
        {
            var label = $"extra-{i}";
            tasks.Add(Task.Run(async () =>
            {
                var createParams = JsonDocument.Parse(
                    $$"""{"cwd":"/tmp/race-extra","label":"{{label}}","create_pane":false}""")
                    .RootElement.Clone();
                await cp.DispatchAsync("workspace.create", createParams, CancellationToken.None);
            }));
        }

        await Task.WhenAll(tasks);

        // Drain best-effort exit persists: ImmediateExitRuntime fires Exited after StartAsync.
        // Under full-suite load, concurrent exit
        // persists can lag several seconds before every pane is terminal in-memory.
        for (var i = 0; i < 400; i++)
        {
            var snap = app.Snapshot();
            if (snap.Panes.Values.All(p => !p.IsAlive &&
                    p.LifecycleState is PaneLifecycle.Exited or PaneLifecycle.Orphaned
                        or PaneLifecycle.Closed))
                break;
            await Task.Delay(20);
        }

        // Final durable write after all races settle.
        app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var createFinal = JsonDocument.Parse(
            """{"cwd":"/tmp/race-final","label":"final","create_pane":false}""").RootElement.Clone();
        await cp.DispatchAsync("workspace.create", createFinal, CancellationToken.None);

        // Brief settle for any last best-effort dirty re-entry after durable write.
        await Task.Delay(50);

        var loaded = (await store.TryLoadAsync("race")).Value;
        Assert.NotNull(loaded);
        Assert.Equal(SessionLifecycle.Ready, loaded!.LifecycleState);
        // Durable workspace.create under concurrent best-effort exit persists must land.
        Assert.Contains(loaded.Workspaces.Values, w => w.Label == "final");
        // Every pane that survived must be terminal (exited) after immediate-exit factory.
        foreach (var pane in loaded.Panes.Values)
        {
            Assert.False(pane.IsAlive);
            Assert.True(
                pane.LifecycleState is PaneLifecycle.Exited or PaneLifecycle.Orphaned
                    or PaneLifecycle.Closed,
                $"unexpected lifecycle {pane.LifecycleState} for {pane.Id.Value}");
        }
    }

    [Fact]
    public async Task Fail_spawn_cleanup_removes_durable_starting_pane()
    {
        await EnsureMigratedAsync();
        await using var store = new SqliteRuntimeSessionStore(_paths);
        var app = new AppState(SessionId.New("zombie"));
        app.UpdateSession(s => s with { Name = "zombie", LifecycleState = SessionLifecycle.Ready });
        app.CreateWorkspace("/tmp/zombie", "ws");
        Assert.True((await store.SaveAsync(app.Snapshot())).IsOk);

        var intel = new PaneIntelligencePipeline();
        // StartAsync first SaveAsyncs the in-memory starting pane (simulating concurrent
        // best-effort PersistGraphAsync), then throws so FailSpawnCleanupAsync runs.
        var factory = new FailAfterPersistingFactory(app, store);
        var cp = new ControlPlaneService(
            app,
            factory,
            intel,
            new HeuristicAgentDetector(),
            store: store);

        var createParams = JsonDocument.Parse(
            """{"command":"does-not-matter","label":"phantom"}""").RootElement.Clone();
        await Assert.ThrowsAsync<ControlPlaneException>(async () =>
            await cp.DispatchAsync("pane.create", createParams, CancellationToken.None));

        // In-memory graph has no phantom pane.
        Assert.Empty(app.ListPanes());
        Assert.NotNull(factory.LastPaneId);

        // Durable graph must not retain the starting pane (zombie / phantom pane fix).
        var loaded = (await store.TryLoadAsync("zombie")).Value;
        Assert.NotNull(loaded);
        Assert.Empty(loaded!.Panes);
        Assert.False(loaded.Panes.ContainsKey(factory.LastPaneId.Value.Value));
    }

    [Fact]
    public async Task Pane_close_uses_removeMissingPanes_and_drops_durable_row()
    {
        await EnsureMigratedAsync();
        await using var store = new SqliteRuntimeSessionStore(_paths);
        var app = new AppState(SessionId.New("close"));
        app.UpdateSession(s => s with { Name = "close", LifecycleState = SessionLifecycle.Ready });
        var ws = app.CreateWorkspace("/tmp/close", "ws");
        var pane = app.RegisterPane(new PaneState
        {
            Id = PaneId.New(),
            TabId = ws.FocusedTabId!.Value,
            WorkspaceId = ws.Id,
            Label = "gone",
            Cwd = "/tmp/close",
            Command = "true",
            LifecycleState = PaneLifecycle.Exited,
            IsAlive = false,
        });
        Assert.True((await store.SaveAsync(app.Snapshot())).IsOk);

        var intel = new PaneIntelligencePipeline();
        var cp = new ControlPlaneService(
            app,
            TestPaneFactories.Create(intel),
            intel,
            new HeuristicAgentDetector(),
            store: store);

        var closeParams = JsonDocument.Parse(
            $$"""{"pane_id":"{{pane.Id.Value}}"}""").RootElement.Clone();
        await cp.DispatchAsync("pane.close", closeParams, CancellationToken.None);

        var loaded = (await store.TryLoadAsync("close")).Value;
        Assert.NotNull(loaded);
        Assert.False(loaded!.Panes.ContainsKey(pane.Id.Value));
    }

    [Fact]
    public async Task Workspace_close_uses_removeMissing_and_drops_durable_row()
    {
        await EnsureMigratedAsync();
        await using var store = new SqliteRuntimeSessionStore(_paths);
        var app = new AppState(SessionId.New("ws-close"));
        app.UpdateSession(s => s with { Name = "ws-close", LifecycleState = SessionLifecycle.Ready });
        var intel = new PaneIntelligencePipeline();
        var cp = new ControlPlaneService(
            app,
            TestPaneFactories.Stub(),
            intel,
            new HeuristicAgentDetector(),
            store: store);
        try
        {
            var first = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                JsonDocument.Parse("""{"cwd":"/tmp/ws-close-a","create_pane":false,"label":"a"}""").RootElement,
                CancellationToken.None);
            var second = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                JsonDocument.Parse("""{"cwd":"/tmp/ws-close-b","create_pane":false,"label":"b"}""").RootElement,
                CancellationToken.None);
            var firstId = first.GetProperty("workspace_id").GetString()!;
            var secondId = second.GetProperty("workspace_id").GetString()!;
            var secondTab = second.GetProperty("focused_tab_id").GetString()!;

            await cp.DispatchAsync(
                ProtocolMethods.WorkspaceClose,
                JsonDocument.Parse($$"""{"workspace_id":"{{secondId}}"}""").RootElement,
                CancellationToken.None);

            var loaded = (await store.TryLoadAsync("ws-close")).Value;
            Assert.NotNull(loaded);
            Assert.True(loaded!.Workspaces.ContainsKey(firstId));
            Assert.False(loaded.Workspaces.ContainsKey(secondId));
            Assert.False(loaded.Tabs.ContainsKey(secondTab));
            Assert.Equal(firstId, loaded.FocusedWorkspaceId?.Value);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Failed_default_pane_workspace_create_rolls_back_durable_row()
    {
        await EnsureMigratedAsync();
        await using var store = new SqliteRuntimeSessionStore(_paths);
        var app = new AppState(SessionId.New("create-fail"));
        app.UpdateSession(s => s with { Name = "create-fail", LifecycleState = SessionLifecycle.Ready });
        var intel = new PaneIntelligencePipeline();
        var factory = new FailAfterPersistingFactory(app, store);
        var cp = new ControlPlaneService(
            app,
            factory,
            intel,
            new HeuristicAgentDetector(),
            store: store);
        try
        {
            await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                JsonDocument.Parse("""{"cwd":"/tmp/create-keep","create_pane":false,"label":"keep"}""").RootElement,
                CancellationToken.None);

            var ex = await Assert.ThrowsAsync<ControlPlaneException>(async () =>
                await cp.DispatchAsync(
                    ProtocolMethods.WorkspaceCreate,
                    JsonDocument.Parse(
                        """{"cwd":"/tmp/create-fail","command":"does-not-matter","label":"phantom","create_pane":true}""")
                        .RootElement,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.PaneStartFailed, ex.Code);

            var remaining = Assert.Single(app.ListWorkspaces());
            Assert.Equal("keep", remaining.Label);
            Assert.DoesNotContain(app.ListWorkspaces(), w => w.Label == "phantom");
            Assert.Empty(app.ListPanes());

            var loaded = (await store.TryLoadAsync("create-fail")).Value;
            Assert.NotNull(loaded);
            Assert.Single(loaded!.Workspaces);
            Assert.Contains(loaded.Workspaces.Values, w => w.Label == "keep");
            Assert.DoesNotContain(loaded.Workspaces.Values, w => w.Label == "phantom");
            Assert.Empty(loaded.Panes);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Last_workspace_failed_default_pane_create_replaces_durable_row()
    {
        await EnsureMigratedAsync();
        await using var store = new SqliteRuntimeSessionStore(_paths);
        var app = new AppState(SessionId.New("last-create-fail"));
        app.UpdateSession(s => s with { Name = "last-create-fail", LifecycleState = SessionLifecycle.Ready });
        var intel = new PaneIntelligencePipeline();
        var factory = new FailAfterPersistingFactory(app, store);
        var cp = new ControlPlaneService(
            app,
            factory,
            intel,
            new HeuristicAgentDetector(),
            store: store);
        try
        {
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(async () =>
                await cp.DispatchAsync(
                    ProtocolMethods.WorkspaceCreate,
                    JsonDocument.Parse(
                        """{"cwd":"/tmp/create-fail","command":"does-not-matter","label":"phantom","create_pane":true}""")
                        .RootElement,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.PaneStartFailed, ex.Code);

            var remaining = Assert.Single(app.ListWorkspaces());
            // The recovery workspace is generated, so its label stays automatic.
            Assert.Equal("create-fail", remaining.Label);
            Assert.False(remaining.CustomLabel);
            Assert.DoesNotContain(app.ListWorkspaces(), w => w.Label == "phantom");
            Assert.Empty(app.ListPanes());
            Assert.False(remaining.DefaultPanePending);

            var loaded = (await store.TryLoadAsync("last-create-fail")).Value;
            Assert.NotNull(loaded);
            Assert.Single(loaded!.Workspaces);
            Assert.Contains(loaded.Workspaces.Values, w => w.Label == "create-fail" && !w.CustomLabel);
            Assert.DoesNotContain(loaded.Workspaces.Values, w => w.Label == "phantom");
            Assert.Empty(loaded.Panes);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Default_pane_workspace_close_is_absent_after_reload()
    {
        await EnsureMigratedAsync();
        await using var store = new SqliteRuntimeSessionStore(_paths);
        var app = new AppState(SessionId.New("pane-ws-close"));
        app.UpdateSession(s => s with { Name = "pane-ws-close", LifecycleState = SessionLifecycle.Ready });
        var intel = new PaneIntelligencePipeline();
        var cp = new ControlPlaneService(
            app,
            TestPaneFactories.Stub(),
            intel,
            new HeuristicAgentDetector(),
            store: store);
        try
        {
            var first = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                JsonDocument.Parse("""{"cwd":"/tmp/pane-ws-a","create_pane":true,"label":"a"}""").RootElement,
                CancellationToken.None);
            var second = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                JsonDocument.Parse("""{"cwd":"/tmp/pane-ws-b","create_pane":true,"label":"b"}""").RootElement,
                CancellationToken.None);
            var firstId = first.GetProperty("workspace_id").GetString()!;
            var secondId = second.GetProperty("workspace_id").GetString()!;
            var secondTab = second.GetProperty("focused_tab_id").GetString()!;
            var secondPane = second.GetProperty("pane").GetProperty("pane_id").GetString()!;

            await cp.DispatchAsync(
                ProtocolMethods.WorkspaceClose,
                JsonDocument.Parse($$"""{"workspace_id":"{{secondId}}"}""").RootElement,
                CancellationToken.None);

            var loaded = (await store.TryLoadAsync("pane-ws-close")).Value;
            Assert.NotNull(loaded);
            Assert.True(loaded!.Workspaces.ContainsKey(firstId));
            Assert.False(loaded.Workspaces.ContainsKey(secondId));
            Assert.False(loaded.Tabs.ContainsKey(secondTab));
            Assert.False(loaded.Panes.ContainsKey(secondPane));
            Assert.Equal(firstId, loaded.FocusedWorkspaceId?.Value);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Restore_reload_drops_pending_empty_workspace()
    {
        await EnsureMigratedAsync();
        await using var store = new SqliteRuntimeSessionStore(_paths);
        var app = new AppState(SessionId.New("pending-reload"));
        app.UpdateSession(s => s with { Name = "pending-reload", LifecycleState = SessionLifecycle.Ready });
        var keep = app.CreateWorkspace("/tmp/keep", "keep");
        var keepPane = app.RegisterPane(new PaneState
        {
            Id = PaneId.New(),
            TabId = keep.FocusedTabId!.Value,
            WorkspaceId = keep.Id,
            Label = "shell",
            Cwd = "/tmp/keep",
            Command = "true",
            LifecycleState = PaneLifecycle.Exited,
            IsAlive = false,
        });
        var phantom = app.CreateWorkspace("/tmp/phantom", "phantom", defaultPanePending: true);
        Assert.True(phantom.DefaultPanePending);
        Assert.True((await store.SaveAsync(app.Snapshot())).IsOk);

        var loaded = (await store.TryLoadAsync("pending-reload")).Value;
        Assert.NotNull(loaded);
        Assert.True(loaded!.Workspaces[phantom.Id.Value].DefaultPanePending);

        var restored = SessionGraphRestorer.ReconcileDeadPids(loaded, new FixedLivenessProbe(false));
        restored = SessionGraphRestorer.DropFailedDefaultPaneOrphans(restored);
        Assert.True(restored.Workspaces.ContainsKey(keep.Id.Value));
        Assert.False(restored.Workspaces.ContainsKey(phantom.Id.Value));
        Assert.True(restored.Panes.ContainsKey(keepPane.Id.Value));

        Assert.True((await store.SaveAsync(restored, removeMissingPanes: true)).IsOk);
        var after = (await store.TryLoadAsync("pending-reload")).Value;
        Assert.NotNull(after);
        Assert.True(after!.Workspaces.ContainsKey(keep.Id.Value));
        Assert.False(after.Workspaces.ContainsKey(phantom.Id.Value));
        Assert.False(after.Tabs.ContainsKey(phantom.FocusedTabId!.Value.Value));
    }

    [Fact]
    public async Task Close_persist_fail_prunes_on_later_save()
    {
        await EnsureMigratedAsync();
        await using var inner = new SqliteRuntimeSessionStore(_paths);
        var store = new FailOnceRemoveMissingStore(inner);
        var app = new AppState(SessionId.New("prune-retry"));
        app.UpdateSession(s => s with { Name = "prune-retry", LifecycleState = SessionLifecycle.Ready });
        var intel = new PaneIntelligencePipeline();
        var cp = new ControlPlaneService(
            app,
            TestPaneFactories.Stub(),
            intel,
            new HeuristicAgentDetector(),
            store: store);
        try
        {
            var first = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                JsonDocument.Parse("""{"cwd":"/tmp/prune-a","create_pane":false,"label":"a"}""").RootElement,
                CancellationToken.None);
            var second = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                JsonDocument.Parse("""{"cwd":"/tmp/prune-b","create_pane":false,"label":"b"}""").RootElement,
                CancellationToken.None);
            var firstId = first.GetProperty("workspace_id").GetString()!;
            var secondId = second.GetProperty("workspace_id").GetString()!;

            var ex = await Assert.ThrowsAsync<ControlPlaneException>(async () =>
                await cp.DispatchAsync(
                    ProtocolMethods.WorkspaceClose,
                    JsonDocument.Parse($$"""{"workspace_id":"{{secondId}}"}""").RootElement,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.PersistenceUnavailable, ex.Code);
            Assert.DoesNotContain(app.ListWorkspaces(), w => w.Id.Value == secondId);

            var stillOnDisk = (await inner.TryLoadAsync("prune-retry")).Value;
            Assert.NotNull(stillOnDisk);
            Assert.True(stillOnDisk!.Workspaces.ContainsKey(secondId));

            await cp.DispatchAsync(
                ProtocolMethods.WorkspaceRename,
                JsonDocument.Parse($$"""{"workspace_id":"{{firstId}}","label":"renamed"}""").RootElement,
                CancellationToken.None);

            var loaded = (await inner.TryLoadAsync("prune-retry")).Value;
            Assert.NotNull(loaded);
            Assert.True(loaded!.Workspaces.ContainsKey(firstId));
            Assert.False(loaded.Workspaces.ContainsKey(secondId));
            Assert.Equal("renamed", loaded.Workspaces[firstId].Label);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private sealed class FixedLivenessProbe(bool alive) : IProcessLivenessProbe
    {
        public bool IsAlive(int pid) => alive;
    }

    /// <summary>
    /// Starts then immediately signals Exited so OnRuntimeExited runs best-effort PersistGraphAsync.
    /// </summary>
    private sealed class ImmediateExitPaneFactory : IPaneRuntimeFactory
    {
        public IPaneRuntime Create(PaneSpawnOptions options) => new ImmediateExitRuntime(options.Id);
    }

    private sealed class ImmediateExitRuntime(PaneId id) : IPaneRuntime
    {
        public PaneId Id { get; } = id;
        public bool IsAlive { get; private set; }
        public int? ExitCode { get; private set; }
        public int? Pid { get; private set; }
#pragma warning disable CS0067 // required by IPaneRuntime; output not simulated
        public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
        public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
#pragma warning restore CS0067
        public event Action<IPaneRuntime, int>? Exited;

        public Task StartAsync(CancellationToken ct)
        {
            IsAlive = true;
            Pid = 42_000 + Math.Abs(Id.Value.GetHashCode() % 1000);
            // Exit after StartAsync returns so RegisterRuntime + reconcile run first,
            // then OnRuntimeExited fires best-effort persist (dirty-bit / single-flight).
            _ = Task.Run(async () =>
            {
                await Task.Yield();
                IsAlive = false;
                ExitCode = 0;
                Pid = null;
                Exited?.Invoke(this, 0);
            }, CancellationToken.None);
            return Task.CompletedTask;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask WriteTextAsync(string text, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct) => ValueTask.CompletedTask;
        public string ReadVisibleText() => "";
        public string ReadRecentText(int maxLines) => "";
        public string ReadRecentUnwrappedText(int maxLines) => ReadRecentText(maxLines);
        public string ReadDetectionText() => "";
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// On StartAsync, persists the current graph (including the starting pane) then fails,
    /// reproducing the concurrent PersistGraphAsync + spawn-failure zombie-pane race.
    /// </summary>
    private sealed class FailAfterPersistingFactory(AppState app, IRuntimeSessionStore store) : IPaneRuntimeFactory
    {
        public PaneId? LastPaneId { get; private set; }

        public IPaneRuntime Create(PaneSpawnOptions options)
        {
            LastPaneId = options.Id;
            return new FailAfterPersistingRuntime(options.Id, app, store);
        }
    }

    private sealed class FailAfterPersistingRuntime(
        PaneId id, AppState app, IRuntimeSessionStore store) : IPaneRuntime
    {
        public PaneId Id { get; } = id;
        public bool IsAlive => false;
        public int? ExitCode => null;
        public int? Pid => null;
#pragma warning disable CS0067 // required by IPaneRuntime; spawn fails before I/O
        public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
        public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
        public event Action<IPaneRuntime, int>? Exited;
#pragma warning restore CS0067

        public async Task StartAsync(CancellationToken ct)
        {
            // Concurrent PersistGraphAsync wrote the starting pane to SQLite.
            var save = await store.SaveAsync(app.Snapshot(), ct).ConfigureAwait(false);
            if (!save.IsOk)
                throw new InvalidOperationException($"pre-fail save: {save.Error.Message}");
            throw new InvalidOperationException("simulated spawn failure");
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask WriteTextAsync(string text, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct) => ValueTask.CompletedTask;
        public string ReadVisibleText() => "";
        public string ReadRecentText(int maxLines) => "";
        public string ReadRecentUnwrappedText(int maxLines) => ReadRecentText(maxLines);
        public string ReadDetectionText() => "";
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailOnceRemoveMissingStore(IRuntimeSessionStore inner) : IRuntimeSessionStore
    {
        private int _failed;

        public Task<RuntimeResult<SessionState?>> TryLoadAsync(
            string sessionName,
            CancellationToken ct = default) =>
            inner.TryLoadAsync(sessionName, ct);

        public Task<RuntimeResult<RuntimeUnit>> SaveAsync(
            SessionState state,
            CancellationToken ct = default,
            bool removeMissingPanes = false)
        {
            if (removeMissingPanes && Interlocked.CompareExchange(ref _failed, 1, 0) == 0)
            {
                return Task.FromResult(RuntimeResult<RuntimeUnit>.Fail(
                    RuntimePersistenceError.Io("injected removeMissing failure")));
            }

            return inner.SaveAsync(state, ct, removeMissingPanes);
        }

        public Task<RuntimeResult<RuntimeUnit>> DeletePaneAsync(
            PaneId paneId,
            CancellationToken ct = default) =>
            inner.DeletePaneAsync(paneId, ct);

        public Task<RuntimeResult<string>> GetRuntimeSessionIdAsync(CancellationToken ct = default) =>
            inner.GetRuntimeSessionIdAsync(ct);
    }
}
