namespace Hypa.AgentRuntime.Application;

/// <summary>
// / Last-interact owner of shared-tab PTY size.
/// <c>src/server/headless/client_views.rs:689-751</c>
/// <c>tab_geometry_controllers</c>.
/// </summary>
public sealed class AttachTabGeometryPublication
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _controllers = new(StringComparer.Ordinal);

    public bool Claim(string connectionId, string tabId)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(tabId))
            return false;

        lock (_gate)
        {
            if (_controllers.TryGetValue(tabId, out var owner)
                && string.Equals(owner, connectionId, StringComparison.Ordinal))
            {
                return false;
            }

            _controllers[tabId] = connectionId;
            return true;
        }
    }

    public bool ClaimUnowned(string connectionId, string tabId)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(tabId))
            return false;

        lock (_gate)
        {
            if (_controllers.ContainsKey(tabId))
                return false;

            _controllers[tabId] = connectionId;
            return true;
        }
    }

    public bool IsController(string connectionId, string tabId)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(tabId))
            return false;

        lock (_gate)
        {
            return _controllers.TryGetValue(tabId, out var owner)
                && string.Equals(owner, connectionId, StringComparison.Ordinal);
        }
    }

    public string? Controller(string tabId)
    {
        if (string.IsNullOrWhiteSpace(tabId))
            return null;

        lock (_gate)
            return _controllers.TryGetValue(tabId, out var owner) ? owner : null;
    }

    public IReadOnlyList<string> Release(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return [];

        lock (_gate)
        {
            var released = new List<string>();
            foreach (var (tabId, owner) in _controllers.ToArray())
            {
                if (!string.Equals(owner, connectionId, StringComparison.Ordinal))
                    continue;
                _controllers.Remove(tabId);
                released.Add(tabId);
            }

            released.Sort(StringComparer.Ordinal);
            return released;
        }
    }
}
