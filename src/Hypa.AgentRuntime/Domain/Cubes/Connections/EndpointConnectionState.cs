namespace Hypa.AgentRuntime.Domain.Cubes.Connections;

/// <summary>
/// The state of one endpoint connection over time.
/// </summary>
public enum EndpointConnectionState
{
    /// <summary>No connection has been committed yet.</summary>
    Connecting,

    /// <summary>The connection is committed and serves the view.</summary>
    Live,

    /// <summary>The live connection was lost. No redial has started yet.</summary>
    Lost,

    /// <summary>A redial is in progress, or has failed and waits to retry.</summary>
    Reconnecting,

    /// <summary>The person chose another endpoint. No redial runs.</summary>
    Failed,
}
