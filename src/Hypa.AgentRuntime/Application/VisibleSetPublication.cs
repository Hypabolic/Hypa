using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Unions live-client tiled ids and overlay ids. Fail-open when a live
/// client is unknown, failed, or expired. A removed client leaves the union.
// / Zero live clients skip.
/// <c>src/server/headless/render.rs:312-327</c>.
/// </summary>
public sealed class VisibleSetPublication : IVisibleSetPublication
{
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly Dictionary<string, ClientPublication> _clients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _retainedPaneIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _postedVisible = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingFull = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _generation = new(StringComparer.Ordinal);

    public VisibleSetPublication(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public Result<VisibleSetSnapshot, VisibleSetError> NoteAttached(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return Result<VisibleSetSnapshot, VisibleSetError>.Fail(VisibleSetError.InvalidConnection);

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var priorFailOpen = AnyLiveFailsOpenUnlocked(now);
            var priorUnion = ClientUnionUnlocked();
            if (!_clients.ContainsKey(connectionId))
                _clients[connectionId] = ClientPublication.Unknown();
            InvalidateAfterMutationUnlocked(priorFailOpen, priorUnion, now);
            return Result<VisibleSetSnapshot, VisibleSetError>.Ok(SnapshotUnlocked(now));
        }
    }

