namespace Hypa.AgentRuntime.Domain.Cubes.Connections;

/// <summary>
/// The live connection of an endpoint was lost.
/// </summary>
public sealed record ConnectionLost(string EndpointId, string Reason);

/// <summary>
/// A lost or reconnecting endpoint is live again on a new connection.
/// </summary>
public sealed record ConnectionRestored(string EndpointId, ConnectionId Connection);

/// <summary>
/// The reconnect of an endpoint ended with no connection.
/// </summary>
public sealed record ConnectionFailed(string EndpointId, string Reason);
