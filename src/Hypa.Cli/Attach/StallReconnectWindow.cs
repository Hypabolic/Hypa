using Hypa.Cli.Attach.Input;
using Hypa.Cli.Attach.Keys;

namespace Hypa.Cli.Attach;

/// <summary>The end of a stall reconnect window.</summary>
/// <param name="Detached">The user ended the attach while the reconnect ran.</param>
/// <param name="ExitReason">Null for the detach key. "stdin closed" when stdin ended.</param>
/// <param name="Reconnect">The reconnect result when the reconnect ended first.</param>
internal sealed record StallWindowResult(
    bool Detached,
    string? ExitReason,
    AttachSession.StallReconnectResult? Reconnect);

/// <summary>
/// Runs a stall reconnect, and keeps the attach under user control while it
/// runs.
/// <list type="bullet">
/// <item>A stdin reader feeds a separate key engine with the live bindings.
/// The detach key ends the window. The window first calls
/// <c>onAbandon</c>, so the paint paths that check the chrome stop, then cancels the
/// reconnect and waits at most <see cref="AbandonGrace"/> for it to stop.
/// It does not wait longer for a reconnect that does not obey its
/// cancellation. A late reconnect result is handed to
/// <c>discardLate</c>.</item>
/// <item>An action host answers on the attach action socket, so
/// <c>latency.report</c> gets a reply.</item>
/// </list>
/// Other keys that the window reads are dropped. The attach is not
/// connected, so they have no pane to go to.
/// </summary>
internal static class StallReconnectWindow
{
    /// <summary>The stdin read wait. It bounds the time to see a cancel.</summary>
    internal const int StdinPollMs = 100;

    internal const string StdinClosed = "stdin closed";

    /// <summary>
    /// The longest wait for a cancelled reconnect after the detach key. With
    /// <see cref="StdinPollMs"/>, the detach ends the attach in well under 1 s.
    /// </summary>
    internal static readonly TimeSpan AbandonGrace = TimeSpan.FromMilliseconds(300);

    internal static async Task<StallWindowResult> RunAsync(
        Func<CancellationToken, Task<AttachSession.StallReconnectResult>> reconnect,
        Func<CancellationToken, IAttachStdinSource> openStdin,
        KeyBindingTable table,
        Func<CancellationToken, IAsyncDisposable?> startActionHost,
        Action<AttachSession.StallReconnectResult> discardLate,
        CancellationToken ct,
        Action? onAbandon = null)
    {
        ArgumentNullException.ThrowIfNull(reconnect);
        ArgumentNullException.ThrowIfNull(openStdin);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(startActionHost);
        ArgumentNullException.ThrowIfNull(discardLate);

        using var reconnectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IAsyncDisposable? host = null;
        IAttachStdinSource? stdin = null;
        try
        {
            host = startActionHost(watchCts.Token);
            stdin = openStdin(watchCts.Token);
            var reconnectTask = Task.Run(() => reconnect(reconnectCts.Token), CancellationToken.None);
            var watch = WatchForExitAsync(stdin, new KeyEngine(table), watchCts.Token);
            var first = await Task.WhenAny(reconnectTask, watch).ConfigureAwait(false);
            if (ReferenceEquals(first, watch)
                && watch.IsCompletedSuccessfully
                && watch.Result is { } exit)
            {
                onAbandon?.Invoke();
                await reconnectCts.CancelAsync().ConfigureAwait(false);
                // Most reconnect steps obey the token, so this wait is short.
                await Task.WhenAny(reconnectTask, Task.Delay(AbandonGrace, CancellationToken.None))
                    .ConfigureAwait(false);
                _ = reconnectTask.ContinueWith(
                    task =>
                    {
                        if (task.IsCompletedSuccessfully)
                            discardLate(task.Result);
                        else
                            _ = task.Exception;
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return new StallWindowResult(true, exit.Reason, null);
            }

            await watchCts.CancelAsync().ConfigureAwait(false);
            WindowExit? lateExit = null;
            try
            {
                lateExit = await watch.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A stdin fault only ends the watch. The reconnect result decides.
            }

            var result = await reconnectTask.ConfigureAwait(false);
            if (lateExit is not null)
            {
                // The detach key came in the same moment as the reconnect
                // result. The key wins: the user asked to leave.
                onAbandon?.Invoke();
                discardLate(result);
                return new StallWindowResult(true, lateExit.Reason, null);
            }

            return new StallWindowResult(false, null, result);
        }
        finally
        {
            await watchCts.CancelAsync().ConfigureAwait(false);
            if (stdin is not null)
                await stdin.DisposeAsync().ConfigureAwait(false);
            if (host is not null)
                await host.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed record WindowExit(string? Reason);

    /// <summary>
    /// Returns the exit when the detach key comes or stdin ends. Returns null
    /// when the window ends first.
    /// </summary>
    private static async Task<WindowExit?> WatchForExitAsync(
        IAttachStdinSource stdin,
        KeyEngine engine,
        CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (!ct.IsCancellationRequested)
        {
            AttachStdinRead read;
            try
            {
                read = await stdin.ReadAsync(buffer, StdinPollMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }

            if (read.Eof)
                return ct.IsCancellationRequested ? null : new WindowExit(StdinClosed);
            if (read.Idle || read.Count <= 0)
                continue;

            foreach (var ev in engine.Feed(buffer.AsSpan(0, read.Count)))
            {
                if (ev.Kind == KeyEngineEventKind.Detach)
                    return new WindowExit(null);
            }
        }

        return null;
    }
}
