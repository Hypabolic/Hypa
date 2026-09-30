using System.Threading.Channels;

namespace Hypa.Cli.Attach.Input;

/// <summary>
// / One blocking stdin thread.
// / forwards raw bytes to the main loop.
/// <c>src/client/mod.rs:445-474</c> launches that reader with
/// <c>std::thread::spawn</c>. Do not poll stdin on the attach
/// supervision thread.
/// </summary>
internal sealed class AttachStdinProducer : IAttachStdinSource
{
    internal const int PollCancelMs = 50;

    private readonly int _fd;
    private readonly Channel<byte[]?> _channel;
    private readonly CancellationTokenSource _stop;
    private readonly Thread _thread;
    private readonly Func<int, int, bool> _poll;
    private readonly Func<int, byte[], int, int> _read;
    private int _disposed;

    private AttachStdinProducer(
        int fd,
        CancellationToken ct,
        Func<int, int, bool> poll,
        Func<int, byte[], int, int> read)
    {
        _fd = fd;
        _poll = poll;
        _read = read;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _channel = Channel.CreateBounded<byte[]?>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _thread = new Thread(ReadLoop)
        {
            Name = "hypa-attach-stdin",
            IsBackground = true,
        };
    }

    public bool ThreadAlive => _thread.IsAlive;

    public static AttachStdinProducer Start(
        int fd,
        CancellationToken ct,
        Func<int, int, bool>? pollReadable = null,
        Func<int, byte[], int, int>? tryRead = null)
    {
        var producer = new AttachStdinProducer(
            fd,
            ct,
            pollReadable ?? UnixRawTerminal.PollReadable,
            tryRead ?? ((file, buf, offset) => UnixRawTerminal.TryRead(file, buf, offset)));
        producer._thread.Start();
        return producer;
    }

    public async ValueTask<AttachStdinRead> ReadAsync(
        byte[] buffer,
        int timeoutMs,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs <= 0)
            timeout.Cancel();
        else
            timeout.CancelAfter(timeoutMs);

        try
        {
            while (await _channel.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false))
            {
                if (!_channel.Reader.TryRead(out var chunk))
                    continue;
                if (chunk is null)
                    return AttachStdinRead.End;
                var n = Math.Min(chunk.Length, buffer.Length);
                chunk.AsSpan(0, n).CopyTo(buffer);
                return AttachStdinRead.Bytes(n);
            }

            return AttachStdinRead.End;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return AttachStdinRead.Timeout;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        try { _stop.Cancel(); }
        catch (ObjectDisposedException)
        {
        }

        _channel.Writer.TryComplete();
        if (_thread.IsAlive)
            _thread.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    private void ReadLoop()
    {
        var buf = new byte[4096];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (!_poll(_fd, PollCancelMs))
                    continue;

                var n = _read(_fd, buf, 0);
                if (n < 0)
                    continue;
                if (n == 0)
                {
                    _channel.Writer.TryWrite(null);
                    _channel.Writer.TryComplete();
                    return;
                }

                var filled = DrainAvailable(buf, n);
                var copy = new byte[filled];
                Buffer.BlockCopy(buf, 0, copy, 0, filled);
                if (!_channel.Writer.TryWrite(copy))
                {
                    _channel.Writer.WriteAsync(copy, _stop.Token).AsTask()
                        .GetAwaiter().GetResult();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _channel.Writer.TryComplete(ex);
            return;
        }

        _channel.Writer.TryComplete();
    }

    private int DrainAvailable(byte[] buf, int filled)
    {
        while (filled < buf.Length && _poll(_fd, 0))
        {
            var n = _read(_fd, buf, filled);
            if (n <= 0)
                break;
            filled += n;
        }

        return filled;
    }
}
