using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// In-memory per-connection views. Transport detach drops the row.
/// </summary>
public sealed class AttachClientViewPublication : IAttachClientViewPublication
{
    private readonly object _gate = new();
    private readonly Dictionary<string, StoredView> _views = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _clientGenerations = new(StringComparer.Ordinal);

    public Result<AttachClientView, AttachClientViewError> Bind(
        AttachClientViewBindRequest request,
        AttachClientTopology topology)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(topology);
        if (string.IsNullOrWhiteSpace(request.ConnectionId)
            || string.IsNullOrWhiteSpace(request.ClientId))
        {
            return Result<AttachClientView, AttachClientViewError>.Fail(
                AttachClientViewError.InvalidConnection);
        }

        lock (_gate)
        {
            var clientId = request.ClientId.Trim();
            _clientGenerations[clientId] = request.ConnectionGeneration;
            DropStaleUnlocked(clientId, request.ConnectionId);

            var view = AttachClientViewReconciler.Seed(
                new AttachClientView
                {
                    ClientId = clientId,
                    EndpointId = request.EndpointId.Trim(),
                    BootId = request.BootId.Trim(),
                },
                topology);

            _views[request.ConnectionId] = new StoredView
            {
                ConnectionGeneration = request.ConnectionGeneration,
                View = view,
            };
            return Result<AttachClientView, AttachClientViewError>.Ok(view);
        }
    }

    public void Unbind(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return;

        lock (_gate)
            _views.Remove(connectionId);
    }

    public Result<AttachClientViewApplyResult, AttachClientViewError> ApplyFocus(
        AttachClientViewFocusRequest request,
        AttachClientTopology topology)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(topology);
        if (string.IsNullOrWhiteSpace(request.ConnectionId))
        {
            return Result<AttachClientViewApplyResult, AttachClientViewError>.Fail(
                AttachClientViewError.InvalidConnection);
        }

        lock (_gate)
        {
            if (!_views.TryGetValue(request.ConnectionId, out var stored))
            {
                return Result<AttachClientViewApplyResult, AttachClientViewError>.Fail(
                    AttachClientViewError.InvalidConnection);
            }

            if (!string.Equals(stored.View.ClientId, request.ClientId, StringComparison.Ordinal))
            {
                return Result<AttachClientViewApplyResult, AttachClientViewError>.Fail(
                    AttachClientViewError.ClientMismatch);
            }

            if (stored.ConnectionGeneration != request.ConnectionGeneration
                || !_clientGenerations.TryGetValue(stored.View.ClientId, out var current)
                || current != request.ConnectionGeneration)
            {
                return Result<AttachClientViewApplyResult, AttachClientViewError>.Fail(
                    AttachClientViewError.StaleGeneration);
            }

            var next = AttachClientViewReconciler.ApplyFocus(
                stored.View,
                topology,
                request.WorkspaceId,
                request.TabId,
                request.PaneId);
            var changed = !ViewsEqual(stored.View, next);
            stored.View = next;
            return Result<AttachClientViewApplyResult, AttachClientViewError>.Ok(
                new AttachClientViewApplyResult
                {
                    View = next,
                    Changed = changed,
                });
        }
    }

    public void ReconcileAll(AttachClientTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        lock (_gate)
        {
            foreach (var stored in _views.Values)
                stored.View = AttachClientViewReconciler.Reconcile(stored.View, topology);
        }
    }

    public AttachClientView? Get(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return null;

        lock (_gate)
            return _views.TryGetValue(connectionId, out var stored) ? stored.View : null;
    }

    public IReadOnlyList<string> ListConnectionIds()
    {
        lock (_gate)
        {
            var ids = _views.Keys.ToArray();
            Array.Sort(ids, StringComparer.Ordinal);
            return ids;
        }
    }

    public IReadOnlyList<string> CollectActivePaneIds(Func<string, bool> isSurfaceActive)
    {
        ArgumentNullException.ThrowIfNull(isSurfaceActive);
        lock (_gate)
        {
            var panes = new List<string>();
            foreach (var (connectionId, stored) in _views)
            {
                if (!isSurfaceActive(connectionId))
                    continue;
                var pane = stored.View.FocusedPaneId();
                if (pane is not null && !panes.Contains(pane, StringComparer.Ordinal))
                    panes.Add(pane);
            }

            panes.Sort(StringComparer.Ordinal);
            return panes;
        }
    }

    private void DropStaleUnlocked(string clientId, string keepConnectionId)
    {
        var stale = _views
            .Where(pair =>
                string.Equals(pair.Value.View.ClientId, clientId, StringComparison.Ordinal)
                && !string.Equals(pair.Key, keepConnectionId, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToList();
        foreach (var connectionId in stale)
            _views.Remove(connectionId);
    }

    private static bool ViewsEqual(AttachClientView left, AttachClientView right) =>
        string.Equals(left.FocusedWorkspaceId, right.FocusedWorkspaceId, StringComparison.Ordinal)
        && MapsEqual(left.ActiveTabIds, right.ActiveTabIds)
        && MapsEqual(left.FocusedPaneIds, right.FocusedPaneIds);

    private static bool MapsEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        if (left.Count != right.Count)
            return false;
        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other)
                || !string.Equals(value, other, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class StoredView
    {
        public required ulong ConnectionGeneration { get; init; }

        public required AttachClientView View { get; set; }
    }
}
