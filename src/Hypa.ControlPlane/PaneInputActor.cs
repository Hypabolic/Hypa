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
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Interlocked.Add(ref _queuedBytes, -item.Bytes.Length);
                if (item.OccupantGeneration != OccupantGeneration)
                {
                    Interlocked.Add(ref _undeliverable, item.Bytes.Length);
                    continue;
                }

                try
                {
                    await Runtime.WriteAsync(item.Bytes, ct).ConfigureAwait(false);
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
                    Interlocked.Add(ref _undeliverable, item.Bytes.Length);
                    throw;
                }
                catch
                {
                    Interlocked.Add(ref _undeliverable, item.Bytes.Length);
                    throw;
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

    private readonly record struct AdmittedInput(byte[] Bytes, int OccupantGeneration);
}
