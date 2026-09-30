namespace Hypa.Placement.Application;

/// <summary>
/// Connection-attempt generation fence.
/// to each attempt. <c>:197-198</c> rejects a stale generation.
/// </summary>
public sealed class RemoteConnectionFence
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ulong> _current = new(StringComparer.Ordinal);
    private ulong _next = 1;

    public ulong Assign(string endpointKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointKey);
        lock (_gate)
        {
            var generation = _next;
            _next = _next == ulong.MaxValue ? 1 : _next + 1;
            _current[endpointKey] = generation;
            return generation;
        }
    }

    public bool Accept(string endpointKey, ulong generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointKey);
        lock (_gate)
        {
            return _current.TryGetValue(endpointKey, out var current) && current == generation;
        }
    }

    public void Retire(string endpointKey, ulong generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointKey);
        lock (_gate)
        {
            if (_current.TryGetValue(endpointKey, out var current) && current == generation)
                _current.Remove(endpointKey);
        }
    }

    public static string EndpointKey(string target, string session) =>
        target + "\u001f" + session;
}
