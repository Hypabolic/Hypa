using System.Threading.Channels;
using Hypa.AgentRuntime.Application;

namespace Hypa.ControlPlane;

/// <summary>
/// Bounded ordered pane input. Admission returns before PTY I/O. One actor
/// owns one runtime and occupant generation, including partial writes.
/// </summary>
internal sealed class PaneInputActor : IAsyncDisposable
{
    public const int DefaultCapacity = 1024;

    private readonly Channel<AdmittedInput> _channel;
    private readonly Task _loop;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action? _onWriteSucceeded;
    private int _closed;
    private int _undeliverable;

    public PaneInputActor(
        IPaneRuntime runtime,
        int occupantGeneration,
        int capacity = DefaultCapacity,
        Action? onWriteSucceeded = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        Runtime = runtime;
        OccupantGeneration = occupantGeneration;
        _onWriteSucceeded = onWriteSucceeded;
        _channel = Channel.CreateBounded<AdmittedInput>(new BoundedChannelOptions(Math.Max(1, capacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
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
        var copy = bytes.ToArray();
        return _channel.Writer.TryWrite(new AdmittedInput(copy, OccupantGeneration));
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
                Interlocked.Add(ref _undeliverable, leftover.Bytes.Length);
        }
    }

    private readonly record struct AdmittedInput(byte[] Bytes, int OccupantGeneration);
}
