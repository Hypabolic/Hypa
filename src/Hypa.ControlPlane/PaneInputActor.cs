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

    /// <summary>Upper bound on one coalesced PTY write.</summary>
    public const int MaxCoalescedWriteBytes = 64 * 1024;

    private readonly Channel<AdmittedInput> _channel;
    private readonly Task _loop;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action? _onWriteSucceeded;
    private readonly int _maxQueuedBytes;
    private int _queuedBytes;
    private int _closed;
    private int _undeliverable;

    public PaneInputActor(
        IPaneRuntime runtime,
        int occupantGeneration,
        int maxQueuedBytes = DefaultMaxQueuedBytes,
        Action? onWriteSucceeded = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        Runtime = runtime;
        OccupantGeneration = occupantGeneration;
        _onWriteSucceeded = onWriteSucceeded;
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

    public bool TryAdmit(ReadOnlyMemory<byte> bytes)
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

        if (_channel.Writer.TryWrite(new AdmittedInput(bytes.ToArray(), OccupantGeneration)))
            return true;
        Interlocked.Add(ref _queuedBytes, -bytes.Length);
        return false;
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
        var coalesced = new byte[MaxCoalescedWriteBytes];
        try
        {
            while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_channel.Reader.TryPeek(out _))
                {
                    var write = TakeCoalesced(coalesced);
                    if (write.IsEmpty)
                        continue;

                    try
                    {
                        await Runtime.WriteAsync(write, ct).ConfigureAwait(false);
                        try
                        {
                            _onWriteSucceeded?.Invoke();
                        }
                        catch
                        {
                            // Scheduling signal only. Paint stays on the emit path.
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        Interlocked.Add(ref _undeliverable, write.Length);
                        throw;
                    }
                    catch
                    {
                        Interlocked.Add(ref _undeliverable, write.Length);
                        throw;
                    }
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
                Interlocked.Add(ref _queuedBytes, -leftover.Bytes.Length);
                Interlocked.Add(ref _undeliverable, leftover.Bytes.Length);
            }
        }
    }

    /// <summary>
    /// Dequeue in admission order into one write of at most
    /// <see cref="MaxCoalescedWriteBytes"/>; an oversized admission is
    /// written alone. Stale-generation input is counted undeliverable.
    /// </summary>
    private ReadOnlyMemory<byte> TakeCoalesced(byte[] scratch)
    {
        var filled = 0;
        while (_channel.Reader.TryPeek(out var next))
        {
            if (next.OccupantGeneration != OccupantGeneration)
            {
                _channel.Reader.TryRead(out _);
                Interlocked.Add(ref _queuedBytes, -next.Bytes.Length);
                Interlocked.Add(ref _undeliverable, next.Bytes.Length);
                continue;
            }

            if (filled == 0 && next.Bytes.Length >= scratch.Length)
            {
                _channel.Reader.TryRead(out _);
                Interlocked.Add(ref _queuedBytes, -next.Bytes.Length);
                return next.Bytes;
            }

            if (filled + next.Bytes.Length > scratch.Length)
                break;

            _channel.Reader.TryRead(out _);
            Interlocked.Add(ref _queuedBytes, -next.Bytes.Length);
            next.Bytes.CopyTo(scratch, filled);
            filled += next.Bytes.Length;
        }

        return scratch.AsMemory(0, filled);
    }

    private readonly record struct AdmittedInput(byte[] Bytes, int OccupantGeneration);
}
