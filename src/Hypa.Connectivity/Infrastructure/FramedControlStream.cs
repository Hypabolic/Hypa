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
    // The bytes of the line that the last write did not end. A write scans
    // its own bytes once, and copies each line one time into its frame.
    private byte[] _writePending = [];
    private int _writePendingLength;
    // A line made from pending bytes and a new write, for a session that
    // copies the payload on send.
    private byte[] _lineBuffer = [];
    // The received line that a read did not take yet. The newline follows
    // it when the frame did not end with one.
    private ReadOnlyMemory<byte> _unread;
    private bool _unreadNewline;
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

                QueueControlLine(frame.Payload);
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
            var start = 0;
            var borrow = _session.CopiesPayloadOnSend;
            while (TryTakeLine(buffer, ref start, borrow, out var line))
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

            AppendPending(buffer.Span[start..]);
            if (_writePendingLength > StreamLimits.MaxControlLineBytes)
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
        var take = Math.Min(_unread.Length, destination.Length);
        _unread[..take].CopyTo(destination);
        _unread = _unread[take..];
        if (_unread.IsEmpty && _unreadNewline && take < destination.Length)
        {
            destination.Span[take++] = (byte)'\n';
            _unreadNewline = false;
        }

        return take;
    }

    // The frame payload is the received line. A received frame is not
    // written again, so the read uses it without a copy.
    private void QueueControlLine(ReadOnlyMemory<byte> payload)
    {
        _unread = payload;
        _unreadNewline = payload.IsEmpty || payload.Span[^1] != (byte)'\n';
    }

    /// <summary>
    /// Takes the next full line: the pending bytes, then the bytes of
    /// <paramref name="chunk"/> from <paramref name="start"/> to the next
    /// newline. A CR before the newline is not part of the line, also when
    /// the CR ended the previous write. With <paramref name="borrow"/>, the
    /// line is a slice of the chunk or of a buffer that the next line uses
    /// again; the session must copy it on send.
    /// </summary>
    private bool TryTakeLine(ReadOnlyMemory<byte> chunk, ref int start, bool borrow, out ReadOnlyMemory<byte> line)
    {
        var newline = chunk.Span[start..].IndexOf((byte)'\n');
        if (newline < 0)
        {
            line = ReadOnlyMemory<byte>.Empty;
            return false;
        }

        var tail = chunk.Slice(start, newline);
        var length = _writePendingLength + tail.Length;
        var lastByte = tail.Length > 0
            ? tail.Span[^1]
            : _writePendingLength > 0 ? _writePending[_writePendingLength - 1] : (byte)0;
        if (length > 0 && lastByte == (byte)'\r')
            length--;

        var fromPending = Math.Min(_writePendingLength, length);
        if (borrow && fromPending == 0)
        {
            line = tail[..length];
        }
        else
        {
            byte[] target;
            if (borrow)
            {
                if (_lineBuffer.Length < length)
                    _lineBuffer = new byte[Math.Max(length, _lineBuffer.Length * 2)];
                target = _lineBuffer;
            }
            else
            {
                target = new byte[length];
            }

            _writePending.AsSpan(0, fromPending).CopyTo(target);
            tail.Span[..(length - fromPending)].CopyTo(target.AsSpan(fromPending));
            line = target.AsMemory(0, length);
        }

        _writePendingLength = 0;
        start += newline + 1;
        return true;
    }

    private void AppendPending(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;
        var needed = _writePendingLength + bytes.Length;
        if (needed > _writePending.Length)
            Array.Resize(ref _writePending, Math.Max(needed, Math.Max(256, _writePending.Length * 2)));
        bytes.CopyTo(_writePending.AsSpan(_writePendingLength));
        _writePendingLength = needed;
    }
}
