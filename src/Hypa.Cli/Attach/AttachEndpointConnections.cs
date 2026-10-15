using System.Diagnostics.CodeAnalysis;
using Hypa.Cli.Attach.Connections;
using Hypa.ControlPlane;
using Hypa.AgentRuntime.Domain.Cubes.Connections;

namespace Hypa.Cli.Attach;

/// <summary>
/// The connection registry of one attach. It holds the identity of each
/// endpoint client, and one <see cref="EndpointConnection"/> for each
/// endpoint id. A client keeps the identity it is bound to until it is
/// unbound or disposed. One instance belongs to one <see cref="AttachLiveState"/>.
/// </summary>
internal sealed class AttachEndpointConnections
{
    private readonly object _gate = new();
    private readonly Dictionary<string, EndpointConnection> _entities = new(StringComparer.Ordinal);
    private readonly Dictionary<ControlPlaneClient, BoundConnection> _byClient =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ControlPlaneClient> _reportedUnbound =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>The clock that new connection entities read.</summary>
    internal TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>
    /// Returns the connection entity of an endpoint. It creates the entity
    /// when the endpoint has none yet. The entity starts in Connecting.
    /// </summary>
    internal EndpointConnection Entity(string endpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        lock (_gate)
        {
            if (!_entities.TryGetValue(endpointId, out var entity))
            {
                entity = new EndpointConnection(endpointId, Time);
                _entities[endpointId] = entity;
            }

            return entity;
        }
    }

    /// <summary>Returns the connection entity of an endpoint when it exists.</summary>
    internal bool TryGetEntity(string endpointId, [MaybeNullWhen(false)] out EndpointConnection connection)
    {
        lock (_gate)
            return _entities.TryGetValue(endpointId, out connection);
    }

    /// <summary>The endpoint whose connection is lost or redialing. Null when none is.</summary>
    internal EndpointConnection? Armed
    {
        get
        {
            lock (_gate)
            {
                foreach (var entity in _entities.Values)
                {
                    if (entity.IsArmed)
                        return entity;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// True the first time a client is reported as unbound. The fallback
    /// log line is written once for each client.
    /// </summary>
    internal bool TryClaimUnboundReport(ControlPlaneClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_gate)
        {
            PruneDisposedLocked();
            return _reportedUnbound.Add(client);
        }
    }

    /// <summary>
    /// Sets the identity of a client. A new identity replaces the old one.
    /// The boot id is kept when the client already has one, so a new
    /// generation of the same connection keeps its boot id.
    /// </summary>
    public void Bind(ControlPlaneClient client, ConnectionId id)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_gate)
        {
            PruneDisposedLocked();
            var bootId = _byClient.TryGetValue(client, out var previous) ? previous.BootId : string.Empty;
            _byClient[client] = new BoundConnection(id, bootId);
        }
    }

    /// <summary>
    /// Sets the identity and the boot id of a client. The boot id is the one
    /// the endpoint reported in its welcome or snapshot for this connection.
    /// </summary>
    public void Bind(ControlPlaneClient client, ConnectionId id, string bootId)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(bootId);
        lock (_gate)
        {
            PruneDisposedLocked();
            _byClient[client] = new BoundConnection(id, bootId);
        }
    }

    /// <summary>Returns the identity of a live bound client.</summary>
    public bool TryGet(ControlPlaneClient client, out ConnectionId id)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_gate)
        {
            if (!client.IsDisposed && _byClient.TryGetValue(client, out var found))
            {
                id = found.Id;
                return true;
            }
        }

        id = default;
        return false;
    }

    /// <summary>
    /// Returns the boot id of a live bound client. False when the client has
    /// no boot id yet.
    /// </summary>
    public bool TryGetBootId(ControlPlaneClient client, out string bootId)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_gate)
        {
            if (!client.IsDisposed
                && _byClient.TryGetValue(client, out var found)
                && found.BootId.Length > 0)
            {
                bootId = found.BootId;
                return true;
            }
        }

        bootId = string.Empty;
        return false;
    }

    /// <summary>Removes the identity of a client.</summary>
    public void Unbind(ControlPlaneClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_gate)
            _byClient.Remove(client);
    }

    /// <summary>True when a client that is not disposed has this identity.</summary>
    public bool IsBound(ConnectionId id)
    {
        lock (_gate)
        {
            foreach (var (client, bound) in _byClient)
            {
                if (!client.IsDisposed && bound.Id == id)
                    return true;
            }
        }

        return false;
    }

    private void PruneDisposedLocked()
    {
        List<ControlPlaneClient>? disposed = null;
        foreach (var client in _byClient.Keys)
        {
            if (client.IsDisposed)
                (disposed ??= []).Add(client);
        }

        if (disposed is not null)
        {
            foreach (var client in disposed)
                _byClient.Remove(client);
        }

        _reportedUnbound.RemoveWhere(client => client.IsDisposed);
    }

    /// <summary>The identity and the boot id of one bound client.</summary>
    private readonly record struct BoundConnection(ConnectionId Id, string BootId);
}
