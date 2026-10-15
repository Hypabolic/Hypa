using Hypa.AgentRuntime.Domain.Cubes.Connections;

namespace Hypa.AgentRuntime.Application.Cubes.Connections;

/// <summary>
/// The step that the reconnect supervisor takes on one tick. The adapter
/// executes the step. The step is a value, so the rule has no I/O.
/// </summary>
public enum ReconnectStep
{
    /// <summary>Nothing to do on this tick.</summary>
    Idle,

    /// <summary>The committed client is live again. Commit the connection.</summary>
    CommitLive,

    /// <summary>Show the reconnect status for the current time. Start no redial.</summary>
    KeepStatus,

    /// <summary>The endpoint left the catalog. End the reconnect.</summary>
    CancelReconnect,

    /// <summary>Show the reconnect status, then start one scheduled redial.</summary>
    Redial,
}

/// <summary>
/// The reconnect status that the time since the loss selects. Hidden before
/// the quiet wait, a notice until the grace window ends, then unreachable.
/// </summary>
public enum ReconnectStatusLevel
{
    /// <summary>No reconnect copy yet.</summary>
    Hidden,

    /// <summary>A neutral reconnect line.</summary>
    Notice,

    /// <summary>The endpoint is unreachable. The error copy shows.</summary>
    Unreachable,
}

/// <summary>
/// The facts that the adapter reads from the attach before it asks the
/// policy. Each fact is a plain value. The policy reads no attach state.
/// </summary>
/// <param name="DetachRequested">The attach is detaching. Nothing runs.</param>
/// <param name="AttemptRunning">A connect or an activation is in flight.</param>
/// <param name="AttemptTargetsEndpoint">The in-flight attempt targets this endpoint.</param>
/// <param name="CommittedEndpointLive">The endpoint is the connected placement and its committed client is live.</param>
/// <param name="EndpointInCatalog">The endpoint is still in the cube catalog.</param>
/// <param name="CanRedial">The attach is in terminal mode and presentation input is not blocked.</param>
public readonly record struct ReconnectFacts(
    bool DetachRequested,
    bool AttemptRunning,
    bool AttemptTargetsEndpoint,
    bool CommittedEndpointLive,
    bool EndpointInCatalog,
    bool CanRedial);

/// <summary>
/// Decides the reconnect step for one armed endpoint connection. The time
/// comes from the <see cref="EndpointConnection"/>, which reads its injected
/// <see cref="TimeProvider"/>. The policy is pure. The adapter takes the
/// control gate, runs the step, and makes the calls into the attach.
/// </summary>
public static class EndpointReconnectPolicy
{
    /// <summary>Decides the step for one tick. A null or unarmed connection, such as a cancelled one, has no step.</summary>
    public static ReconnectStep Decide(EndpointConnection? connection, ReconnectFacts facts)
    {
        if (connection is null || !connection.IsArmed || facts.DetachRequested)
            return ReconnectStep.Idle;

        if (!facts.AttemptRunning && facts.CommittedEndpointLive)
            return ReconnectStep.CommitLive;

        if (facts.AttemptRunning)
        {
            // Our own redial, or another activation, is in flight. Keep the
            // time-based status moving only when it targets this endpoint.
            return facts.AttemptTargetsEndpoint ? ReconnectStep.KeepStatus : ReconnectStep.Idle;
        }

        if (!facts.EndpointInCatalog)
            return ReconnectStep.CancelReconnect;

        if (!facts.CanRedial || !connection.IsDue)
            return ReconnectStep.KeepStatus;

        return ReconnectStep.Redial;
    }

    /// <summary>The status level that the elapsed time since the loss selects.</summary>
    public static ReconnectStatusLevel StatusFor(EndpointConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.GraceExpired)
            return ReconnectStatusLevel.Unreachable;
        return connection.QuietElapsed ? ReconnectStatusLevel.Notice : ReconnectStatusLevel.Hidden;
    }
}
