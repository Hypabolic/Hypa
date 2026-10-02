using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.Worktrees;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Mutable in-memory session graph. State is separated from runtime:
/// this type holds pure data; PaneRuntime objects live in the server host.
/// </summary>
public sealed class AppState
{
    private readonly object _gate = new();
    private SessionState _currentSession = null!;
    private SessionState _session
    {
        get => _currentSession;
        set
        {
            // Reconcile only when topology changes. Status updates reuse Tabs.
            if (!ReferenceEquals(_currentSession?.Tabs, value.Tabs))
            {
                Dictionary<string, TabState>? reconciled = null;
                foreach (var (key, tab) in value.Tabs)
                {
                    var root = tab.IdentityPaneId;
                    if (root is null && _currentSession is not null
                        && _currentSession.Tabs.TryGetValue(key, out var previous))
                        root = previous.IdentityPaneId;
                    var leaves = LayoutTreeOperations.Leaves(tab.LayoutRoot)
                        .Select(leaf => leaf.PaneId)
                        .Where(id => id is not null && tab.PaneIds.Contains(id.Value))
                        .Select(id => id!.Value).ToArray();
                    if (root is null || !leaves.Contains(root.Value))
                        root = leaves.Length > 0 ? leaves[0] : null;
                    if (root != tab.IdentityPaneId)
                    {
                        reconciled ??= new Dictionary<string, TabState>(value.Tabs);
                        reconciled[key] = tab with { IdentityPaneId = root };
                    }
                }
                if (reconciled is not null)
                    value = value with { Tabs = reconciled };
            }
            _currentSession = value;
        }
    }

    public AppState(SessionId sessionId)
    {
        _session = new SessionState
        {
            Id = sessionId,
            Name = sessionId.Value,
        };
    }

    public SessionId SessionId
    {
        get { lock (_gate) return _session.Id; }
    }

    public SessionState Snapshot()
    {
        lock (_gate)
        {
            return _session with
            {
                Workspaces = new Dictionary<string, WorkspaceState>(_session.Workspaces),
                Tabs = new Dictionary<string, TabState>(_session.Tabs),
                Panes = new Dictionary<string, PaneState>(_session.Panes),
            };
        }
    }

