using System.Collections.Concurrent;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.ControlPlane;

/// <summary>
/// Row shift. A connection that offered
/// <see cref="AttachEndpointProtocol.RowShiftCapability"/> in its hello can
/// get a live cells delta with <c>shift</c>: the client moves the rows of its
/// kept pane grid, then writes only the rows that still differ. A pane that
/// scrolls thus sends the new rows, not the full pane. A connection without
/// the capability gets the plain delta.
/// </summary>
public sealed partial class ControlPlaneService
{
    private readonly ConcurrentDictionary<string, byte> _rowShiftConnections = new(StringComparer.Ordinal);

    /// <summary>
    /// Turns row shift on for <paramref name="connection"/> when its hello
    /// lists the capability. A hello without it turns row shift off.
    /// </summary>
    private void ConfigureRowShift(IClientConnection connection, AttachEndpointHello hello)
    {
        var offered = hello.OptionalCapabilities is { } optional
            && optional.Contains(AttachEndpointProtocol.RowShiftCapability, StringComparer.Ordinal);
        if (offered)
            _rowShiftConnections[connection.ConnectionId] = 0;
        else
            _rowShiftConnections.TryRemove(connection.ConnectionId, out _);
    }

    /// <summary>True when the cells deltas of <paramref name="connectionId"/> can carry a row shift.</summary>
    internal bool RowShiftOn(string connectionId) => _rowShiftConnections.ContainsKey(connectionId);

    private void ForgetRowShift(string connectionId) => _rowShiftConnections.TryRemove(connectionId, out _);
}
