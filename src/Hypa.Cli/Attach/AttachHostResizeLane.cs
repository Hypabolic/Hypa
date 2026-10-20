namespace Hypa.Cli.Attach;

/// <summary>
/// The host resize path of one attach. A host resize (SIGWINCH) puts its
/// size here and returns at once. One worker applies the sizes, one at a
/// time, and the newest size replaces a size that did not start.
/// <para>
/// A resize holds the control gate across a chain of round trips to the mux
/// (snapshot, layout, a <c>pane.resize</c> for each visible pane). On a cube
/// with 40 ms RTT that chain takes 200 ms or more. A window drag sends many
/// SIGWINCH signals. Without this lane, each signal queued its own chain on
/// the gate, and the chains ran back to back.
/// </para>
/// <para>
/// The key path does not use this lane or the control gate. Keys go to the
/// input sender while a resize waits for its replies.
/// </para>
/// </summary>
internal sealed class AttachHostResizeLane
{
    private readonly object _gate = new();
    private readonly Func<int, int, CancellationToken, Task> _apply;
    private readonly Action<Exception>? _onFault;
    private readonly CancellationToken _ct;
    private (int Cols, int Rows)? _next;
    private Task _worker = Task.CompletedTask;
    private bool _running;

    /// <param name="apply">Applies one host size. It owns the control gate.</param>
    /// <param name="onFault">Gets an exception of <paramref name="apply"/>. The lane continues.</param>
    /// <param name="ct">Stops the lane. A size that did not start is then not applied.</param>
    public AttachHostResizeLane(
        Func<int, int, CancellationToken, Task> apply,
        CancellationToken ct,
        Action<Exception>? onFault = null)
    {
        ArgumentNullException.ThrowIfNull(apply);
        _apply = apply;
        _onFault = onFault;
        _ct = ct;
    }

    /// <summary>
    /// Completes when no size waits and no resize runs. Tests use it.
    /// </summary>
    internal Task Idle
    {
        get
        {
            lock (_gate)
                return _worker;
        }
    }

    /// <summary>
    /// Puts the newest host size on the lane. Does not wait. When a resize
    /// runs, this size replaces a size that waits, and the worker applies it
    /// after the resize that runs.
    /// </summary>
    public void Request(int cols, int rows)
    {
        if (cols < 1 || rows < 1 || _ct.IsCancellationRequested)
            return;
        lock (_gate)
        {
            _next = (cols, rows);
            if (_running)
                return;
            _running = true;
            _worker = Task.Run(RunAsync, CancellationToken.None);
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            (int Cols, int Rows) size;
            lock (_gate)
            {
                if (_next is not { } next || _ct.IsCancellationRequested)
                {
                    _next = null;
                    _running = false;
                    return;
                }

                size = next;
                _next = null;
            }

            try
            {
                await _apply(size.Cols, size.Rows, _ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                // A resize is best-effort. A failed resize must not stop the
                // next one.
                try { _onFault?.Invoke(ex); }
                catch
                {
                    // The fault callback must not stop the lane.
                }
            }
        }
    }
}
