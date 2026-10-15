namespace Hypa.AgentRuntime.Domain.Cubes.Connections;

/// <summary>
/// The connection of one endpoint over time. The identity is the endpoint id.
/// The entity is pure: it does no I/O and holds no statics. Time comes from
/// the injected <see cref="TimeProvider"/>, and it uses the monotonic clock
/// so that a sleep does not end the grace window early.
/// A transition returns a result. A rejected move leaves the entity unchanged.
/// The caller holds the activation gate while it changes an entity.
/// </summary>
public sealed class EndpointConnection
{
    /// <summary>The quiet status line appears after this wait.</summary>
    public static readonly TimeSpan QuietAfter = TimeSpan.FromSeconds(2);

    /// <summary>The unreachable copy appears after this wait.</summary>
    public static readonly TimeSpan GraceWindow = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(0.5),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(5),
    ];

    private readonly TimeProvider _time;

    public EndpointConnection(string endpointId, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        ArgumentNullException.ThrowIfNull(time);
        EndpointId = endpointId;
        _time = time;
    }

    public string EndpointId { get; }

    /// <summary>The connection that is current. Default until the first commit.</summary>
    public ConnectionId Connection { get; private set; }

    /// <summary>The boot id the endpoint reported for the current connection.</summary>
    public string BootId { get; private set; } = string.Empty;

    public EndpointConnectionState State { get; private set; } = EndpointConnectionState.Connecting;

    /// <summary>The monotonic timestamp of the loss, in <see cref="TimeProvider.GetTimestamp"/> units.</summary>
    public long LostAt { get; private set; }

    /// <summary>Redials started by the schedule since the loss.</summary>
    public int Attempts { get; private set; }

    /// <summary>The monotonic timestamp when the next scheduled redial is due.</summary>
    public long NextAttemptAt { get; private set; }

    /// <summary>The reason code of the last failed redial. Null when none failed.</summary>
    public string? LastFailureCode { get; private set; }

    /// <summary>
    /// True between the commit point and the activation completion. The
    /// state does not change in this window. The restore event waits for
    /// <see cref="CompleteCommit"/>.
    /// </summary>
    public bool IsCommitting { get; private set; }

    private ConnectionId _pendingConnection;
    private string _pendingBootId = string.Empty;

    /// <summary>True while a reconnect is armed: the connection is lost or redialing.</summary>
    public bool IsArmed => State is EndpointConnectionState.Lost or EndpointConnectionState.Reconnecting;

    /// <summary>True when the next scheduled redial may start now.</summary>
    public bool IsDue => IsArmed && Now >= NextAttemptAt;

    /// <summary>True at least <see cref="QuietAfter"/> after the loss.</summary>
    public bool QuietElapsed => IsArmed && Elapsed >= Ticks(QuietAfter);

    /// <summary>True at least <see cref="GraceWindow"/> after the loss.</summary>
    public bool GraceExpired => IsArmed && Elapsed >= Ticks(GraceWindow);

    /// <summary>The wait before attempt <paramref name="attempt"/>. Attempt 1 is the first.</summary>
    public static TimeSpan DelayBefore(int attempt) =>
        Backoff[Math.Clamp(attempt - 1, 0, Backoff.Length - 1)];

    /// <summary>
    /// A committed connection is live. It is valid from every state. A
    /// restore from a lost or redialing state returns the restore event.
    /// </summary>
    public Result<ConnectionRestored?, string> Live(ConnectionId connection, string bootId)
    {
        ArgumentNullException.ThrowIfNull(bootId);
        var problem = ConnectionProblem(connection);
        if (problem is not null)
            return Result<ConnectionRestored?, string>.Fail(problem);

        var previous = State;
        Connection = connection;
        BootId = bootId;
        State = EndpointConnectionState.Live;
        LostAt = 0;
        NextAttemptAt = 0;
        Attempts = 0;
        LastFailureCode = null;

        ConnectionRestored? restored = previous is EndpointConnectionState.Lost
            or EndpointConnectionState.Reconnecting
            ? new ConnectionRestored(EndpointId, connection)
            : null;
        return Result<ConnectionRestored?, string>.Ok(restored);
    }

    /// <summary>
    /// The commit point of a connection. The activation has accepted it, but
    /// the presentation is not yet complete. The state stays as it is. The
    /// endpoint status reads Online while the activation runs, and the
    /// reconnect stays armed until <see cref="CompleteCommit"/>.
    /// </summary>
    public Result<EndpointConnectionState, string> BeginCommit(ConnectionId connection, string bootId)
    {
        ArgumentNullException.ThrowIfNull(bootId);
        var problem = ConnectionProblem(connection);
        if (problem is not null)
            return Result<EndpointConnectionState, string>.Fail(problem);

        _pendingConnection = connection;
        _pendingBootId = bootId;
        IsCommitting = true;
        return Result<EndpointConnectionState, string>.Ok(State);
    }

    /// <summary>
    /// The activation did not complete. The commit window closes and the
    /// state stays as it was.
    /// </summary>
    public void AbandonCommit() => IsCommitting = false;

    /// <summary>
    /// The activation completed. The pending connection becomes live. A
    /// restore from a lost or redialing state returns the restore event.
    /// </summary>
    public Result<ConnectionRestored?, string> CompleteCommit()
    {
        if (!IsCommitting)
            return Result<ConnectionRestored?, string>.Fail("no commit is pending");

        IsCommitting = false;
        return Live(_pendingConnection, _pendingBootId);
    }

    /// <summary>The live connection was lost. Records the loss time.</summary>
    public Result<ConnectionLost, string> Lose(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (State is not EndpointConnectionState.Live)
            return Result<ConnectionLost, string>.Fail($"cannot lose a connection in state {State}");

        State = EndpointConnectionState.Lost;
        LostAt = Now;
        Attempts = 0;
        NextAttemptAt = LostAt + Ticks(DelayBefore(1));
        return Result<ConnectionLost, string>.Ok(new ConnectionLost(EndpointId, reason));
    }

    /// <summary>
    /// Starts one redial and moves to Reconnecting. A scheduled redial needs
    /// the schedule to be due. It counts one attempt and schedules the next.
    /// An immediate redial is a person's click. It skips the wait and does
    /// not count or move the schedule.
    /// </summary>
    public Result<EndpointConnectionState, string> BeginRedial(bool immediate = false)
    {
        if (!IsArmed)
            return Result<EndpointConnectionState, string>.Fail($"cannot redial a connection in state {State}");
        if (!immediate && !IsDue)
            return Result<EndpointConnectionState, string>.Fail("the next redial is not due");

        State = EndpointConnectionState.Reconnecting;
        if (!immediate)
        {
            Attempts++;
            NextAttemptAt = Now + Ticks(DelayBefore(Attempts + 1));
        }

        return Result<EndpointConnectionState, string>.Ok(State);
    }

    /// <summary>
    /// A redial did not connect. It stays armed in Reconnecting and keeps the
    /// reason code.
    /// </summary>
    public Result<EndpointConnectionState, string> RedialFailed(ConnectError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (!IsArmed)
            return Result<EndpointConnectionState, string>.Fail($"cannot fail a redial in state {State}");

        State = EndpointConnectionState.Reconnecting;
        LastFailureCode = error.Reason;
        return Result<EndpointConnectionState, string>.Ok(State);
    }

    /// <summary>
    /// The person chose another endpoint. The reconnect ends in Failed. No
    /// redial runs until a later commit makes the connection live again.
    /// </summary>
    public Result<ConnectionFailed, string> Cancel()
    {
        if (!IsArmed)
            return Result<ConnectionFailed, string>.Fail($"cannot cancel a connection in state {State}");

        State = EndpointConnectionState.Failed;
        return Result<ConnectionFailed, string>.Ok(new ConnectionFailed(EndpointId, "cancelled by selection"));
    }

    private string? ConnectionProblem(ConnectionId connection)
    {
        if (!string.Equals(connection.EndpointId, EndpointId, StringComparison.Ordinal))
            return $"connection {connection} does not belong to {EndpointId}";
        if (!connection.IsKnown)
            return "connection generation is unknown";
        return null;
    }

    private long Now => _time.GetTimestamp();

    private long Elapsed => Now - LostAt;

    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * _time.TimestampFrequency);
}