    public Result<VisibleSetSnapshot, VisibleSetError> Publish(VisibleSetPublishRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ConnectionId))
            return Result<VisibleSetSnapshot, VisibleSetError>.Fail(VisibleSetError.InvalidConnection);

        var paneIds = new HashSet<string>(StringComparer.Ordinal);
        if (request.PaneIds is not null)
        {
            foreach (var raw in request.PaneIds)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    return Result<VisibleSetSnapshot, VisibleSetError>.Fail(VisibleSetError.InvalidPaneId);
                paneIds.Add(raw.Trim());
            }
        }

        string? overlay = null;
        if (!string.IsNullOrWhiteSpace(request.OverlayPaneId))
            overlay = request.OverlayPaneId.Trim();

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var priorFailOpen = AnyLiveFailsOpenUnlocked(now);
            var priorUnion = ClientUnionUnlocked();
            _clients[request.ConnectionId] = new ClientPublication
            {
                Status = ClientStatus.Fresh,
                PublishedAt = now,
                PaneIds = paneIds,
                OverlayPaneId = overlay,
            };
            if (paneIds.Count > 0)
                _retainedPaneIds[request.ConnectionId] = paneIds.ToArray();
            InvalidateAfterMutationUnlocked(priorFailOpen, priorUnion, now);
            return Result<VisibleSetSnapshot, VisibleSetError>.Ok(SnapshotUnlocked(now, priorUnion));
        }
    }

    public Result<VisibleSetSnapshot, VisibleSetError> PublishOverlay(
        string connectionId,
        string? overlayPaneId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return Result<VisibleSetSnapshot, VisibleSetError>.Fail(VisibleSetError.InvalidConnection);

        string? overlay = null;
        if (!string.IsNullOrWhiteSpace(overlayPaneId))
            overlay = overlayPaneId.Trim();

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var priorFailOpen = AnyLiveFailsOpenUnlocked(now);
            var priorUnion = ClientUnionUnlocked();
            if (!_clients.TryGetValue(connectionId, out var client))
            {
                client = ClientPublication.Unknown();
                _clients[connectionId] = client;
            }

            client.OverlayPaneId = overlay;
            if (client.Status == ClientStatus.Fresh)
                client.PublishedAt = now;
            InvalidateAfterMutationUnlocked(priorFailOpen, priorUnion, now);
            return Result<VisibleSetSnapshot, VisibleSetError>.Ok(SnapshotUnlocked(now, priorUnion));
        }
    }

    public Result<VisibleSetSnapshot, VisibleSetError> MarkFailed(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return Result<VisibleSetSnapshot, VisibleSetError>.Fail(VisibleSetError.InvalidConnection);

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var priorFailOpen = AnyLiveFailsOpenUnlocked(now);
            var priorUnion = ClientUnionUnlocked();
            if (!_clients.TryGetValue(connectionId, out var client))
            {
                client = ClientPublication.Unknown();
                _clients[connectionId] = client;
            }

            client.Status = ClientStatus.Failed;
            InvalidateAfterMutationUnlocked(priorFailOpen, priorUnion, now);
            return Result<VisibleSetSnapshot, VisibleSetError>.Ok(SnapshotUnlocked(now, priorUnion));
        }
    }

    public Result<VisibleSetSnapshot, VisibleSetError> Remove(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return Result<VisibleSetSnapshot, VisibleSetError>.Fail(VisibleSetError.InvalidConnection);

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var priorFailOpen = AnyLiveFailsOpenUnlocked(now);
            var priorUnion = ClientUnionUnlocked();
            _clients.Remove(connectionId);
            _retainedPaneIds.Remove(connectionId);
            InvalidateAfterMutationUnlocked(priorFailOpen, priorUnion, now);
            return Result<VisibleSetSnapshot, VisibleSetError>.Ok(SnapshotUnlocked(now, priorUnion));
        }
    }

    public IReadOnlyList<string> RetainedPaneIds(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return [];

        lock (_gate)
        {
            if (_retainedPaneIds.TryGetValue(connectionId, out var retained) && retained.Length > 0)
                return retained;
            if (_clients.TryGetValue(connectionId, out var client) && client.PaneIds.Count > 0)
                return client.PaneIds.ToArray();
            return [];
        }
    }

    public Result<VisibleSetSnapshot, VisibleSetError> Renew(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return Result<VisibleSetSnapshot, VisibleSetError>.Fail(VisibleSetError.InvalidConnection);

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_clients.TryGetValue(connectionId, out var client))
                return Result<VisibleSetSnapshot, VisibleSetError>.Fail(VisibleSetError.InvalidConnection);

            if (client.Status == ClientStatus.Fresh)
                client.PublishedAt = now;
            return Result<VisibleSetSnapshot, VisibleSetError>.Ok(SnapshotUnlocked(now));
        }
    }

    public bool MayCapture(string paneId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return false;

        lock (_gate)
            return MayCaptureUnlocked(paneId, now);
    }

    public CaptureAdmission AdmitCapture(string paneId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return CaptureAdmission.Skip;

        lock (_gate)
        {
            if (!MayCaptureUnlocked(paneId, now))
            {
                _postedVisible.Remove(paneId);
                _pendingFull.Add(paneId);
                return CaptureAdmission.Skip;
            }

            if (!_generation.TryGetValue(paneId, out var generation) || generation <= 0)
            {
                BumpUnlocked(paneId);
                generation = _generation[paneId];
            }

            if (_pendingFull.Contains(paneId) || !_postedVisible.Contains(paneId))
            {
                _pendingFull.Add(paneId);
                return CaptureAdmission.For(CaptureAdmissionKind.CaptureFull, generation);
            }

            return CaptureAdmission.For(CaptureAdmissionKind.Capture, generation);
        }
    }

    public void CommitCapture(string paneId, long generation)
    {
        if (string.IsNullOrWhiteSpace(paneId) || generation <= 0)
            return;

        lock (_gate)
        {
            if (!_generation.TryGetValue(paneId, out var current) || current != generation)
                return;
            _pendingFull.Remove(paneId);
            _postedVisible.Add(paneId);
        }
    }

    private bool MayCaptureUnlocked(string paneId, DateTimeOffset now)
    {
        if (_clients.Count == 0)
            return false;
        if (AnyLiveFailsOpenUnlocked(now))
            return true;
        return ClientUnionUnlocked().Contains(paneId);
    }

    private void InvalidateAfterMutationUnlocked(
        bool priorFailOpen,
        HashSet<string> priorUnion,
        DateTimeOffset now)
    {
        var nextFailOpen = AnyLiveFailsOpenUnlocked(now);
        var nextUnion = ClientUnionUnlocked();
        if (_clients.Count == 0)
        {
            foreach (var id in _postedVisible.ToArray())
                BumpUnlocked(id);
            foreach (var id in _pendingFull.ToArray())
                BumpUnlocked(id);
            return;
        }

        if (!priorFailOpen && !nextFailOpen)
        {
            foreach (var id in priorUnion)
            {
                if (!nextUnion.Contains(id))
                    BumpUnlocked(id);
            }

            foreach (var id in nextUnion)
            {
                if (!priorUnion.Contains(id))
                    BumpUnlocked(id);
            }

            return;
        }

        if (!priorFailOpen || nextFailOpen)
            return;

        foreach (var id in _postedVisible.ToArray())
        {
            if (!nextUnion.Contains(id))
                BumpUnlocked(id);
        }

        foreach (var id in nextUnion)
        {
            if (!_postedVisible.Contains(id))
                BumpUnlocked(id);
        }
    }

    private void BumpUnlocked(string paneId)
    {
        _generation.TryGetValue(paneId, out var current);
        _generation[paneId] = current + 1;
        _postedVisible.Remove(paneId);
        _pendingFull.Add(paneId);
    }

    private VisibleSetSnapshot SnapshotUnlocked(DateTimeOffset now, HashSet<string>? priorUnion = null)
    {
        var union = AnyLiveFailsOpenUnlocked(now)
            ? []
            : ClientUnionUnlocked();
        var revealed = new List<string>();
        foreach (var id in union)
        {
            if (priorUnion is null || !priorUnion.Contains(id))
                revealed.Add(id);
        }

        revealed.Sort(StringComparer.Ordinal);
        var unionList = union.ToList();
        unionList.Sort(StringComparer.Ordinal);
        return new VisibleSetSnapshot
        {
            UnionPaneIds = unionList,
            RevealedPaneIds = revealed,
            LiveClientCount = _clients.Count,
            FailOpen = AnyLiveFailsOpenUnlocked(now),
        };
    }

    private bool AnyLiveFailsOpenUnlocked(DateTimeOffset now)
    {
        foreach (var client in _clients.Values)
        {
            if (client.Status == ClientStatus.Unknown || client.Status == ClientStatus.Failed)
                return true;
            if (now - client.PublishedAt > Freshness)
                return true;
        }

        return false;
    }

    private HashSet<string> ClientUnionUnlocked()
    {
        var union = new HashSet<string>(StringComparer.Ordinal);
        foreach (var client in _clients.Values)
        {
            foreach (var id in client.PaneIds)
                union.Add(id);
            if (client.OverlayPaneId is { Length: > 0 } overlay)
                union.Add(overlay);
        }

        return union;
    }

    private enum ClientStatus
    {
        Unknown = 0,
        Fresh = 1,
        Failed = 2,
    }

    private sealed class ClientPublication
    {
        public ClientStatus Status { get; set; }
        public DateTimeOffset PublishedAt { get; set; }
        public HashSet<string> PaneIds { get; set; } = new(StringComparer.Ordinal);
        public string? OverlayPaneId { get; set; }

        public static ClientPublication Unknown() => new() { Status = ClientStatus.Unknown };
    }
}
