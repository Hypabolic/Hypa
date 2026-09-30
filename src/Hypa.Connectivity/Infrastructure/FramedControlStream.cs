using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// Byte stream of NDJSON control frames. Heartbeat, flow-control, and binary
/// frames stay off this stream. This is not reconnect.
/// </summary>
public sealed class FramedControlStream : Stream
{
    private readonly IFramedSession _session;
    private readonly bool _ownsSession;
    private readonly StreamDirection _outgoing;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly SemaphoreSlim _read = new(1, 1);
    private readonly List<byte> _writePending = [];
    private byte[]? _unread;
    private int _unreadOffset;
    private int _unreadLength;
    private int _disposed;

    public FramedControlStream(IFramedSession session, bool ownsSession = true)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _ownsSession = ownsSession;
        _outgoing = StreamFrameRules.Outgoing(session.Binding.Role);
    }

    public JoinBinding Binding => _session.Binding;

    public override bool CanRead => Volatile.Read(ref _disposed) == 0;

    public override bool CanSeek => false;

    public override bool CanWrite => Volatile.Read(ref _disposed) == 0;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (buffer.Length == 0)
            return 0;

        await _read.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var copied = CopyUnread(buffer);
            if (copied > 0)
                return copied;

            while (copied == 0)
            {
                var received = await _session.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (!received.Ok || received.Value is null)
                    return 0;

                var frame = received.Value;
                if (!frame.IsControl || frame.PayloadLength == 0)
                    continue;

                QueueControlLine(frame.Payload.Span);
                copied = CopyUnread(buffer);
            }

            return copied;
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
        finally
        {
            _read.Release();
        }
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (buffer.Length == 0)
            return;

        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _writePending.AddRange(buffer.ToArray());
            while (TryTakeLine(out var line))
            {
                if (line.Length > StreamLimits.MaxControlLineBytes)
                    throw new IOException("NDJSON line exceeded maximum length.");

                var sent = await _session.SendAsync(
                        StreamFrame.Control(_outgoing, 0, line),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!sent.Ok)
                {
                    throw new IOException(
                        sent.Detail ?? sent.Reason ?? ConnectivityReasons.PeerUnavailable);
                }
            }

            if (_writePending.Count > StreamLimits.MaxControlLineBytes)
                throw new IOException("NDJSON line exceeded maximum length.");
        }
        finally
        {
            _write.Release();
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            DisposeCoreAsync().AsTask().GetAwaiter().GetResult();

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await DisposeCoreAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask DisposeCoreAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_ownsSession)
            await _session.DisposeAsync().ConfigureAwait(false);
    }

    private int CopyUnread(Memory<byte> destination)
    {
        if (_unread is null || _unreadOffset >= _unreadLength)
            return 0;

        var available = _unreadLength - _unreadOffset;
        var take = Math.Min(available, destination.Length);
        _unread.AsSpan(_unreadOffset, take).CopyTo(destination.Span);
        _unreadOffset += take;
        if (_unreadOffset >= _unreadLength)
        {
            _unread = null;
            _unreadOffset = 0;
            _unreadLength = 0;
        }

        return take;
    }

    private void QueueControlLine(ReadOnlySpan<byte> payload)
    {
        var needNewline = payload.Length == 0 || payload[^1] != (byte)'\n';
        var length = payload.Length + (needNewline ? 1 : 0);
        var buffer = new byte[length];
        payload.CopyTo(buffer);
        if (needNewline)
            buffer[payload.Length] = (byte)'\n';

        _unread = buffer;
        _unreadOffset = 0;
        _unreadLength = length;
    }

    private bool TryTakeLine(out byte[] line)
    {
        var newline = _writePending.IndexOf((byte)'\n');
        if (newline < 0)
        {
            line = [];
            return false;
        }

        var length = newline;
        if (length > 0 && _writePending[length - 1] == (byte)'\r')
            length--;

        line = _writePending.GetRange(0, length).ToArray();
        _writePending.RemoveRange(0, newline + 1);
        return true;
    }
}
