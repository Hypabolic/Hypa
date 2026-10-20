using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach;

/// <summary>
/// The control heartbeat of one attach attempt. It has two loops:
/// <list type="bullet">
/// <item>The liveness loop of the local mux link. It takes no control gate.
/// Any received line proves the link alive. It pings only after a quiet
/// interval, and a stall needs a ping with no reply and no line in the
/// timeout. See <see cref="LocalLinkLiveness"/>.</item>
/// <item>The maintenance loop. Each tick checks for a detach request, then
/// runs the overlay drain, the visible-set renewal or the active-cube work
/// under the control gate. A mux error or a call timeout in that work is
/// written to the attach log and is not a stall: only the liveness loop
/// decides a stall. A closed link still ends the attempt. It asks for the
/// update notice on its own slow period. That call holds no gate and its
/// failure is not a stall.</item>
/// </list>
/// </summary>
public sealed partial class AttachSession
{
    /// <summary>The period of the liveness decision. It is not the ping period.</summary>
    internal static readonly TimeSpan LivenessTickPeriod = TimeSpan.FromMilliseconds(250);

    /// <summary>The period of the update notice call.</summary>
    internal static readonly TimeSpan UpdateNoticePeriod = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan UpdateNoticeCallTimeout = TimeSpan.FromSeconds(2);

    internal const string MaintenanceLogStage = "heartbeat_maintenance";

    private static Task ControlHeartbeatAsync(
        ControlPlaneClient control,
        SemaphoreSlim controlGate,
        AttachLiveState live,
        UnixRawTerminal tty,
        CancellationTokenSource linked,
        CancellationToken ct) =>
        ControlHeartbeatAsync(control, controlGate, live, tty, linked, TimeProvider.System, ct);

