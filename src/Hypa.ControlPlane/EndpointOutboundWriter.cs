using System.Text;
using System.Threading.Channels;

namespace Hypa.ControlPlane;

/// <summary>
/// Encode has already produced one frame. <c>try_send</c> accepts it onto this
/// connection's writer. The socket write runs on the worker. One queue lives
/// for the life of the connection.
/// </summary>
internal sealed class EndpointOutboundWriter
{
    internal const int MaxQueuedMessages = 256;

    internal const int MaxQueuedBytes = 2 * 32 * 1024 * 1024;

    private readonly Channel<OutboundFrame> _frames;
    private readonly Func<string, CancellationToken, Task> _writeLine;
    private readonly object _budget = new();
    private int _queuedBytes;
    private int _queuedMessages;
    private int _pendingWrites;
    private int _stopped;
    private int _workerStarted;
    private TaskCompletionSource? _hold;

    internal EndpointOutboundWriter(Func<string, CancellationToken, Task> writeLine)
    {
        _writeLine = writeLine;
        _frames = Channel.CreateBounded<OutboundFrame>(new BoundedChannelOptions(MaxQueuedMessages)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    internal bool IsStopped => Volatile.Read(ref _stopped) != 0;

    internal int PendingWrites => Volatile.Read(ref _pendingWrites);

    internal void HoldWrites()
    {
        lock (_budget)
            _hold ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal void ReleaseWrites()
    {
        TaskCompletionSource? hold;
        lock (_budget)
        {
            hold = _hold;
            _hold = null;
        }

        hold?.TrySetResult();
    }

    // Returns when the frame is queued.
    /// A full queue or a stopped writer fails here. The caller does not wait
    /// for the socket write or for a reply.
    internal bool TrySend(string line, TaskCompletionSource<string> pending, out string error)
    {
        if (Volatile.Read(ref _stopped) != 0)
        {
            error = "endpoint writer stopped";
            return false;
        }

        var len = Encoding.UTF8.GetByteCount(line);
        lock (_budget)
        {
            if (_stopped != 0)
            {
                error = "endpoint writer stopped";
                return false;
            }

            var nextBytes = (long)_queuedBytes + len;
            if (_queuedMessages >= MaxQueuedMessages || nextBytes > MaxQueuedBytes)
            {
                error = "endpoint output queue is full";
                return false;
            }

            if (!_frames.Writer.TryWrite(new OutboundFrame(line, len, pending)))
            {
                error = "endpoint writer stopped";
                return false;
            }

            _queuedMessages++;
            _queuedBytes = (int)nextBytes;
            _pendingWrites++;
        }

        EnsureWorker();
        error = string.Empty;
        return true;
    }

    internal void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;
        ReleaseWrites();
        _frames.Writer.TryComplete();
    }

    private void EnsureWorker()
    {
        if (Interlocked.Exchange(ref _workerStarted, 1) != 0)
            return;
        _ = Task.Factory.StartNew(
                LoopAsync,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();
    }

    private async Task LoopAsync()
    {
        try
        {
            while (await _frames.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_frames.Reader.TryRead(out var frame))
                    await WriteFrameAsync(frame).ConfigureAwait(false);
            }
        }
        catch (ChannelClosedException)
        {
        }
    }

    private async Task WriteFrameAsync(OutboundFrame frame)
    {
        ReleaseBudget(frame.Length);
        try
        {
            await WaitHoldAsync().ConfigureAwait(false);
            if (Volatile.Read(ref _stopped) != 0)
            {
                frame.Pending.TrySetException(new IOException("endpoint writer stopped"));
                return;
            }

            try
            {
                using var writeBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _writeLine(frame.Line, writeBudget.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _stopped, 1);
                frame.Pending.TrySetException(ex is OperationCanceledException
                    ? new IOException("endpoint write timed out")
                    : ex);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pendingWrites);
        }
    }

    private void ReleaseBudget(int length)
    {
        lock (_budget)
        {
            _queuedMessages = Math.Max(0, _queuedMessages - 1);
            _queuedBytes = Math.Max(0, _queuedBytes - length);
        }
    }

    private async Task WaitHoldAsync()
    {
        Task? hold;
        lock (_budget)
            hold = _hold?.Task;
        if (hold is not null)
            await hold.ConfigureAwait(false);
    }

    private readonly record struct OutboundFrame(string Line, int Length, TaskCompletionSource<string> Pending);
}
