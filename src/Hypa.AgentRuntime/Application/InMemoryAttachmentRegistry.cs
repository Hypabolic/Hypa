using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Thread-safe in-memory attachment registry. Max attachments is session-wide (health.limits).
/// </summary>
public sealed class InMemoryAttachmentRegistry : IAttachmentRegistry
{
    public const int DefaultMaxAttachments = 16;

    private readonly object _gate = new();
    private readonly Dictionary<string, AttachmentState> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _byConnection = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _bySubscription = new(StringComparer.Ordinal);
    private readonly int _maxAttachments;
    private long _idCounter;

    public InMemoryAttachmentRegistry(int maxAttachments = DefaultMaxAttachments)
    {
        _maxAttachments = maxAttachments > 0 ? maxAttachments : DefaultMaxAttachments;
    }

    public int Count
    {
        get { lock (_gate) return _byId.Count; }
    }

    public AttachmentCreateResult Create(
        string paneId,
        string connectionId,
        string subscriptionId,
        string mode,
        string? leaseId) =>
        CreateCore(paneId, connectionId, subscriptionId, mode, leaseId, reuseExisting: false);

    public AttachmentCreateResult GetOrCreate(
        string paneId,
        string connectionId,
        string subscriptionId,
        string mode,
        string? leaseId) =>
        CreateCore(paneId, connectionId, subscriptionId, mode, leaseId, reuseExisting: true);

    private AttachmentCreateResult CreateCore(
        string paneId,
        string connectionId,
        string subscriptionId,
        string mode,
        string? leaseId,
        bool reuseExisting)
    {
        if (string.IsNullOrWhiteSpace(paneId) ||
            string.IsNullOrWhiteSpace(connectionId) ||
            string.IsNullOrWhiteSpace(subscriptionId) ||
            mode is not (AttachmentModes.Observe or AttachmentModes.Control))
        {
            return new AttachmentCreateResult { Invalid = true };
        }

        if (mode == AttachmentModes.Control && string.IsNullOrWhiteSpace(leaseId))
            return new AttachmentCreateResult { Invalid = true };

        lock (_gate)
        {
            if (reuseExisting)
            {
                var existing = FindExistingUnderLock(paneId, connectionId, subscriptionId, mode);
                if (existing is not null)
                {
                    // Refresh lease_id on control re-attach when the client re-claims.
                    if (mode == AttachmentModes.Control &&
                        !string.IsNullOrWhiteSpace(leaseId) &&
                        !string.Equals(existing.LeaseId, leaseId, StringComparison.Ordinal))
                    {
                        existing = existing with { LeaseId = leaseId };
                        _byId[existing.AttachmentId] = existing;
                    }

                    return new AttachmentCreateResult
                    {
                        Ok = true,
                        Attachment = existing,
                        Reused = true,
                    };
                }
            }

            if (_byId.Count >= _maxAttachments)
                return new AttachmentCreateResult { LimitExceeded = true };

            var id = "att_" + Interlocked.Increment(ref _idCounter);
            var att = new AttachmentState
            {
                AttachmentId = id,
                PaneId = paneId,
                ConnectionId = connectionId,
                SubscriptionId = subscriptionId,
                Mode = mode,
                LeaseId = leaseId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            _byId[id] = att;
            AddIndexUnderLock(_byConnection, connectionId, id);
            AddIndexUnderLock(_bySubscription, subscriptionId, id);
            return new AttachmentCreateResult { Ok = true, Attachment = att };
        }
    }

    public AttachmentState? Drop(string attachmentId)
    {
        if (string.IsNullOrWhiteSpace(attachmentId))
            return null;

        lock (_gate)
            return RemoveUnderLock(attachmentId);
    }

    public IReadOnlyList<AttachmentState> DropBySubscription(string subscriptionId)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
            return [];

        lock (_gate)
        {
            if (!_bySubscription.Remove(subscriptionId, out var set))
                return [];

            var list = new List<AttachmentState>(set.Count);
            foreach (var id in set)
            {
                if (_byId.Remove(id, out var att))
                {
                    RemoveFromIndexUnderLock(_byConnection, att.ConnectionId, id);
                    list.Add(att);
                }
            }

            return list;
        }
    }

    public IReadOnlyList<AttachmentState> DropConnection(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return [];

        lock (_gate)
        {
            if (!_byConnection.Remove(connectionId, out var set))
                return [];

            var list = new List<AttachmentState>(set.Count);
            foreach (var id in set)
            {
                if (_byId.Remove(id, out var att))
                {
                    RemoveFromIndexUnderLock(_bySubscription, att.SubscriptionId, id);
                    list.Add(att);
                }
            }

            return list;
        }
    }

    public IReadOnlyList<AttachmentState> DropByPane(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return [];

        lock (_gate)
        {
            var list = new List<AttachmentState>();
            foreach (var att in _byId.Values.ToList())
            {
                if (!string.Equals(att.PaneId, paneId, StringComparison.Ordinal))
                    continue;
                var removed = RemoveUnderLock(att.AttachmentId);
                if (removed is not null)
                    list.Add(removed);
            }

            return list;
        }
    }

    public AttachmentState? Get(string attachmentId)
    {
        if (string.IsNullOrWhiteSpace(attachmentId))
            return null;
        lock (_gate)
            return _byId.TryGetValue(attachmentId, out var a) ? a : null;
    }

    public IReadOnlyList<AttachmentState> ListAll()
    {
        lock (_gate)
            return _byId.Values.ToList();
    }

    private AttachmentState? FindExistingUnderLock(
        string paneId,
        string connectionId,
        string subscriptionId,
        string mode)
    {
        if (!_bySubscription.TryGetValue(subscriptionId, out var set))
            return null;

        foreach (var id in set)
        {
            if (!_byId.TryGetValue(id, out var att))
                continue;
            if (string.Equals(att.PaneId, paneId, StringComparison.Ordinal) &&
                string.Equals(att.ConnectionId, connectionId, StringComparison.Ordinal) &&
                string.Equals(att.Mode, mode, StringComparison.Ordinal))
            {
                return att;
            }
        }

        return null;
    }

    private AttachmentState? RemoveUnderLock(string attachmentId)
    {
        if (!_byId.Remove(attachmentId, out var att))
            return null;
        RemoveFromIndexUnderLock(_byConnection, att.ConnectionId, attachmentId);
        RemoveFromIndexUnderLock(_bySubscription, att.SubscriptionId, attachmentId);
        return att;
    }

    private static void AddIndexUnderLock(
        Dictionary<string, HashSet<string>> index,
        string key,
        string id)
    {
        if (!index.TryGetValue(key, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            index[key] = set;
        }

        set.Add(id);
    }

    private static void RemoveFromIndexUnderLock(
        Dictionary<string, HashSet<string>> index,
        string key,
        string id)
    {
        if (!index.TryGetValue(key, out var set))
            return;
        set.Remove(id);
        if (set.Count == 0)
            index.Remove(key);
    }
}