    internal static async Task ControlHeartbeatAsync(
        ControlPlaneClient control,
        SemaphoreSlim controlGate,
        AttachLiveState live,
        UnixRawTerminal? tty,
        CancellationTokenSource linked,
        TimeProvider time,
        CancellationToken ct,
        LocalLinkLiveness? liveness = null)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(controlGate);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(linked);
        ArgumentNullException.ThrowIfNull(time);
        using var inner = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var probe = LocalLinkProbeLoopAsync(
            () => HeartbeatPingTarget(live, control),
            liveness ?? new LocalLinkLiveness(time),
            time,
            inner.Token);
        var notice = new UpdateNoticeSchedule(time);
        var maintenance = RunPeriodicAsync(
            token => MaintenanceTickAsync(control, controlGate, live, tty, linked, notice, token),
            ControlHeartbeatPeriod,
            time,
            inner.Token);
        var finished = await Task.WhenAny(probe, maintenance).ConfigureAwait(false);
        await inner.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(probe, maintenance).ConfigureAwait(false);
        }
        catch (Exception) when (finished.IsFaulted || finished.IsCanceled || ct.IsCancellationRequested)
        {
            // The first loop to end gives the result below.
        }

        await finished.ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="tick"/> on each period of <paramref name="time"/>
    /// until <paramref name="ct"/> ends. A tick that throws ends the loop.
    /// </summary>
    internal static async Task RunPeriodicAsync(
        Func<CancellationToken, Task> tick,
        TimeSpan period,
        TimeProvider time,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tick);
        ArgumentNullException.ThrowIfNull(time);
        using var timer = new PeriodicTimer(period, time);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            await tick(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The liveness loop of the local mux link. It never takes the control
    /// gate and never waits for a ping reply inside a tick.
    /// <paramref name="observeTick"/> sees each decision. Tests use it to
    /// know when a tick ran.
    /// </summary>
    internal static async Task LocalLinkProbeLoopAsync(
        Func<ControlPlaneClient> target,
        LocalLinkLiveness liveness,
        TimeProvider time,
        CancellationToken ct,
        Action<LocalLinkStep>? observeTick = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(liveness);
        ArgumentNullException.ThrowIfNull(time);
        using var timer = new PeriodicTimer(LivenessTickPeriod, time);
        // A stall must not wait for the ping that it gave up on.
        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ControlPlaneClient? sampled = null;
        var pings = new List<Task>();
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                // The attempt is the local attach. Its probe asks the local
                // mux. A cube outage belongs to the connection supervisor.
                var client = target();
                if (client.IsDisposed)
                    throw new InvalidOperationException("Not connected");
                if (client.IsTransportClosed)
                    throw new IOException("Connection closed");
                if (!ReferenceEquals(client, sampled))
                {
                    liveness.Reset();
                    sampled = client;
                }

                var step = liveness.Poll(client.LinesReceived, out var pingId);
                if (step == LocalLinkStep.Expired)
                {
                    throw new ControlPlaneClientTimeoutException(
                        $"control ping timed out: no line for {liveness.QuietInterval.TotalMilliseconds:0} ms "
                        + $"and no ping reply in {liveness.PingTimeout.TotalMilliseconds:0} ms");
                }

                if (step == LocalLinkStep.Ping)
                {
                    pings.RemoveAll(task => task.IsCompleted);
                    pings.Add(SendLivenessPingAsync(client, liveness, pingId, pingCts.Token));
                }

                observeTick?.Invoke(step);
            }
        }
        finally
        {
            await pingCts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(pings).ConfigureAwait(false);
        }
    }

    private static async Task SendLivenessPingAsync(
        ControlPlaneClient client,
        LocalLinkLiveness liveness,
        int pingId,
        CancellationToken ct)
    {
        try
        {
            // The tick decides the expiry. The call timeout only ends a call
            // that the tick already gave up on.
            await client.CallAsync(
                    ProtocolMethods.Ping,
                    ct: ct,
                    timeout: liveness.PingTimeout + liveness.QuietInterval)
                .ConfigureAwait(false);
            liveness.Answered(pingId);
        }
        catch (Exception ex) when (ex is OperationCanceledException
            or IOException
            or ObjectDisposedException
            or InvalidOperationException
            or ControlPlaneException
            or ControlPlaneClientTimeoutException)
        {
            // The tick reports a stall. A closed link reaches the read loop.
        }
    }

    /// <summary>
    /// One maintenance tick. The overlay drain and the visible-set renewal go
    /// to the active endpoint, and only to its own live client. A detach
    /// request ends the attempt before any gate wait.
    /// </summary>
    internal static async Task MaintenanceTickAsync(
        ControlPlaneClient control,
        SemaphoreSlim controlGate,
        AttachLiveState live,
        UnixRawTerminal? tty,
        CancellationTokenSource linked,
        UpdateNoticeSchedule? notice,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(controlGate);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(linked);
        if (CancelIfDetachRequested(live, linked))
            return;

        var target = HeartbeatPingTarget(live, control);
        if (notice is not null)
            await RefreshUpdateNoticeIfDueAsync(target, controlGate, live, tty, notice, token).ConfigureAwait(false);

        await controlGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var active = LiveControlClient(live, control)!;
            if (ReferenceEquals(active, target))
            {
                if (target.IsDisposed)
                    throw new InvalidOperationException("Not connected");
                await DrainOverlayEventsAsync(active, live, tty, token, linked)
                    .ConfigureAwait(false);
                await RenewVisibleSetIfDueAsync(
                        new ControlPlaneCallPort(active), live, token)
                    .ConfigureAwait(false);
            }
            else
            {
                await ServeActiveCubeFromHeartbeatAsync(active, live, tty, token, linked)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is ControlPlaneException or ControlPlaneClientTimeoutException)
        {
            AttachProcessLog.Failed(
                live.ProcessLog,
                MaintenanceLogStage,
                live.SessionName,
                live.AttachClientId,
                $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            controlGate.Release();
        }

        CancelIfDetachRequested(live, linked);
    }

    private static bool CancelIfDetachRequested(AttachLiveState live, CancellationTokenSource linked)
    {
        if (!live.InputDetachRequested && !live.DetachRequested)
            return false;
        try { linked.Cancel(); }
        catch (ObjectDisposedException)
        {
            // The session already ended.
        }

        return true;
    }

    /// <summary>
    /// Asks the local mux for the update notice on its own period. The call
    /// holds no control gate. A failure only keeps the last notice: the
    /// liveness loop owns the stall decision.
    /// </summary>
    private static async Task RefreshUpdateNoticeIfDueAsync(
        ControlPlaneClient target,
        SemaphoreSlim controlGate,
        AttachLiveState live,
        UnixRawTerminal? tty,
        UpdateNoticeSchedule notice,
        CancellationToken token)
    {
        if (live.UpdateNotices is null || !ShowsLocalMux(live) || !notice.TakeDue())
            return;
        if (target.IsDisposed || target.IsTransportClosed)
            return;

        System.Text.Json.JsonElement pong;
        try
        {
            pong = await target.CallAsync(
                    ProtocolMethods.Ping,
                    ct: token,
                    timeout: UpdateNoticeCallTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException
            or ObjectDisposedException
            or InvalidOperationException
            or ControlPlaneException
            or ControlPlaneClientTimeoutException)
        {
            return;
        }

        await controlGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RefreshUpdateNotice(live, tty, pong);
        }
        finally
        {
            controlGate.Release();
        }
    }

    /// <summary>The slow period of the update notice call. The first call is due at once.</summary>
    internal sealed class UpdateNoticeSchedule(TimeProvider time)
    {
        private long? _last;

        public bool TakeDue()
        {
            var now = time.GetTimestamp();
            if (_last is { } last && time.GetElapsedTime(last, now) < UpdateNoticePeriod)
                return false;
            _last = now;
            return true;
        }
    }
}
