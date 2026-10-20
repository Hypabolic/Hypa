using System.Threading.Channels;

namespace Hypa.ControlPlane;

/// <summary>
/// Internal work items that run one at a time, in post order, off the path
/// of the request that posts them. The queue owns a lifetime token. Dispose
/// cancels the token and waits for the active item. A fault of an item goes
/// to the fault callback and does not stop the queue.
/// </summary>
internal sealed class SerialWorkQueue : IAsyncDisposable
{
    private readonly Channel<Func<CancellationToken, Task>> _items =
        Channel.CreateUnbounded<Func<CancellationToken, Task>>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly CancellationTokenSource _cts = new();
    private readonly Action<Exception> _onFault;
    private readonly object _gate = new();
    private Task? _loop;
    private int _pending;
    private TaskCompletionSource? _idle;
    private bool _disposed;

    public SerialWorkQueue(Action<Exception> onFault)
    {
        ArgumentNullException.ThrowIfNull(onFault);
        _onFault = onFault;
    }

    /// <summary>Puts one item on the queue. Returns false after dispose.</summary>
    public bool TryPost(Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            if (_disposed)
                return false;
            if (!_items.Writer.TryWrite(work))
                return false;
            _pending++;
            _loop ??= Task.Run(RunAsync, CancellationToken.None);
            return true;
        }
    }

    /// <summary>Completes when no item is queued or active.</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate)
        {
            if (_pending == 0)
                return Task.CompletedTask;
            _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _idle.Task;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _items.Writer.TryComplete();
            loop = _loop;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* shutdown */ }
        }

        TaskCompletionSource? idle;
        lock (_gate)
        {
            _pending = 0;
            idle = _idle;
            _idle = null;
        }

        idle?.TrySetResult();
        _cts.Dispose();
    }

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        await foreach (var work in _items.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await work(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                try { _onFault(ex); }
                catch { /* the fault callback must not stop the queue */ }
            }
            finally
            {
                TaskCompletionSource? idle = null;
                lock (_gate)
                {
                    if (_pending > 0)
                        _pending--;
                    if (_pending == 0)
                    {
                        idle = _idle;
                        _idle = null;
                    }
                }

                idle?.TrySetResult();
            }
        }
    }
}
