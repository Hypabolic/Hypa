using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.ProgramStatus;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Restart restore helpers: PID reconciliation without reattaching live processes.
/// </summary>
public static class SessionGraphRestorer
{
    /// <summary>
    /// Demote every pane that cannot be a live reattached process after restart.
    /// never reattaches: IsAlive is always false after reconcile.
    /// <list type="bullet">
    /// <item>Null pid and non-terminal lifecycle → orphaned (or exited if exit_code set).</item>
    /// <item>Non-null pid and probe dead → clear pid, orphaned/exited.</item>
    /// <item>Non-null pid and probe alive → keep pid for diagnostics / future reattach,
    /// force IsAlive=false and lifecycle orphaned/exited until reattach exists.</item>
    /// </list>
    /// </summary>
    public static SessionState ReconcileDeadPids(SessionState state, IProcessLivenessProbe probe)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(probe);

        var panes = new Dictionary<string, PaneState>(state.Panes, StringComparer.Ordinal);
        var changed = false;
        var now = DateTimeOffset.UtcNow;

        foreach (var (key, loaded) in state.Panes)
        {
            // Probe each PID once. The record lifecycle and the PID reconcile below must
            // agree, so a process that exits between two probes cannot keep stale status.
            var alive = loaded.Pid is { } loadedPid && probe.IsAlive(loadedPid);
            var pane = RestoreProgramStatus(loaded, alive, now);
            // pin contract: never restore generation 0 (SQLite DEFAULT 0 / legacy rows).
            // First real occupant identity is 1; clamps here so bump/wait never treat 0 as pin.
            var gen = NormalizeOccupantGeneration(pane.OccupantGeneration);

            if (pane.Pid is null)
            {
                // Null-PID panes must not restore as running/starting.
                var terminal = IsTerminalLifecycle(pane.LifecycleState);
                var nextStatus = pane.StatusAfterProcessDeath();
                var nextKind = pane.AgentAuthority?.Kind ?? pane.AgentKind;
                var nextMessage = pane.AgentAuthority?.Message ?? pane.AgentMessage;
                if (pane.IsAlive
                    || !ReferenceEquals(pane, loaded)
                    || !terminal
                    || gen != pane.OccupantGeneration
                    || nextStatus != pane.AgentStatus
                    || !string.Equals(nextKind, pane.AgentKind, StringComparison.Ordinal)
                    || !string.Equals(nextMessage, pane.AgentMessage, StringComparison.Ordinal))
                {
                    var demotedLifecycle = terminal
                        ? pane.LifecycleState
                        : DemoteLifecycle(pane);
                    panes[key] = pane with
                    {
                        IsAlive = false,
                        LifecycleState = demotedLifecycle,
                        AgentStatus = nextStatus,
                        AgentKind = nextKind,
                        AgentMessage = nextMessage,
                        OccupantGeneration = gen,
                        UpdatedAt = now,
                    };
                    changed = true;
                }

                continue;
            }

            var pid = pane.Pid.Value;
            // Probe branches: keep pid only when the OS still reports the process,
            // for diagnostics and future reattach. Never mark IsAlive.
            panes[key] = pane with
            {
                IsAlive = false,
                Pid = alive ? pid : null,
                LifecycleState = DemoteLifecycle(pane),
                // Held semantic authority owns status across restore; otherwise
                // working/unknown on a non-live pane is presentation noise.
                AgentStatus = pane.StatusAfterProcessDeath(),
                AgentKind = pane.AgentAuthority?.Kind ?? pane.AgentKind,
                AgentMessage = pane.AgentAuthority?.Message ?? pane.AgentMessage,
                OccupantGeneration = gen,
                UpdatedAt = now,
            };
            changed = true;
        }

        if (!changed)
            return state;