    /// <summary>
    /// Replace the entire session graph (restart hydration). Caller must hold no other locks.
    /// </summary>
    public void Replace(SessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            var panes = new Dictionary<string, PaneState>(state.Panes);
            var tabs = new Dictionary<string, TabState>(state.Tabs.Count, StringComparer.Ordinal);
            foreach (var (key, tab) in state.Tabs)
                tabs[key] = NormalizeTabOccupancy(tab, panes);
            _session = state with
            {
                Workspaces = new Dictionary<string, WorkspaceState>(state.Workspaces),
                Tabs = tabs,
                Panes = panes,
            };
        }
    }

    /// <summary>Alias for <see cref="Replace"/> used by hosted restore path.</summary>
    public void Load(SessionState state) => Replace(state);

    /// <summary>Update session-level metadata (lifecycle, placement, replay flags).</summary>
    public SessionState UpdateSession(Func<SessionState, SessionState> update)
    {
        lock (_gate)
        {
            _session = update(_session) with { UpdatedAt = DateTimeOffset.UtcNow };
            return Snapshot();
        }
    }

    public WorkspaceState CreateWorkspace(
        string cwd,
        string? label = null,
        AtomicBinding? binding = null,
        bool defaultPanePending = false)
    {
        lock (_gate)
        {
            var id = WorkspaceId.New();
            var tabId = TabId.New();
            var resolvedLabel = label;
            if (string.IsNullOrWhiteSpace(resolvedLabel))
            {
                var leaf = Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                resolvedLabel = string.IsNullOrWhiteSpace(leaf) ? cwd : leaf;
            }

            var workspace = new WorkspaceState
            {
                Id = id,
                Ordinal = _session.Workspaces.Values.Select(w => w.Ordinal).DefaultIfEmpty(-1).Max() + 1,
                Label = resolvedLabel,
                CustomLabel = !string.IsNullOrWhiteSpace(label),
                Cwd = cwd,
                FocusedTabId = tabId,
                TabIds = [tabId],
                Binding = binding,
                DefaultPanePending = defaultPanePending,
            };
            var tab = new TabState
            {
                Id = tabId,
                WorkspaceId = id,
                Label = "main",
                Ordinal = 0,
                CustomLabel = false,
            };

            var workspaces = new Dictionary<string, WorkspaceState>(_session.Workspaces) { [id.Value] = workspace };
            var tabs = new Dictionary<string, TabState>(_session.Tabs) { [tabId.Value] = tab };

            _session = _session with
            {
                Workspaces = workspaces,
                Tabs = tabs,
                FocusedWorkspaceId = _session.FocusedWorkspaceId ?? id,
            };
            return workspace;
        }
    }

    public PaneState RegisterPane(PaneState pane)
    {
        lock (_gate)
        {
            if (!_session.Tabs.TryGetValue(pane.TabId.Value, out var tab))
                throw new InvalidOperationException($"Tab {pane.TabId} not found.");

            var panes = new Dictionary<string, PaneState>(_session.Panes) { [pane.Id.Value] = pane };
            // A pane id is in exactly one of: a layout leaf, or HiddenPaneIds,
            // and in exactly one tab. Detach globally before attach so a
            // cross-tab re-register cannot leave the old occupancy.
            var tabs = new Dictionary<string, TabState>(_session.Tabs.Count, StringComparer.Ordinal);
            foreach (var (key, existing) in _session.Tabs)
                tabs[key] = DetachFromHidden(DetachFromTiled(existing, pane.Id), pane.Id);

            tab = tabs[tab.Id.Value];
            if (pane.Placement == PanePlacement.Hidden)
            {
                tab = tab with { HiddenPaneIds = tab.HiddenPaneIds.Append(pane.Id).ToList() };
            }
            else
            {
                var paneIds = tab.PaneIds.Append(pane.Id).ToList();
                var layout = tab.LayoutRoot;
                if (layout is null && tab.PaneIds.Count == 0)
                    layout = LayoutTreeOperations.FromPane(pane);
                else if (!LayoutTreeOperations.ContainsPane(layout, pane.Id))
                    layout = LayoutTreeOperations.AttachPane(layout, LayoutTreeOperations.FromPane(pane));
                tab = tab with
                {
                    PaneIds = paneIds,
                    FocusedPaneId = tab.FocusedPaneId ?? pane.Id,
                    LayoutRoot = layout,
                };
            }

            tabs[tab.Id.Value] = tab;
            _session = _session with { Panes = panes, Tabs = tabs };
            return pane;
        }
    }

    /// <summary>
    /// Clear the default-pane create marker after a pane starts. Register
    /// alone does not clear it: a failed start still has no usable PTY.
    /// </summary>
    public void ClearDefaultPanePending(WorkspaceId workspaceId)
    {
        lock (_gate)
        {
            if (!_session.Workspaces.TryGetValue(workspaceId.Value, out var workspace)
                || !workspace.DefaultPanePending)
            {
                return;
            }

            var workspaces = new Dictionary<string, WorkspaceState>(_session.Workspaces)
            {
                [workspace.Id.Value] = workspace with { DefaultPanePending = false },
            };
            _session = _session with { Workspaces = workspaces };
        }
    }

    public PaneState? GetPane(PaneId id)
    {
        lock (_gate)
            return _session.Panes.TryGetValue(id.Value, out var p) ? p : null;
    }

    public PaneState? UpdatePane(PaneId id, Func<PaneState, PaneState> update)
    {
        lock (_gate)
        {
            if (!_session.Panes.TryGetValue(id.Value, out var current))
                return null;

            var next = update(current) with { UpdatedAt = DateTimeOffset.UtcNow };
            if (next.AgentStatus != current.AgentStatus)
            {
                var seq = _session.NextAgentStateChangeSeq + 1;
                next = next with { LastAgentStateChangeSeq = seq };
                var panes = new Dictionary<string, PaneState>(_session.Panes) { [id.Value] = next };
                _session = _session with { Panes = panes, NextAgentStateChangeSeq = seq };
                return next;
            }

            var nextPanes = new Dictionary<string, PaneState>(_session.Panes) { [id.Value] = next };
            _session = _session with { Panes = nextPanes };
            return next;
        }
    }

    /// <summary>
    /// Set or clear a pane label and rewrite the matching layout leaf under one lock.
    /// </summary>
    public PaneState? RenamePane(PaneId id, string label)
    {
        lock (_gate)
        {
            if (!_session.Panes.TryGetValue(id.Value, out var current))
                return null;

            var next = current with { Label = label, UpdatedAt = DateTimeOffset.UtcNow };
            var panes = new Dictionary<string, PaneState>(_session.Panes) { [id.Value] = next };
            var tabs = _session.Tabs;
            if (_session.Tabs.TryGetValue(next.TabId.Value, out var tab) && tab.LayoutRoot is not null)
            {
                var layout = LayoutTreeOperations.SetPaneLabel(tab.LayoutRoot, id, label);
                if (!ReferenceEquals(layout, tab.LayoutRoot))
                {
                    tabs = new Dictionary<string, TabState>(_session.Tabs)
                    {
                        [tab.Id.Value] = tab with { LayoutRoot = layout },
                    };
                }
            }

            _session = _session with { Panes = panes, Tabs = tabs };
            return next;
        }
    }

    /// <summary>
    /// Session-scoped binding write under one AppState lock: store session binding and
    /// fill only panes whose binding is currently null (race-safe vs concurrent pane-scoped set).
    /// </summary>
    /// <returns>Pane ids that received a fill (for intelligence bind).</returns>
    public IReadOnlyList<PaneId> SetSessionBindingFillNullPanes(AtomicBinding binding, bool governed)
    {
        ArgumentNullException.ThrowIfNull(binding);
        lock (_gate)
        {
            // Governed is sticky: once true, session-scoped writes cannot clear it.
            _session = _session with
            {
                Binding = binding,
                Governed = _session.Governed || governed,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            var filled = new List<PaneId>();
            Dictionary<string, PaneState>? panes = null;
            foreach (var (key, pane) in _session.Panes)
            {
                if (pane.Binding is not null)
                    continue;
                panes ??= new Dictionary<string, PaneState>(_session.Panes);
                panes[key] = pane with
                {
                    Binding = binding,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                filled.Add(pane.Id);
            }

            if (panes is not null)
                _session = _session with { Panes = panes };

            return filled;
        }
    }

    /// <summary>
    /// Resolve process-level tenant/run anchors (Appendix E.1 / KD-6 singularity).
    /// Prefers explicit process anchors, then session/workspace/pane bindings.
    /// </summary>
    public (string? TenantId, string? RunId) ResolveProcessBindingAnchors()
    {
        lock (_gate)
        {
            string? tenant = _session.ProcessTenantId ?? _session.Binding?.TenantId;
            string? run = _session.ProcessRunId ?? _session.Binding?.RunId;

            if (tenant is not null && run is not null)
                return (tenant, run);

            foreach (var ws in _session.Workspaces.Values.OrderBy(w => w.Id.Value, StringComparer.Ordinal))
            {
                tenant ??= ws.Binding?.TenantId;
                run ??= ws.Binding?.RunId;
                if (tenant is not null && run is not null)
                    return (tenant, run);
            }

            foreach (var pane in _session.Panes.Values.OrderBy(p => p.Id.Value, StringComparer.Ordinal))
            {
                tenant ??= pane.Binding?.TenantId;
                run ??= pane.Binding?.RunId;
                if (tenant is not null && run is not null)
                    return (tenant, run);
            }

            return (tenant, run);
        }
    }

    /// <summary>
    /// Capture process tenant/run anchors on first governed/remote admit without
    /// overwriting an existing session Binding or established anchors.
    /// </summary>
    public void CaptureProcessBindingAnchors(string? tenantId, string? runId)
    {
        if (string.IsNullOrWhiteSpace(tenantId) && string.IsNullOrWhiteSpace(runId))
            return;

        lock (_gate)
        {
            var nextTenant = string.IsNullOrWhiteSpace(_session.ProcessTenantId)
                ? (string.IsNullOrWhiteSpace(tenantId) ? null : tenantId)
                : _session.ProcessTenantId;
            var nextRun = string.IsNullOrWhiteSpace(_session.ProcessRunId)
                ? (string.IsNullOrWhiteSpace(runId) ? null : runId)
                : _session.ProcessRunId;

            if (nextTenant == _session.ProcessTenantId && nextRun == _session.ProcessRunId)
                return;

            _session = _session with
            {
                ProcessTenantId = nextTenant,
                ProcessRunId = nextRun,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }
    }

    public bool RemovePane(PaneId id)
    {
        lock (_gate)
        {
            if (!_session.Panes.TryGetValue(id.Value, out var pane))
                return false;

            var panes = new Dictionary<string, PaneState>(_session.Panes);
            panes.Remove(id.Value);

            var tabs = new Dictionary<string, TabState>(_session.Tabs);
            if (tabs.TryGetValue(pane.TabId.Value, out var tab))
            {
                var remaining = tab.PaneIds.Where(p => p.Value != id.Value).ToList();
                var remainingHidden = tab.HiddenPaneIds.Where(p => p.Value != id.Value).ToList();
                var (layout, _) = LayoutTreeOperations.RemovePane(tab.LayoutRoot, id);
                var zoomedPane = tab.ZoomedPaneId?.Value == id.Value ? remaining.FirstOrDefault() : tab.ZoomedPaneId;
                tabs[tab.Id.Value] = tab with
                {
                    PaneIds = remaining,
                    HiddenPaneIds = remainingHidden,
                    FocusedPaneId = tab.FocusedPaneId?.Value == id.Value
                        ? remaining.FirstOrDefault()
                        : tab.FocusedPaneId,
                    LayoutRoot = layout,
                    Zoomed = tab.Zoomed && remaining.Count > 1 && zoomedPane is { Value: not null },
                    ZoomedPaneId = tab.Zoomed && remaining.Count > 1 ? zoomedPane : null,
                };
            }

            _session = _session with { Panes = panes, Tabs = tabs };
            return true;
        }
    }

    public IReadOnlyList<PaneState> ListPanes()
    {
        lock (_gate)
            return _session.Panes.Values.ToList();
    }

    public IReadOnlyList<WorkspaceState> ListWorkspaces()
    {
        lock (_gate)
            return _session.Workspaces.Values
                .OrderBy(w => w.Ordinal)
                .ThenBy(w => w.Id.Value, StringComparer.Ordinal)
                .ToList();
    }

    /// <summary>Move one workspace using insert-before semantics, including the end slot.</summary>
    public WorkspaceState? MoveWorkspace(WorkspaceId workspaceId, int insertIndex)
    {
        lock (_gate)
        {
            var ordered = _session.Workspaces.Values
                .OrderBy(w => w.Ordinal)
                .ThenBy(w => w.Id.Value, StringComparer.Ordinal)
                .ToList();
            if (insertIndex < 0 || insertIndex > ordered.Count)
                throw new ArgumentOutOfRangeException(nameof(insertIndex));
            var source = ordered.FindIndex(w => w.Id.Value == workspaceId.Value);
            if (source < 0)
                return null;

            var destination = source >= insertIndex ? insertIndex : insertIndex - 1;
            if (destination == source)
                return ordered[source];

            var moved = ordered[source];
            ordered.RemoveAt(source);
            ordered.Insert(destination, moved);
            var workspaces = new Dictionary<string, WorkspaceState>(_session.Workspaces);
            for (var i = 0; i < ordered.Count; i++)
                workspaces[ordered[i].Id.Value] = ordered[i] with { Ordinal = i };
            _session = _session with { Workspaces = workspaces };
            return workspaces[workspaceId.Value];
        }
    }

    /// <summary>Move an ordered block before an anchor, or append when no anchor is supplied.</summary>
    public MoveWorkspaceBlockOutcome MoveWorkspaceBlock(
        IReadOnlyList<WorkspaceId> workspaceIds,
        WorkspaceId? beforeWorkspaceId)
    {
        ArgumentNullException.ThrowIfNull(workspaceIds);
        lock (_gate)
        {
            if (workspaceIds.Count == 0)
                return MoveWorkspaceBlockOutcome.Invalid;

            var ordered = _session.Workspaces.Values
                .OrderBy(w => w.Ordinal)
                .ThenBy(w => w.Id.Value, StringComparer.Ordinal)
                .ToList();
            var blockIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in workspaceIds)
            {
                if (!blockIds.Add(id.Value))
                    return MoveWorkspaceBlockOutcome.Invalid;
                if (!_session.Workspaces.ContainsKey(id.Value))
                    return MoveWorkspaceBlockOutcome.NotFound;
            }

            if (beforeWorkspaceId is { } anchor && blockIds.Contains(anchor.Value))
                return MoveWorkspaceBlockOutcome.Invalid;
            if (beforeWorkspaceId is { } existingAnchor
                && !_session.Workspaces.ContainsKey(existingAnchor.Value))
                return MoveWorkspaceBlockOutcome.NotFound;

            var block = workspaceIds.Select(id => _session.Workspaces[id.Value]).ToList();
            var remainder = ordered.Where(w => !blockIds.Contains(w.Id.Value)).ToList();
            var insertion = beforeWorkspaceId is { } before
                ? remainder.FindIndex(w => w.Id.Value == before.Value)
                : remainder.Count;
            if (insertion < 0)
                return MoveWorkspaceBlockOutcome.NotFound;
            remainder.InsertRange(insertion, block);
            var changed = remainder.Select(w => w.Id.Value).Where((id, i) => id != ordered[i].Id.Value).Any();
            if (!changed)
                return MoveWorkspaceBlockOutcome.Unchanged;

            var workspaces = new Dictionary<string, WorkspaceState>(_session.Workspaces);
            for (var i = 0; i < remainder.Count; i++)
                workspaces[remainder[i].Id.Value] = remainder[i] with { Ordinal = i };
            _session = _session with { Workspaces = workspaces };
            return MoveWorkspaceBlockOutcome.Moved;
        }
    }

    public WorkspaceState? GetWorkspace(WorkspaceId id)
    {
        lock (_gate)
            return _session.Workspaces.TryGetValue(id.Value, out var w) ? w : null;
    }

    public TabState? GetTab(TabId id)
    {
        lock (_gate)
            return _session.Tabs.TryGetValue(id.Value, out var t) ? t : null;
    }

    public IReadOnlyList<TabState> ListTabs(WorkspaceId? workspaceId = null)
    {
        lock (_gate)
        {
            var tabs = _session.Tabs.Values.AsEnumerable();
            if (workspaceId is { } ws)
                tabs = tabs.Where(t => t.WorkspaceId.Value == ws.Value);
            return tabs.OrderBy(t => t.Ordinal).ThenBy(t => t.Id.Value, StringComparer.Ordinal).ToList();
        }
    }

    public TabState CreateTab(WorkspaceId workspaceId, string? label = null, bool focus = true)
    {
        lock (_gate)
        {
            if (!_session.Workspaces.TryGetValue(workspaceId.Value, out var ws))
                throw new InvalidOperationException($"Workspace {workspaceId} not found.");

            var tabId = TabId.New();
            var ordinal = ws.TabIds.Count == 0
                ? 0
                : ws.TabIds
                    .Select(id => _session.Tabs.TryGetValue(id.Value, out var t) ? t.Ordinal : -1)
                    .DefaultIfEmpty(-1)
                    .Max() + 1;
            var custom = !string.IsNullOrWhiteSpace(label);
            var tab = new TabState
            {
                Id = tabId,
                WorkspaceId = workspaceId,
                Label = custom ? label! : $"tab-{ordinal + 1}",
                Ordinal = ordinal,
                CustomLabel = custom,
            };
            var tabIds = ws.TabIds.Append(tabId).ToList();
            var workspaces = new Dictionary<string, WorkspaceState>(_session.Workspaces)
            {
                [ws.Id.Value] = ws with
                {
                    TabIds = tabIds,
                    FocusedTabId = focus ? tabId : ws.FocusedTabId ?? tabId,
                },
            };
            var tabs = new Dictionary<string, TabState>(_session.Tabs) { [tabId.Value] = tab };
            _session = _session with { Workspaces = workspaces, Tabs = tabs };
            return tab;
        }
    }

    public TabState? FocusTab(TabId tabId)
    {
        lock (_gate)
        {
            if (!_session.Tabs.TryGetValue(tabId.Value, out var tab))
                return null;
            if (!_session.Workspaces.TryGetValue(tab.WorkspaceId.Value, out var ws))
                return null;
            var workspaces = new Dictionary<string, WorkspaceState>(_session.Workspaces)
            {
                [ws.Id.Value] = ws with { FocusedTabId = tabId },
            };
            _session = _session with
            {
                Workspaces = workspaces,
                FocusedWorkspaceId = ws.Id,
            };
            return tab;
        }
    }

    public WorkspaceState? FocusWorkspace(WorkspaceId workspaceId)
    {
        lock (_gate)
        {
            if (!_session.Workspaces.TryGetValue(workspaceId.Value, out var ws))
                return null;
            _session = _session with { FocusedWorkspaceId = ws.Id };
            return ws;
        }
    }

    /// <summary>
    /// Focus <paramref name="paneId"/>: tab focused pane, workspace focused tab,
    /// session focused workspace, and mark the pane seen. One lock.
    /// </summary>
    public PaneState? FocusPane(PaneId paneId)
    {
        lock (_gate)
        {
            if (!_session.Panes.TryGetValue(paneId.Value, out var pane))
                return null;
            if (pane.Placement == PanePlacement.Hidden)
                return null;
            if (!_session.Tabs.TryGetValue(pane.TabId.Value, out var tab))
                return null;
            if (!_session.Workspaces.TryGetValue(pane.WorkspaceId.Value, out var ws))
                return null;

            var nextPane = pane with { Seen = true, UpdatedAt = DateTimeOffset.UtcNow };
            var panes = new Dictionary<string, PaneState>(_session.Panes) { [pane.Id.Value] = nextPane };
            var tabs = new Dictionary<string, TabState>(_session.Tabs)
            {
                [tab.Id.Value] = tab with { FocusedPaneId = pane.Id },
            };
            var workspaces = new Dictionary<string, WorkspaceState>(_session.Workspaces)
            {
                [ws.Id.Value] = ws with { FocusedTabId = tab.Id },
            };
            _session = _session with
            {
                Panes = panes,
                Tabs = tabs,
                Workspaces = workspaces,
                FocusedWorkspaceId = ws.Id,
            };
            return nextPane;
        }
    }

    /// <summary>
    // / Mark every pane on the tab seen.
    /// in <c>src/app/actions.rs</c> lines 523-543.
    /// </summary>
    public bool MarkTabSeen(TabId tabId)
    {
        lock (_gate)
        {
            Dictionary<string, PaneState>? panes = null;
            foreach (var pane in _session.Panes.Values)
            {
                if (pane.TabId.Value != tabId.Value || pane.Seen)
                    continue;
                panes ??= new Dictionary<string, PaneState>(_session.Panes);
                panes[pane.Id.Value] = pane with { Seen = true, UpdatedAt = DateTimeOffset.UtcNow };
            }

            if (panes is null)
                return false;
            _session = _session with { Panes = panes };
            return true;
        }
    }

    public WorkspaceState? RenameWorkspace(WorkspaceId workspaceId, string label)
    {
        lock (_gate)
        {
            if (!_session.Workspaces.TryGetValue(workspaceId.Value, out var ws))
                return null;
            var next = ws with { Label = label, CustomLabel = true };
            var workspaces = new Dictionary<string, WorkspaceState>(_session.Workspaces)
            {
                [ws.Id.Value] = next,
            };
            _session = _session with { Workspaces = workspaces };
            return next;
        }
    }

    /// <summary>
    /// True when the session has one workspace or none. Call before any pane teardown.
    /// </summary>
    public bool IsLastWorkspace()
    {
        lock (_gate)
            return _session.Workspaces.Count <= 1;
    }

    /// <summary>
    /// Remove a workspace and its tabs/panes from the in-memory graph.
    /// Does not touch the filesystem. Last workspace fails closed.
    /// </summary>
    public CloseWorkspaceOutcome CloseWorkspace(WorkspaceId workspaceId)
    {
        lock (_gate)
        {
            if (!_session.Workspaces.TryGetValue(workspaceId.Value, out var ws))
                return CloseWorkspaceOutcome.NotFound;
            if (_session.Workspaces.Count <= 1)
                return CloseWorkspaceOutcome.LastWorkspace;

            var tabs = new Dictionary<string, TabState>(_session.Tabs);
            var panes = new Dictionary<string, PaneState>(_session.Panes);
            foreach (var tab in _session.Tabs.Values)
            {
                if (tab.WorkspaceId.Value != ws.Id.Value)
                    continue;
                foreach (var paneId in tab.PaneIds)
                    panes.Remove(paneId.Value);
                tabs.Remove(tab.Id.Value);
            }

            foreach (var pane in _session.Panes.Values)
            {
                if (pane.WorkspaceId.Value == ws.Id.Value)
                    panes.Remove(pane.Id.Value);
            }

            var workspaces = new Dictionary<string, WorkspaceState>(_session.Workspaces);
            workspaces.Remove(ws.Id.Value);

            var focused = _session.FocusedWorkspaceId;
            if (focused?.Value == ws.Id.Value)
            {
                // Insertion order: first remaining workspace from ListWorkspaces().
                focused = workspaces.Count == 0
                    ? null
                    : workspaces.Values
                        .OrderBy(w => w.Ordinal)
                        .ThenBy(w => w.Id.Value, StringComparer.Ordinal)
                        .First().Id;
            }

            _session = _session with
            {
                Workspaces = workspaces,
                Tabs = tabs,
                Panes = panes,
                FocusedWorkspaceId = focused,
            };
            return CloseWorkspaceOutcome.Closed;
        }
    }

    public WorkspaceState? SetWorktreeMembership(WorkspaceId workspaceId, WorktreeSpaceMembership? membership)
    {
        lock (_gate)
        {
            if (!_session.Workspaces.TryGetValue(workspaceId.Value, out var ws))
                return null;
            var next = ws with { Worktree = membership };
            var workspaces = new Dictionary<string, WorkspaceState>(_session.Workspaces)
            {
                [ws.Id.Value] = next,
            };
            _session = _session with { Workspaces = workspaces };
            return next;
        }
    }

    public IReadOnlyList<WorkspaceState> WorkspacesSharingKey(string key)
    {
        lock (_gate)
        {
            return _session.Workspaces.Values
                .Where(w => w.Worktree is { } wt
                    && string.Equals(wt.Key, key, StringComparison.Ordinal))
                .OrderBy(w => w.Ordinal)
                .ThenBy(w => w.Id.Value, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public TabState? RenameTab(TabId tabId, string label)
    {
        lock (_gate)
        {
            if (!_session.Tabs.TryGetValue(tabId.Value, out var tab))
                return null;
            var next = tab with { Label = label, CustomLabel = true };
            var tabs = new Dictionary<string, TabState>(_session.Tabs) { [tabId.Value] = next };
            _session = _session with { Tabs = tabs };
            return _session.Tabs[tabId.Value];
        }
    }

    public TabState? MoveTab(TabId tabId, int index)
    {
        lock (_gate)
        {
            if (!_session.Tabs.TryGetValue(tabId.Value, out var tab))
                return null;
            if (!_session.Workspaces.TryGetValue(tab.WorkspaceId.Value, out var ws))
                return null;
            var ordered = ws.TabIds
                .Select(id => _session.Tabs.TryGetValue(id.Value, out var t) ? t : null)
                .Where(t => t is not null)
                .Cast<TabState>()
                .OrderBy(t => t.Ordinal)
                .ToList();
            var current = ordered.FindIndex(t => t.Id.Value == tabId.Value);
            if (current < 0)
                return null;
            if (index < 0 || index >= ordered.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            ordered.RemoveAt(current);
            ordered.Insert(index, tab);
            var tabs = new Dictionary<string, TabState>(_session.Tabs);
            for (var i = 0; i < ordered.Count; i++)
                tabs[ordered[i].Id.Value] = ordered[i] with { Ordinal = i };
            var workspaces = new Dictionary<string, WorkspaceState>(_session.Workspaces)
            {
                [ws.Id.Value] = ws with { TabIds = ordered.Select(t => t.Id).ToList() },
            };
            _session = _session with { Tabs = tabs, Workspaces = workspaces };
            return tabs[tabId.Value];
        }
    }

    /// <summary>
    /// True when closing <paramref name="tabId"/> would remove the last tab of its workspace.
    /// Call before any pane teardown.
    /// </summary>
    public bool IsLastTabInWorkspace(TabId tabId)
    {
        lock (_gate)
        {
            if (!_session.Tabs.TryGetValue(tabId.Value, out var tab))
                return false;
            if (!_session.Workspaces.TryGetValue(tab.WorkspaceId.Value, out var ws))
                return false;
            return ws.TabIds.Count <= 1;
        }
    }

    public CloseTabOutcome CloseTab(TabId tabId)
    {
        lock (_gate)
        {
            if (!_session.Tabs.TryGetValue(tabId.Value, out var tab))
                return CloseTabOutcome.NotFound;
            if (!_session.Workspaces.TryGetValue(tab.WorkspaceId.Value, out var ws))
                return CloseTabOutcome.NotFound;
            if (ws.TabIds.Count <= 1)
                return CloseTabOutcome.LastTab;

            var panes = new Dictionary<string, PaneState>(_session.Panes);
            foreach (var paneId in OccupantPaneIds(tab))
                panes.Remove(paneId.Value);
            var tabs = new Dictionary<string, TabState>(_session.Tabs);
            tabs.Remove(tabId.Value);
            var remaining = ws.TabIds.Where(id => id.Value != tabId.Value).ToList();
            var workspaces = new Dictionary<string, WorkspaceState>(_session.Workspaces)
            {
                [ws.Id.Value] = ws with
                {
                    TabIds = remaining,
                    FocusedTabId = ws.FocusedTabId?.Value == tabId.Value
                        ? remaining.FirstOrDefault()
                        : ws.FocusedTabId,
                },
            };
            _session = _session with { Tabs = tabs, Panes = panes, Workspaces = workspaces };
            return CloseTabOutcome.Closed;
        }
    }

    public TabState? SetTabLayout(
        TabId tabId,
        LayoutNode? root,
        bool? zoomed = null,
        PaneId? zoomedPaneId = null,
        PaneId? focusedPaneId = null)
    {
        lock (_gate)
        {
            if (!_session.Tabs.TryGetValue(tabId.Value, out var tab))
                return null;
            // A pane id occupies exactly one tab tree. Reject a duplicate or
            // foreign tiled leaf instead of dual-occupying another tab.
            var leaves = LayoutTreeOperations.Leaves(root);
            var paneIds = new List<PaneId>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var leaf in leaves)
            {
                if (leaf.PaneId is not { } leafId)
                    continue;
                if (!seen.Add(leafId.Value))
                {
                    throw new InvalidOperationException(
                        $"Pane {leafId.Value} appears more than once in the layout.");
                }
                if (OccupiesHidden(tab, _session.Panes, leafId))
                {
                    throw new InvalidOperationException(
                        $"Pane {leafId.Value} is hidden and cannot be a layout leaf.");
                }
                if (OccupiesForeignTab(tabId, _session.Panes, leafId))
                {
                    throw new InvalidOperationException(
                        $"Pane {leafId.Value} belongs to another tab and cannot be a layout leaf.");
                }

                paneIds.Add(leafId);
            }
            var nextFocused = focusedPaneId ?? tab.FocusedPaneId ?? paneIds.FirstOrDefault();
            if (nextFocused is { } candidate
                && OccupiesHidden(tab, _session.Panes, candidate))
            {
                nextFocused = paneIds.FirstOrDefault();
            }

            var next = tab with
            {
                LayoutRoot = root,
                PaneIds = paneIds.Count > 0 ? paneIds : tab.PaneIds,
                HiddenPaneIds = tab.HiddenPaneIds,
                FocusedPaneId = nextFocused,
                Zoomed = zoomed ?? tab.Zoomed,
                ZoomedPaneId = zoomed == false ? null : zoomedPaneId ?? tab.ZoomedPaneId,
            };
            var tabs = new Dictionary<string, TabState>(_session.Tabs) { [tabId.Value] = next };
            _session = _session with { Tabs = tabs };
            return _session.Tabs[tabId.Value];
        }
    }

    public TabState? UpdateTab(TabId id, Func<TabState, TabState> update)
    {
        lock (_gate)
        {
            if (!_session.Tabs.TryGetValue(id.Value, out var current))
                return null;
            var next = update(current);
            var tabs = new Dictionary<string, TabState>(_session.Tabs) { [id.Value] = next };
            _session = _session with { Tabs = tabs };
            return _session.Tabs[id.Value];
        }
    }

    public PaneState? MovePaneToTab(PaneId paneId, TabId destinationTabId)
    {
        lock (_gate)
        {
            if (!_session.Panes.TryGetValue(paneId.Value, out var pane))
                return null;
            if (pane.Placement == PanePlacement.Hidden)
                return null;
            if (!_session.Tabs.TryGetValue(destinationTabId.Value, out var dest))
                return null;
            if (!_session.Tabs.TryGetValue(pane.TabId.Value, out var source))
                return null;
            if (source.Id.Value == dest.Id.Value)
                return null;

            var nextPane = pane with { TabId = dest.Id, WorkspaceId = dest.WorkspaceId };
            var panes = new Dictionary<string, PaneState>(_session.Panes) { [paneId.Value] = nextPane };

            var sourceRemaining = source.PaneIds.Where(p => p.Value != paneId.Value).ToList();
            var (sourceLayout, _) = LayoutTreeOperations.RemovePane(source.LayoutRoot, paneId);

            var destPaneIds = dest.PaneIds.Contains(paneId)
                ? dest.PaneIds.ToList()
                : dest.PaneIds.Append(paneId).ToList();
            var destLayout = dest.LayoutRoot is null && dest.PaneIds.Count == 0
                ? LayoutTreeOperations.FromPane(nextPane)
                : LayoutTreeOperations.ContainsPane(dest.LayoutRoot, paneId)
                    ? dest.LayoutRoot
                    : LayoutTreeOperations.AttachPane(dest.LayoutRoot, LayoutTreeOperations.FromPane(nextPane));

            var tabs = new Dictionary<string, TabState>(_session.Tabs)
            {
                [source.Id.Value] = source with
                {
                    PaneIds = sourceRemaining,
                    FocusedPaneId = source.FocusedPaneId?.Value == paneId.Value
                        ? sourceRemaining.FirstOrDefault()
                        : source.FocusedPaneId,
                    LayoutRoot = sourceLayout,
                    Zoomed = false,
                    ZoomedPaneId = null,
                },
                [dest.Id.Value] = dest with
                {
                    PaneIds = destPaneIds,
                    FocusedPaneId = dest.FocusedPaneId ?? paneId,
                    LayoutRoot = destLayout,
                },
            };
            _session = _session with { Panes = panes, Tabs = tabs };
            return nextPane;
        }
    }

    /// <summary>
    /// Move hidden occupants from <paramref name="sourceTabId"/> onto
    /// <paramref name="destinationTabId"/> without attaching layout leaves.
    /// </summary>
    public void RehomeHiddenPanes(TabId sourceTabId, TabId destinationTabId)
    {
        lock (_gate)
        {
            if (!_session.Tabs.TryGetValue(sourceTabId.Value, out var source))
                return;
            if (!_session.Tabs.TryGetValue(destinationTabId.Value, out var dest))
                return;
            if (source.Id.Value == dest.Id.Value)
                return;
            if (source.HiddenPaneIds.Count == 0)
                return;

            var hidden = source.HiddenPaneIds.ToList();
            var panes = new Dictionary<string, PaneState>(_session.Panes);
            foreach (var id in hidden)
            {
                if (!panes.TryGetValue(id.Value, out var pane))
                    continue;
                panes[id.Value] = pane with
                {
                    TabId = dest.Id,
                    WorkspaceId = dest.WorkspaceId,
                    Placement = PanePlacement.Hidden,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
            }

            var destHidden = dest.HiddenPaneIds.ToList();
            foreach (var id in hidden)
            {
                if (!destHidden.Any(p => p.Value == id.Value))
                    destHidden.Add(id);
            }

            var tabs = new Dictionary<string, TabState>(_session.Tabs)
            {
                [source.Id.Value] = source with { HiddenPaneIds = [] },
                [dest.Id.Value] = dest with { HiddenPaneIds = destHidden },
            };
            _session = _session with { Panes = panes, Tabs = tabs };
        }
    }

    public static IReadOnlyList<PaneId> OccupantPaneIds(TabState tab)
    {
        if (tab.HiddenPaneIds.Count == 0)
            return tab.PaneIds;
        var list = tab.PaneIds.ToList();
        foreach (var id in tab.HiddenPaneIds)
        {
            if (!list.Exists(p => p.Value == id.Value))
                list.Add(id);
        }

        return list;
    }

    /// <summary>
    /// Placement is the occupancy source. Hidden ids leave every BSP tree
    /// only. Restore strips foreign and hidden leaves instead of admitting
    /// dual occupancy.
    /// </summary>
    public static TabState NormalizeTabOccupancy(
        TabState tab,
        IReadOnlyDictionary<string, PaneState> panes)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(panes);

        var hiddenIds = new List<PaneId>();
        var hiddenSet = new HashSet<string>(StringComparer.Ordinal);
        var tiled = new List<PaneState>();
        var tiledSet = new HashSet<string>(StringComparer.Ordinal);
        var stripIds = new List<PaneId>();
        var stripSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pane in panes.Values)
        {
            var ownedHere = pane.TabId.Value == tab.Id.Value;
            if (pane.Placement == PanePlacement.Hidden)
            {
                if (stripSet.Add(pane.Id.Value))
                    stripIds.Add(pane.Id);
                if (ownedHere && hiddenSet.Add(pane.Id.Value))
                    hiddenIds.Add(pane.Id);
                continue;
            }

            if (!ownedHere)
            {
                if (stripSet.Add(pane.Id.Value))
                    stripIds.Add(pane.Id);
                continue;
            }

            if (tiledSet.Add(pane.Id.Value))
                tiled.Add(pane);
        }

        var layout = LayoutTreeOperations.WithoutPanes(tab.LayoutRoot, stripIds);
        var leftover = LayoutTreeOperations.Leaves(layout)
            .Where(l => l.PaneId is { } id && !tiledSet.Contains(id.Value))
            .Select(l => l.PaneId!.Value)
            .ToList();
        if (leftover.Count > 0)
            layout = LayoutTreeOperations.WithoutPanes(layout, leftover);
        if (layout is null && tiled.Count > 0)
            layout = LayoutTreeOperations.SynthesizeFromPaneIds(tiled);

        var paneIds = new List<PaneId>(tiled.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var leaf in LayoutTreeOperations.Leaves(layout))
        {
            if (leaf.PaneId is not { } id || !tiledSet.Contains(id.Value) || !seen.Add(id.Value))
                continue;
            paneIds.Add(id);
        }

        foreach (var pane in tiled)
        {
            if (seen.Add(pane.Id.Value))
                paneIds.Add(pane.Id);
        }

        PaneId? focused = tab.FocusedPaneId;
        if (focused is not { } fp
            || hiddenSet.Contains(fp.Value)
            || !paneIds.Exists(p => p.Value == fp.Value))
        {
            focused = paneIds.Count > 0 ? paneIds[0] : null;
        }

        var zoomedPane = tab.ZoomedPaneId;
        if (zoomedPane is { } zp
            && (hiddenSet.Contains(zp.Value) || !paneIds.Exists(p => p.Value == zp.Value)))
        {
            zoomedPane = null;
        }

        return tab with
        {
            LayoutRoot = layout,
            PaneIds = paneIds,
            HiddenPaneIds = hiddenIds,
            FocusedPaneId = focused,
            Zoomed = tab.Zoomed && zoomedPane is { Value: not null } && paneIds.Count > 1,
            ZoomedPaneId = tab.Zoomed && zoomedPane is { Value: not null } && paneIds.Count > 1
                ? zoomedPane
                : null,
        };
    }

    private static bool OccupiesHidden(
        TabState tab,
        IReadOnlyDictionary<string, PaneState> panes,
        PaneId id)
    {
        if (tab.HiddenPaneIds.Any(h => h.Value == id.Value))
            return true;
        return panes.TryGetValue(id.Value, out var pane) && pane.Placement == PanePlacement.Hidden;
    }

    /// <summary>
    /// Insert an existing pane as a tiled leaf. One lock. Failure leaves the
    // / original graph.
    /// </summary>
    public Result<PaneVisibilityChange, PaneVisibilityError> TryShowTiled(
        PaneId paneId,
        TabId? tabId,
        PaneId? targetPaneId,
        string? direction,
        double ratio,
        bool focus)
    {
        lock (_gate)
        {
            if (!_session.Panes.TryGetValue(paneId.Value, out var pane))
            {
                return Result<PaneVisibilityChange, PaneVisibilityError>.Fail(
                    PaneVisibilityError.NotFound($"Pane not found: {paneId.Value}"));
            }

            var destTabId = tabId ?? pane.TabId;
            if (!_session.Tabs.TryGetValue(destTabId.Value, out var dest))
            {
                return Result<PaneVisibilityChange, PaneVisibilityError>.Fail(
                    PaneVisibilityError.NotFound($"Tab not found: {destTabId.Value}"));
            }

            if (!_session.Tabs.TryGetValue(pane.TabId.Value, out var source))
            {
                return Result<PaneVisibilityChange, PaneVisibilityError>.Fail(
                    PaneVisibilityError.NotFound($"Tab not found: {pane.TabId.Value}"));
            }

            var alreadyTiledHere = pane.Placement == PanePlacement.Tiled
                && pane.TabId.Value == dest.Id.Value
                && LayoutTreeOperations.ContainsPane(dest.LayoutRoot, paneId);
            if (alreadyTiledHere)
            {
                return Result<PaneVisibilityChange, PaneVisibilityError>.Ok(new PaneVisibilityChange
                {
                    Pane = pane,
                    SourceTabId = source.Id,
                    TargetTabId = dest.Id,
                    From = pane.Placement,
                    To = PanePlacement.Tiled,
                    Changed = false,
                });
            }

            var destIsEmpty = dest.LayoutRoot is null && dest.PaneIds.Count == 0;
            if (!string.IsNullOrWhiteSpace(direction) && !LayoutNode.IsSplitDirection(direction))
            {
                return Result<PaneVisibilityChange, PaneVisibilityError>.Fail(
                    PaneVisibilityError.InvalidTarget("direction must be right or down"));
            }

            if (!LayoutNode.IsValidRatio(ratio))
            {
                return Result<PaneVisibilityChange, PaneVisibilityError>.Fail(
                    PaneVisibilityError.InvalidTarget("ratio must be in (0,1)"));
            }

            if (targetPaneId is { } suppliedTarget)
            {
                if (destIsEmpty
                    || dest.LayoutRoot is null
                    || !LayoutTreeOperations.ContainsPane(dest.LayoutRoot, suppliedTarget))
                {
                    return Result<PaneVisibilityChange, PaneVisibilityError>.Fail(
                        PaneVisibilityError.InvalidTarget(
                            $"Pane not found in layout: {suppliedTarget.Value}"));
                }
            }

            if (!destIsEmpty && !LayoutNode.IsSplitDirection(direction))
            {
                return Result<PaneVisibilityChange, PaneVisibilityError>.Fail(
                    PaneVisibilityError.InvalidTarget("direction must be right or down"));
            }

            var panes = new Dictionary<string, PaneState>(_session.Panes);
            var tabs = new Dictionary<string, TabState>(_session.Tabs.Count, StringComparer.Ordinal);
            foreach (var (key, existing) in _session.Tabs)
                tabs[key] = DetachFromHidden(DetachFromTiled(existing, paneId), paneId);

            dest = tabs[dest.Id.Value];
            var nextPane = pane with
            {
                Placement = PanePlacement.Tiled,
                TabId = dest.Id,
                WorkspaceId = dest.WorkspaceId,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            panes[paneId.Value] = nextPane;

            LayoutNode nextLayout;
            if (dest.LayoutRoot is null && dest.PaneIds.Count == 0)
            {
                nextLayout = LayoutTreeOperations.FromPane(nextPane);
            }
            else
            {
                if (!LayoutNode.IsSplitDirection(direction))
                {
                    return Result<PaneVisibilityChange, PaneVisibilityError>.Fail(
                        PaneVisibilityError.InvalidTarget("direction must be right or down"));
                }

                if (!LayoutNode.IsValidRatio(ratio))
                {
                    return Result<PaneVisibilityChange, PaneVisibilityError>.Fail(
                        PaneVisibilityError.InvalidTarget("ratio must be in (0,1)"));
                }

                var target = targetPaneId ?? dest.FocusedPaneId ?? dest.PaneIds.FirstOrDefault();
                if (target.Value == paneId.Value)
                    target = dest.PaneIds.FirstOrDefault(p => p.Value != paneId.Value);
                if (target.Value is null
                    || dest.LayoutRoot is null
                    || !LayoutTreeOperations.ContainsPane(dest.LayoutRoot, target)
                    || target.Value == paneId.Value)
                {
                    return Result<PaneVisibilityChange, PaneVisibilityError>.Fail(
                        PaneVisibilityError.InvalidTarget(
                            $"Pane not found in layout: {target.Value ?? targetPaneId?.Value}"));
                }

                try
                {
                    nextLayout = LayoutTreeOperations.Split(
                        dest.LayoutRoot,
                        target,
                        LayoutTreeOperations.FromPane(nextPane),
                        direction!,
                        ratio);
                }
                catch (InvalidOperationException)
                {
                    return Result<PaneVisibilityChange, PaneVisibilityError>.Fail(
                        PaneVisibilityError.InvalidTarget($"Pane not found in layout: {target.Value}"));
                }
                catch (ArgumentOutOfRangeException)
                {
                    return Result<PaneVisibilityChange, PaneVisibilityError>.Fail(
                        PaneVisibilityError.InvalidTarget("direction or ratio is invalid"));
                }
            }

            var leaves = LayoutTreeOperations.Leaves(nextLayout);
            var paneIds = new List<PaneId>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var leaf in leaves)
            {
                if (leaf.PaneId is not { } leafId)
                    continue;
                if (!seen.Add(leafId.Value))
                {
                    return Result<PaneVisibilityChange, PaneVisibilityError>.Fail(
                        PaneVisibilityError.DuplicateLeaf(
                            $"Pane {leafId.Value} appears more than once in the layout."));
                }

                paneIds.Add(leafId);
            }

            var nextFocused = focus ? paneId : dest.FocusedPaneId ?? paneId;
            if (nextFocused.Value is null || !seen.Contains(nextFocused.Value))
                nextFocused = paneId;

            tabs[dest.Id.Value] = dest with
            {
                LayoutRoot = nextLayout,
                PaneIds = paneIds,
                HiddenPaneIds = dest.HiddenPaneIds,
                FocusedPaneId = nextFocused,
                Zoomed = false,
                ZoomedPaneId = null,
            };

            _session = _session with { Panes = panes, Tabs = tabs };
            return Result<PaneVisibilityChange, PaneVisibilityError>.Ok(new PaneVisibilityChange
            {
                Pane = nextPane,
                SourceTabId = source.Id,
                TargetTabId = dest.Id,
                From = pane.Placement,
                To = PanePlacement.Tiled,
                Changed = true,
            });
        }
    }

    /// <summary>
    /// Remove a tiled leaf and keep the runtime hidden. One lock.
    // Hypa does not kill the PTY.
    /// </summary>
    public Result<PaneVisibilityChange, PaneVisibilityError> TryHideTiled(PaneId paneId)
    {
        lock (_gate)
        {
            if (!_session.Panes.TryGetValue(paneId.Value, out var pane))
            {
                return Result<PaneVisibilityChange, PaneVisibilityError>.Fail(
                    PaneVisibilityError.NotFound($"Pane not found: {paneId.Value}"));
            }

            if (!_session.Tabs.TryGetValue(pane.TabId.Value, out var tab))
            {
                return Result<PaneVisibilityChange, PaneVisibilityError>.Fail(
                    PaneVisibilityError.NotFound($"Tab not found: {pane.TabId.Value}"));
            }

            if (pane.Placement == PanePlacement.Hidden
                && tab.HiddenPaneIds.Any(p => p.Value == paneId.Value)
                && !LayoutTreeOperations.ContainsPane(tab.LayoutRoot, paneId))
            {
                return Result<PaneVisibilityChange, PaneVisibilityError>.Ok(new PaneVisibilityChange
                {
                    Pane = pane,
                    SourceTabId = tab.Id,
                    TargetTabId = tab.Id,
                    From = PanePlacement.Hidden,
                    To = PanePlacement.Hidden,
                    Changed = false,
                });
            }

            var panes = new Dictionary<string, PaneState>(_session.Panes);
            var tabs = new Dictionary<string, TabState>(_session.Tabs.Count, StringComparer.Ordinal);
            foreach (var (key, existing) in _session.Tabs)
                tabs[key] = DetachFromHidden(DetachFromTiled(existing, paneId), paneId);

            tab = tabs[tab.Id.Value];
            var nextPane = pane with
            {
                Placement = PanePlacement.Hidden,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            panes[paneId.Value] = nextPane;
            var hidden = tab.HiddenPaneIds.ToList();
            if (!hidden.Exists(p => p.Value == paneId.Value))
                hidden.Add(paneId);
            tabs[tab.Id.Value] = tab with { HiddenPaneIds = hidden };
            tab = tabs[tab.Id.Value];

            var targetTabId = tab.Id;
            TabId? closedTabId = null;
            Dictionary<string, WorkspaceState>? workspaces = null;
            if (TabHasNoTiledLeaf(tab)
                && _session.Workspaces.TryGetValue(tab.WorkspaceId.Value, out var workspace)
                && workspace.TabIds.Count > 1)
            {
                var destinationId = HideCloseDestination(workspace, tab, tabs);
                if (tabs.TryGetValue(destinationId.Value, out var destination)
                    && destination.Id.Value != tab.Id.Value)
                {
                    workspaces = new Dictionary<string, WorkspaceState>(_session.Workspaces);
                    RehomeHiddenOnto(panes, tabs, tab, destination);
                    tabs.Remove(tab.Id.Value);
                    var remaining = workspace.TabIds.Where(id => id.Value != tab.Id.Value).ToList();
                    var nextFocus = workspace.FocusedTabId?.Value == tab.Id.Value
                        ? destination.Id
                        : workspace.FocusedTabId;
                    workspaces[workspace.Id.Value] = workspace with
                    {
                        TabIds = remaining,
                        FocusedTabId = nextFocus,
                    };
                    closedTabId = tab.Id;
                    targetTabId = destination.Id;
                    nextPane = panes[paneId.Value];
                }
            }

            _session = workspaces is null
                ? _session with { Panes = panes, Tabs = tabs }
                : _session with { Panes = panes, Tabs = tabs, Workspaces = workspaces };
            return Result<PaneVisibilityChange, PaneVisibilityError>.Ok(new PaneVisibilityChange
            {
                Pane = nextPane,
                SourceTabId = pane.TabId,
                TargetTabId = targetTabId,
                From = pane.Placement,
                To = PanePlacement.Hidden,
                Changed = true,
                ClosedTabId = closedTabId,
            });
        }
    }

    /// <summary>
    /// Capture this pane's placement, its parent split, and the focus this
    /// call may steal. Does not copy unrelated tabs.
    /// </summary>
    public VisibilityRestorePoint? CaptureVisibility(PaneId paneId)
    {
        lock (_gate)
        {
            if (!_session.Panes.TryGetValue(paneId.Value, out var pane))
                return null;

            TabId? focusedTab = null;
            if (_session.FocusedWorkspaceId is { } focusedWorkspace
                && _session.Workspaces.TryGetValue(focusedWorkspace.Value, out var workspace))
            {
                focusedTab = workspace.FocusedTabId;
            }

            _session.Tabs.TryGetValue(pane.TabId.Value, out var tab);
            return new VisibilityRestorePoint
            {
                PaneId = paneId,
                Placement = pane.Placement,
                TabId = pane.TabId,
                WorkspaceId = pane.WorkspaceId,
                Insertion = pane.Placement == PanePlacement.Tiled
                    ? LayoutTreeOperations.DescribeInsertion(tab?.LayoutRoot, paneId)
                    : null,
                TabFocusedPaneId = tab?.FocusedPaneId,
                Zoomed = tab is { Zoomed: true, ZoomedPaneId: { } zoomed }
                    && zoomed.Value == paneId.Value,
                ZoomedPaneId = tab?.ZoomedPaneId,
                FocusedWorkspaceId = _session.FocusedWorkspaceId,
                FocusedTabId = focusedTab,
                SourceTab = tab,
            };
        }
    }

    /// <summary>
    /// Restore this pane's placement after a failed durable write. Only this
    /// pane's membership and a stolen session focus change. Other tab labels,
    /// ratios, zoom, and focus stay. This method takes only the AppState lock.
    /// </summary>
    public void RevertVisibility(VisibilityRestorePoint point, TabId? createdTabId)
    {
        ArgumentNullException.ThrowIfNull(point);
        lock (_gate)
        {
            if (!_session.Panes.TryGetValue(point.PaneId.Value, out var currentPane))
                return;

            var panes = new Dictionary<string, PaneState>(_session.Panes);
            var tabs = new Dictionary<string, TabState>(_session.Tabs.Count, StringComparer.Ordinal);
            foreach (var (key, existing) in _session.Tabs)
                tabs[key] = DetachFromHidden(DetachFromTiled(existing, point.PaneId), point.PaneId);

            var workspaces = new Dictionary<string, WorkspaceState>(_session.Workspaces);
            var sourceExists = tabs.ContainsKey(point.TabId.Value);
            if (!sourceExists)
                sourceExists = RestoreClosedSourceTab(panes, tabs, workspaces, point);
            var stolenSessionFocus = OwnsSessionFocusUnlocked(createdTabId, point);

            if (createdTabId is { } created
                && sourceExists
                && tabs.TryGetValue(created.Value, out var createdTab)
                && OccupantPaneIds(createdTab).Count == 0
                && workspaces.TryGetValue(createdTab.WorkspaceId.Value, out var createdWorkspace)
                && createdWorkspace.TabIds.Count > 1)
            {
                tabs.Remove(created.Value);
                var remaining = createdWorkspace.TabIds.Where(id => id.Value != created.Value).ToList();
                var nextFocus = createdWorkspace.FocusedTabId;
                if (nextFocus?.Value == created.Value)
                    nextFocus = remaining.FirstOrDefault();
                if (nextFocus is { } focusTab
                    && (!tabs.ContainsKey(focusTab.Value)
                        || tabs[focusTab.Value].WorkspaceId.Value != createdWorkspace.Id.Value))
                {
                    nextFocus = remaining.FirstOrDefault();
                }

                workspaces[createdWorkspace.Id.Value] = createdWorkspace with
                {
                    TabIds = remaining,
                    FocusedTabId = nextFocus,
                };
            }

            TabId homeTab;
            WorkspaceId homeWorkspace;
            var homePlacement = point.Placement;
            if (sourceExists)
            {
                homeTab = point.TabId;
                homeWorkspace = point.WorkspaceId;
            }
            else
            {
                var fallback = FindLiveTab(tabs, workspaces, point.WorkspaceId, createdTabId);
                if (fallback is null)
                    return;
                homeTab = fallback.Id;
                homeWorkspace = fallback.WorkspaceId;
                homePlacement = PanePlacement.Hidden;
            }

            var restored = currentPane with
            {
                Placement = homePlacement,
                TabId = homeTab,
                WorkspaceId = homeWorkspace,
            };
            panes[point.PaneId.Value] = restored;
            AttachPaneMembership(tabs, restored, point);

            if (stolenSessionFocus)
                RestoreSessionFocusUnlocked(workspaces, tabs, point);

            _session = _session with
            {
                Panes = panes,
                Tabs = tabs,
                Workspaces = workspaces,
                FocusedWorkspaceId = stolenSessionFocus
                    && point.FocusedWorkspaceId is { } priorWorkspace
                    && workspaces.ContainsKey(priorWorkspace.Value)
                        ? priorWorkspace
                        : _session.FocusedWorkspaceId,
            };
        }
    }

    public void RestoreFocus(WorkspaceId? workspaceId, TabId? tabId)
    {
        if (tabId is { } id)
        {
            FocusTab(id);
            return;
        }

        if (workspaceId is { } workspace)
            FocusWorkspace(workspace);
    }

    private bool OwnsSessionFocusUnlocked(TabId? createdTabId, VisibilityRestorePoint point)
    {
        if (_session.FocusedWorkspaceId is not { } focused)
            return false;
        if (!_session.Workspaces.TryGetValue(focused.Value, out var workspace))
            return false;
        if (createdTabId is { } created && workspace.FocusedTabId?.Value == created.Value)
            return true;
        if (point.OperationFocusedTabId is { } opTab
            && workspace.FocusedTabId?.Value == opTab.Value)
        {
            return true;
        }

        return focused.Value == point.WorkspaceId.Value
            && createdTabId is { } stolen
            && workspace.FocusedTabId?.Value == stolen.Value;
    }

    private static void RestoreSessionFocusUnlocked(
        Dictionary<string, WorkspaceState> workspaces,
        IReadOnlyDictionary<string, TabState> tabs,
        VisibilityRestorePoint point)
    {
        if (point.FocusedWorkspaceId is not { } priorWorkspace
            || point.FocusedTabId is not { } priorTab)
        {
            return;
        }

        if (!tabs.TryGetValue(priorTab.Value, out var tab)
            || tab.WorkspaceId.Value != priorWorkspace.Value
            || !workspaces.TryGetValue(priorWorkspace.Value, out var workspace)
            || workspace.TabIds.All(id => id.Value != priorTab.Value))
        {
            return;
        }

        workspaces[priorWorkspace.Value] = workspace with { FocusedTabId = priorTab };
    }

    private static bool TabHasNoTiledLeaf(TabState tab)
    {
        if (tab.PaneIds.Count > 0)
            return false;
        if (tab.LayoutRoot is null)
            return true;
        foreach (var leaf in LayoutTreeOperations.Leaves(tab.LayoutRoot))
        {
            if (leaf.PaneId is not null)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Tab that receives hidden occupants when a hide removes the last leaf.
    /// Prefer the tab that had focus when this tab opened. Otherwise the
    /// previous tab in the workspace, then the next tab.
    /// </summary>
    private static TabId HideCloseDestination(
        WorkspaceState workspace,
        TabState closed,
        IReadOnlyDictionary<string, TabState> tabs)
    {
        if (closed.OpenedFromTabId is { } opened
            && opened.Value != closed.Id.Value
            && tabs.ContainsKey(opened.Value)
            && workspace.TabIds.Any(id => id.Value == opened.Value))
        {
            var prior = tabs[opened.Value];
            if (prior.WorkspaceId.Value == workspace.Id.Value)
                return opened;
        }

        var index = -1;
        for (var i = 0; i < workspace.TabIds.Count; i++)
        {
            if (workspace.TabIds[i].Value == closed.Id.Value)
            {
                index = i;
                break;
            }
        }

        if (index > 0)
            return workspace.TabIds[index - 1];
        for (var i = index + 1; i < workspace.TabIds.Count; i++)
        {
            if (workspace.TabIds[i].Value != closed.Id.Value)
                return workspace.TabIds[i];
        }

        return workspace.TabIds.First(id => id.Value != closed.Id.Value);
    }

    private static void RehomeHiddenOnto(
        Dictionary<string, PaneState> panes,
        Dictionary<string, TabState> tabs,
        TabState source,
        TabState destination)
    {
        var destHidden = destination.HiddenPaneIds.ToList();
        foreach (var id in source.HiddenPaneIds)
        {
            if (panes.TryGetValue(id.Value, out var occupant))
            {
                panes[id.Value] = occupant with
                {
                    TabId = destination.Id,
                    WorkspaceId = destination.WorkspaceId,
                    Placement = PanePlacement.Hidden,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
            }

            if (!destHidden.Exists(p => p.Value == id.Value))
                destHidden.Add(id);
        }

        tabs[destination.Id.Value] = destination with { HiddenPaneIds = destHidden };
    }

    /// <summary>
    /// Put the pre-image tab back when a hide removed it after taking its
    /// last leaf. Returns true when the source tab is present afterwards.
    /// </summary>
    private static bool RestoreClosedSourceTab(
        Dictionary<string, PaneState> panes,
        Dictionary<string, TabState> tabs,
        Dictionary<string, WorkspaceState> workspaces,
        VisibilityRestorePoint point)
    {
        if (point.SourceTab is not { } sourceTab || sourceTab.Id.Value != point.TabId.Value)
            return false;

        foreach (var key in tabs.Keys.ToList())
            tabs[key] = DetachFromHidden(DetachFromTiled(tabs[key], point.PaneId), point.PaneId);

        var hidden = new List<PaneId>();
        foreach (var id in sourceTab.HiddenPaneIds)
        {
            if (id.Value == point.PaneId.Value)
                continue;
            if (!panes.TryGetValue(id.Value, out var occupant))
                continue;
            // A sibling show after this hide owns that pane. Leave it.
            if (occupant.Placement != PanePlacement.Hidden)
                continue;
            foreach (var key in tabs.Keys.ToList())
                tabs[key] = DetachFromHidden(DetachFromTiled(tabs[key], id), id);
            panes[id.Value] = occupant with
            {
                TabId = sourceTab.Id,
                WorkspaceId = sourceTab.WorkspaceId,
                Placement = PanePlacement.Hidden,
            };
            hidden.Add(id);
        }

        tabs[sourceTab.Id.Value] = sourceTab with { HiddenPaneIds = hidden };
        if (workspaces.TryGetValue(sourceTab.WorkspaceId.Value, out var workspace)
            && workspace.TabIds.All(id => id.Value != sourceTab.Id.Value))
        {
            var tabIds = workspace.TabIds.Append(sourceTab.Id).ToList();
            tabIds.Sort((a, b) =>
            {
                var left = tabs.TryGetValue(a.Value, out var leftTab) ? leftTab.Ordinal : int.MaxValue;
                var right = tabs.TryGetValue(b.Value, out var rightTab) ? rightTab.Ordinal : int.MaxValue;
                var order = left.CompareTo(right);
                return order != 0 ? order : string.CompareOrdinal(a.Value, b.Value);
            });
            workspaces[workspace.Id.Value] = workspace with { TabIds = tabIds };
        }

        if (panes.TryGetValue(point.PaneId.Value, out var failedPane))
        {
            panes[point.PaneId.Value] = failedPane with
            {
                TabId = sourceTab.Id,
                WorkspaceId = sourceTab.WorkspaceId,
                Placement = point.Placement,
            };
        }

        return true;
    }

    private static TabState? FindLiveTab(
        IReadOnlyDictionary<string, TabState> tabs,
        IReadOnlyDictionary<string, WorkspaceState> workspaces,
        WorkspaceId workspaceId,
        TabId? preferred)
    {
        if (preferred is { } id && tabs.TryGetValue(id.Value, out var preferredTab))
            return preferredTab;
        if (workspaces.TryGetValue(workspaceId.Value, out var workspace))
        {
            foreach (var tabId in workspace.TabIds)
            {
                if (tabs.TryGetValue(tabId.Value, out var tab))
                    return tab;
            }
        }

        return tabs.Values.FirstOrDefault();
    }

    private static void AttachPaneMembership(
        Dictionary<string, TabState> tabs,
        PaneState pane,
        VisibilityRestorePoint point)
    {
        if (!tabs.TryGetValue(pane.TabId.Value, out var dest))
            return;

        if (pane.Placement == PanePlacement.Hidden)
        {
            if (!dest.HiddenPaneIds.Any(id => id.Value == pane.Id.Value))
            {
                dest = dest with
                {
                    HiddenPaneIds = dest.HiddenPaneIds.Append(pane.Id).ToList(),
                };
            }

            tabs[dest.Id.Value] = dest;
            return;
        }

        if (LayoutTreeOperations.ContainsPane(dest.LayoutRoot, pane.Id)
            || dest.PaneIds.Any(id => id.Value == pane.Id.Value))
        {
            return;
        }

        LayoutNode nextLayout;
        if (dest.LayoutRoot is null && dest.PaneIds.Count == 0)
        {
            nextLayout = LayoutTreeOperations.FromPane(pane);
        }
        else if (point.Insertion is { } insertion)
        {
            nextLayout = LayoutTreeOperations.Reinsert(
                dest.LayoutRoot,
                LayoutTreeOperations.FromPane(pane),
                insertion);
        }
        else
        {
            nextLayout = LayoutTreeOperations.AttachPane(
                dest.LayoutRoot,
                LayoutTreeOperations.FromPane(pane));
        }

        var paneIds = LayoutTreeOperations.Leaves(nextLayout)
            .Select(leaf => leaf.PaneId)
            .Where(id => id is { Value: not null })
            .Cast<PaneId>()
            .ToList();
        var focused = dest.FocusedPaneId;
        if (point.TabFocusedPaneId?.Value == pane.Id.Value)
        {
            if (point.OperationFocusedPaneId is null
                || focused is null
                || focused.Value == point.OperationFocusedPaneId.Value)
            {
                focused = pane.Id;
            }
        }
        else if (focused is null || !paneIds.Exists(id => id.Value == focused.Value.Value))
            focused = pane.Id;

        var zoomed = dest.Zoomed;
        var zoomedPane = dest.ZoomedPaneId;
        if (point.Zoomed && point.ZoomedPaneId?.Value == pane.Id.Value && !dest.Zoomed)
        {
            zoomed = true;
            zoomedPane = pane.Id;
        }

        tabs[dest.Id.Value] = dest with
        {
            LayoutRoot = nextLayout,
            PaneIds = paneIds,
            FocusedPaneId = focused,
            Zoomed = zoomed,
            ZoomedPaneId = zoomed ? zoomedPane : null,
        };
    }

    private static bool OccupiesForeignTab(
        TabId tabId,
        IReadOnlyDictionary<string, PaneState> panes,
        PaneId id) =>
        panes.TryGetValue(id.Value, out var pane) && pane.TabId.Value != tabId.Value;

    private static TabState DetachFromTiled(TabState tab, PaneId id)
    {
        if (!tab.PaneIds.Any(p => p.Value == id.Value)
            && !LayoutTreeOperations.ContainsPane(tab.LayoutRoot, id))
        {
            return tab;
        }

        var remaining = tab.PaneIds.Where(p => p.Value != id.Value).ToList();
        var (layout, _) = LayoutTreeOperations.RemovePane(tab.LayoutRoot, id);
        var zoomedPane = tab.ZoomedPaneId?.Value == id.Value ? remaining.FirstOrDefault() : tab.ZoomedPaneId;
        return tab with
        {
            PaneIds = remaining,
            LayoutRoot = layout,
            FocusedPaneId = tab.FocusedPaneId?.Value == id.Value
                ? remaining.FirstOrDefault()
                : tab.FocusedPaneId,
            Zoomed = tab.Zoomed && remaining.Count > 1 && zoomedPane is { Value: not null },
            ZoomedPaneId = tab.Zoomed && remaining.Count > 1 ? zoomedPane : null,
        };
    }

    private static TabState DetachFromHidden(TabState tab, PaneId id)
    {
        if (!tab.HiddenPaneIds.Any(p => p.Value == id.Value))
            return tab;
        return tab with { HiddenPaneIds = tab.HiddenPaneIds.Where(p => p.Value != id.Value).ToList() };
    }
}

public enum CloseTabOutcome
{
    Closed,
    NotFound,
    LastTab,
}

public enum CloseWorkspaceOutcome
{
    Closed,
    NotFound,
    LastWorkspace,
}

public enum MoveWorkspaceBlockOutcome
{
    Moved,
    Unchanged,
    NotFound,
    Invalid,
}
