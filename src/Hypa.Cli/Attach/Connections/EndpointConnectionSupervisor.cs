using System.Net.Sockets;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Cubes.Connections;
using Hypa.AgentRuntime.Domain.Cubes.Connections;
using Hypa.Cli.Attach.Keys;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach.Connections;

/// <summary>
/// Runs the endpoint reconnect step on a short timer for one attach attempt.
/// The step runs under the control gate, so it never races a click or a
/// <c>hypa client action</c>. The timer takes the gate only while a
/// connection is armed.
/// </summary>
internal sealed class EndpointConnectionSupervisor : IAsyncDisposable
{
    internal static readonly TimeSpan TickPeriod = TimeSpan.FromMilliseconds(250);

    private readonly CancellationTokenSource _cts;
    private readonly Task _loop;

    private EndpointConnectionSupervisor(
        Func<bool> armed,
        SemaphoreSlim controlGate,
        Func<CancellationToken, Task<bool>> step,
        TimeProvider time,
        CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = RunAsync(armed, controlGate, step, time, _cts.Token);
    }

    public static EndpointConnectionSupervisor Start(
        Func<bool> armed,
        SemaphoreSlim controlGate,
        Func<CancellationToken, Task<bool>> step,
        CancellationToken ct,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(armed);
        ArgumentNullException.ThrowIfNull(controlGate);
        ArgumentNullException.ThrowIfNull(step);
        return new EndpointConnectionSupervisor(armed, controlGate, step, time ?? TimeProvider.System, ct);
    }

    /// <summary>
    /// One supervisor step for the armed endpoint. The caller holds the
    /// control gate. The step is decided by <see cref="EndpointReconnectPolicy"/>.
    /// This method executes it. A due redial calls <see cref="IEndpointRedialPort"/>
    /// directly. It does not build a mouse result. Returns true when the step
    /// changed state.
    /// </summary>
    internal static async Task<bool> StepAsync(
        AttachLiveState live,
        IEndpointRedialPort redial,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(redial);
        var connection = live.Connections.Armed;
        if (connection is null)
            return false;

        var endpointId = connection.EndpointId;

        // The policy reads the activation state and the transition changes the
        // entity. Both run under ActivationGate, as every other writer does,
        // so a commit or a cancel cannot interleave with them. The gate is not
        // held across the dial or a paint. The order is control gate, then
        // ActivationGate; no code waits for the control gate while it holds
        // ActivationGate.
        ReconnectStep step;
        lock (live.ActivationGate)
        {
            if (live.Connections.Armed is not { } armed || !ReferenceEquals(armed, connection))
                return false;

            var attemptRunning = live.PendingActivation is not null || live.DestConnectAttempt is not null;
            var facts = new ReconnectFacts(
                DetachRequested: live.DetachRequested,
                AttemptRunning: attemptRunning,
                AttemptTargetsEndpoint: AttachSession.AttemptTargetsCube(live, endpointId),
                CommittedEndpointLive: !attemptRunning
                    && string.Equals(live.ConnectedPlacementId, endpointId, StringComparison.Ordinal)
                    && AttachSession.CommittedEndpointIsLive(live, endpointId),
                EndpointInCatalog: live.Cubes.Any(item => string.Equals(item.Id, endpointId, StringComparison.Ordinal)),
                CanRedial: live.Engine.Mode is AttachClientMode.Terminal
                    && !AttachSession.BlocksPresentationInput(live));

            step = EndpointReconnectPolicy.Decide(connection, facts);
            switch (step)
            {
                case ReconnectStep.CommitLive:
                    // The committed client is live again. Commit the connection.
                    AttachSession.MarkEndpointLive(
                        live,
                        AttachSession.LiveConnectionFor(live, endpointId),
                        AttachSession.LiveBootIdFor(live, endpointId));
                    break;

                case ReconnectStep.CancelReconnect:
                    // The cube left the catalog. Nothing is left to dial.
                    AttachSession.CancelReconnect(live, connection);
                    break;

                case ReconnectStep.Redial:
                    // The schedule is due, so the scheduled redial starts and counts one attempt.
                    _ = connection.BeginRedial();
                    break;
            }
        }

        switch (step)
        {
            case ReconnectStep.CommitLive:
                AttachSession.PaintReconnectStatus(live, tty);
                return true;

            case ReconnectStep.KeepStatus:
                return AttachSession.ShowReconnectStatus(live, connection, tty);

            case ReconnectStep.CancelReconnect:
                // The cancel cleared the reconnect status. Paint it away now.
                AttachSession.PaintReconnectStatus(live, tty);
                return true;

            case ReconnectStep.Redial:
                _ = AttachSession.ShowReconnectStatus(live, connection, tty);
                AttachProcessLog.CubesRequested(live.ProcessLog, "reconnect", live.SessionName, live.AttachClientId);
                await redial.RedialAsync(endpointId, ct).ConfigureAwait(false);

                // A failed dial keeps the reconnect armed and stays quiet in grace.
                if (live.Connections.Armed is { } still)
                    _ = AttachSession.ShowReconnectStatus(live, still, tty);
                return true;

            case ReconnectStep.Idle:
            default:
                return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _cts.Dispose();
    }

    private static async Task RunAsync(
        Func<bool> armed,
        SemaphoreSlim controlGate,
        Func<CancellationToken, Task<bool>> step,
        TimeProvider time,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TickPeriod, time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!armed())
                    continue;
                await controlGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await step(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (
                    ex is ControlPlaneException
                        or ControlPlaneClientTimeoutException
                        or InvalidOperationException
                        or IOException
                        or ObjectDisposedException
                        or SocketException)
                {
                    // One failed attempt. The next backoff step tries again.
                }
                finally
                {
                    controlGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