        return state with
        {
            Panes = panes,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Rebuild the program status of a loaded pane. The pane saved its records, not the
    /// authority they derive. A process that is gone cannot still work or wait for the
    /// person, so the <see cref="ProgramStatusLifecycleEvent.ProcessExit"/> transition drops
    /// its working and blocked records. A process that the probe still sees keeps them.
    /// <see cref="ProgramStatusAuthorityPolicy"/> then derives the authority again.
    /// An explicit report authority, which the pane saved, wins over the records as before.
    /// </summary>
    private static PaneState RestoreProgramStatus(PaneState pane, bool survived, DateTimeOffset now)
    {
        var store = pane.ProgramStatusStore;
        if (store.IsEmpty)
            return pane;

        if (!survived)
            store = store.On(ProgramStatusLifecycleEvent.ProcessExit);
        return ProgramStatusAuthorityPolicy.Apply(pane, store, now);
    }

    /// <summary>
    /// Drop a workspace that requested a default pane and still has none.
    /// Intentional <c>create_pane = false</c> rows keep
    /// <see cref="WorkspaceState.DefaultPanePending"/> false and stay.
    // / Historical rows default to false.
    /// <c>src/persist/restore.rs:64-77</c> starts a shell in every saved pane;
    /// do not restore a failed default-pane create as an empty space.
    /// </summary>
    public static SessionState DropFailedDefaultPaneOrphans(SessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var keep = new Dictionary<string, WorkspaceState>(StringComparer.Ordinal);
        var dropped = false;
        var rewritten = false;
        foreach (var (id, workspace) in state.Workspaces)
        {
            if (!workspace.DefaultPanePending)
            {
                keep[id] = workspace;
                continue;
            }

            var hasPane = false;
            foreach (var pane in state.Panes.Values)
            {
                if (string.Equals(pane.WorkspaceId.Value, id, StringComparison.Ordinal))
                {
                    hasPane = true;
                    break;
                }
            }

            if (hasPane)
            {
                keep[id] = workspace with { DefaultPanePending = false };
                rewritten = true;
                continue;
            }

            dropped = true;
        }

        if (!dropped && !rewritten)
            return state;

        IReadOnlyDictionary<string, TabState> tabs = state.Tabs;
        IReadOnlyDictionary<string, PaneState> panes = state.Panes;
        if (dropped)
        {
            var nextTabs = new Dictionary<string, TabState>(StringComparer.Ordinal);
            foreach (var (tabId, tab) in state.Tabs)
            {
                if (keep.ContainsKey(tab.WorkspaceId.Value))
                    nextTabs[tabId] = tab;
            }

            var nextPanes = new Dictionary<string, PaneState>(StringComparer.Ordinal);
            foreach (var (paneId, pane) in state.Panes)
            {
                if (keep.ContainsKey(pane.WorkspaceId.Value))
                    nextPanes[paneId] = pane;
            }

            tabs = nextTabs;
            panes = nextPanes;
        }

        WorkspaceId? focused = state.FocusedWorkspaceId;
        if (focused is { } current && !keep.ContainsKey(current.Value))
        {
            focused = keep.Count == 0
                ? null
                : keep.Values
                    .OrderBy(w => w.Ordinal)
                    .ThenBy(w => w.Id.Value, StringComparer.Ordinal)
                    .First().Id;
        }

        return state with
        {
            Workspaces = keep,
            Tabs = tabs,
            Panes = panes,
            FocusedWorkspaceId = focused,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Occupant generation 0 is a pre-admit / unset sentinel. Wire, pin, and restore
    /// always present the first occupant as generation 1.
    /// </summary>
    public static int NormalizeOccupantGeneration(int generation) =>
        generation <= 0 ? 1 : generation;

    /// <summary>
    /// Promote a restored durable session to a live host process lifecycle when the prior
    /// host exited via graceful shutdown (<c>stopped</c> / <c>stopping</c>).
    /// Does <b>not</b> clear <see cref="SessionLifecycle.FrozenReadOnly"/> — G1 quiescence
    /// requires export or abort as the only exits from a prepared freeze (design §10.4).
    /// Other non-shutdown states (failed, handoff_*, reconciling) are preserved as well.
    /// </summary>
    public static SessionState MarkReadyForLiveHost(SessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        // Only promote graceful-shutdown states. Blind Ready wipe would drop a durable freeze
        // from runtime.checkpoint.prepare after crash/restart with no abort/conflict recovery.
        if (state.LifecycleState is not (SessionLifecycle.Stopped or SessionLifecycle.Stopping))
            return state;

        return state with
        {
            LifecycleState = SessionLifecycle.Ready,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static bool IsTerminalLifecycle(string lifecycle) =>
        lifecycle is PaneLifecycle.Exited or PaneLifecycle.Orphaned or PaneLifecycle.Closed;

    private static string DemoteLifecycle(PaneState pane) =>
        pane.ExitCode is not null || pane.LifecycleState == PaneLifecycle.Exited
            ? PaneLifecycle.Exited
            : PaneLifecycle.Orphaned;

}
