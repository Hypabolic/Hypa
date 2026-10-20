using System.Threading.Channels;
using Hypa.AgentRuntime.Application;

namespace Hypa.ControlPlane;

/// <summary>
/// Bounded ordered pane input. Admission returns before PTY I/O. One actor
/// owns one runtime and occupant generation, including partial writes.
/// The bound is in bytes, not admissions: a paste arriving as many small
/// <c>pane.send_keys</c> notifications must not be rejected for its count.
/// </summary>
internal sealed class PaneInputActor : IAsyncDisposable
{
    public const int DefaultMaxQueuedBytes = 16 * 1024 * 1024;

    private readonly Channel<AdmittedInput> _channel;
    private readonly Task _loop;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action? _onWriteSucceeded;
    private readonly Action<InputSeqStamp>? _onSeqWriteStarting;
    private readonly Action<InputSeqStamp, bool>? _onSeqWriteEnded;
    private readonly int _maxQueuedBytes;
    private int _queuedBytes;
    private int _closed;
    private int _undeliverable;

    /// <param name="onSeqWriteStarting">Runs just before the PTY write of a batch with a seq.</param>
    /// <param name="onSeqWriteEnded">
    /// Runs once after that write. The flag is true when the write completed.
    /// </param>
    public PaneInputActor(
        IPaneRuntime runtime,
        int occupantGeneration,
        int maxQueuedBytes = DefaultMaxQueuedBytes,
        Action? onWriteSucceeded = null,
        Action<InputSeqStamp>? onSeqWriteStarting = null,
        Action<InputSeqStamp, bool>? onSeqWriteEnded = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        Runtime = runtime;
        OccupantGeneration = occupantGeneration;
        _onWriteSucceeded = onWriteSucceeded;
        _onSeqWriteStarting = onSeqWriteStarting;
        _onSeqWriteEnded = onSeqWriteEnded;
        _maxQueuedBytes = Math.Max(1, maxQueuedBytes);
        _channel = Channel.CreateUnbounded<AdmittedInput>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
    }

    public IPaneRuntime Runtime { get; }

    public int OccupantGeneration { get; }

    public bool IsAccepting => Volatile.Read(ref _closed) == 0;

    public int UndeliverableBytes => Volatile.Read(ref _undeliverable);

    /// <summary>
    /// Test seam: runs after the loop takes an item and before the PTY write.
    /// A test holds a write here to start a capture before the write completes.
    /// </summary>
    internal Func<Task>? BeforeWriteForTests { get; set; }

    /// <summary>
    /// Test seam: runs after the PTY write returns and before the seq record.
    /// A test holds here to let the echo arrive before the record.
    /// </summary>
    internal Func<Task>? AfterWriteForTests { get; set; }

    private void EndSeqWrite(InputSeqStamp stamp, bool written)
    {
        try
        {
            _onSeqWriteEnded?.Invoke(stamp, written);
        }
        catch
        {
            // Measurement and paint signal only. The write result stands.
        }
    }

    /// <param name="keyboard">
    /// True for key presses and typed text. Only then does the write completion
    /// run the write signal that opens the echo window.
    /// </param>
    public bool TryAdmit(ReadOnlyMemory<byte> bytes, InputSeqStamp? seq = null, bool keyboard = true)
    {
        if (bytes.Length == 0)
            return true;
        if (Volatile.Read(ref _closed) != 0)
            return false;
        if (Interlocked.Add(ref _queuedBytes, bytes.Length) > _maxQueuedBytes)
        {
            Interlocked.Add(ref _queuedBytes, -bytes.Length);
            return false;
        }

        if (_channel.Writer.TryWrite(new AdmittedInput(bytes.ToArray(), OccupantGeneration, Barrier: null, seq, keyboard)))
            return true;
        Interlocked.Add(ref _queuedBytes, -bytes.Length);
        return false;
    }

    /// <summary>
    /// Test seam: completes after every input admitted before this call is written
    /// or retired. Admission returns before the write, so a test that counts writes
    /// waits here first.
    /// </summary>
    internal Task WhenDrainedForTestsAsync()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_channel.Writer.TryWrite(new AdmittedInput([], OccupantGeneration, barrier, Seq: null, Keyboard: false)))
            return barrier.Task;

        // Closed: the loop writes or retires what is left, then ends.
        return _loop.ContinueWith(static _ => { }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public void RejectNewAdmission()
    {
        Volatile.Write(ref _closed, 1);
        _channel.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        RejectNewAdmission();
        try { await _cts.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { /* already */ }

        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (IOException) { /* PTY closed */ }
        catch (ObjectDisposedException) { /* runtime gone */ }
        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (item.Barrier is { } barrier)
                {
                    barrier.TrySetResult();
                    continue;
                }

                Interlocked.Add(ref _queuedBytes, -item.Bytes.Length);
                if (item.OccupantGeneration != OccupantGeneration)
                {
                    Interlocked.Add(ref _undeliverable, item.Bytes.Length);
                    continue;
                }

                var seqWriteOpen = false;
                try
                {
                    if (BeforeWriteForTests is { } beforeWrite)
                        await beforeWrite().ConfigureAwait(false);
                    if (item.Seq is { } starting)
                    {
                        // A live capture that overlaps this write is held
                        // back until the end call below records the seq.
                        _onSeqWriteStarting?.Invoke(starting);
                        seqWriteOpen = true;
                    }

                    await Runtime.WriteAsync(item.Bytes, ct).ConfigureAwait(false);
                    if (AfterWriteForTests is { } afterWrite)
                        await afterWrite().ConfigureAwait(false);
                    // The ack comes before the paint token. The paint that the
                    // token makes immediate must read this write as complete.
                    if (item.Seq is { } stamp)
                    {
                        seqWriteOpen = false;
                        EndSeqWrite(stamp, written: true);
                    }

                    try
                    {
                        // Mouse, focus and paste input open no echo window.
                        if (item.Keyboard)
                            _onWriteSucceeded?.Invoke();
                    }
                    catch
                    {
                        // Scheduling signal only. Paint stays on the emit path.
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    Interlocked.Add(ref _undeliverable, item.Bytes.Length);
                    throw;
                }
                catch
                {
                    Interlocked.Add(ref _undeliverable, item.Bytes.Length);
                    throw;
                }
                finally
                {
                    // A failed write still ends, so a held paint is not stuck.
                    if (seqWriteOpen && item.Seq is { } failed)
                        EndSeqWrite(failed, written: false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            while (_channel.Reader.TryRead(out var leftover))
            {
                if (leftover.Barrier is { } barrier)
                {
                    barrier.TrySetResult();
                    continue;
                }

                Interlocked.Add(ref _queuedBytes, -leftover.Bytes.Length);
                Interlocked.Add(ref _undeliverable, leftover.Bytes.Length);
            }
        }
    }

    private readonly record struct AdmittedInput(
        byte[] Bytes,
        int OccupantGeneration,
        TaskCompletionSource? Barrier,
        InputSeqStamp? Seq,
        bool Keyboard);
}
