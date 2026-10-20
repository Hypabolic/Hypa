using System.Buffers;
using System.Text;

namespace Hypa.ControlPlane.Unix;

public enum BoundedNdjsonReadStatus
{
    Line = 0,
    EndOfStream = 1,
    Overflow = 2,
}

public readonly record struct BoundedNdjsonReadResult
{
    public BoundedNdjsonReadStatus Status { get; init; }

    public string? Line { get; init; }

    /// <summary>UTF-8 byte length of <see cref="Line"/>, excluding the newline.</summary>
    public int ByteLength { get; init; }

    public bool IsLine => Status == BoundedNdjsonReadStatus.Line;

    public bool IsEndOfStream => Status == BoundedNdjsonReadStatus.EndOfStream;

    public bool IsOverflow => Status == BoundedNdjsonReadStatus.Overflow;

    public static BoundedNdjsonReadResult FromLine(string line) =>
        FromLine(line, Encoding.UTF8.GetByteCount(line));

    public static BoundedNdjsonReadResult FromLine(string line, int byteLength) =>
        new()
        {
            Status = BoundedNdjsonReadStatus.Line,
            Line = line,
            ByteLength = byteLength,
        };

    public static BoundedNdjsonReadResult EndOfStream() =>
        new() { Status = BoundedNdjsonReadStatus.EndOfStream };

    public static BoundedNdjsonReadResult Overflow() =>
        new() { Status = BoundedNdjsonReadStatus.Overflow };
}

/// <summary>
/// The UTF-8 bytes of one NDJSON line in a buffer from a pool. The newline
/// is not included. The owner calls <see cref="Return"/> one time when it no
/// longer reads the bytes. A second call does nothing, so two copies of a
/// reference cannot give the buffer back to the pool two times.
/// </summary>
internal sealed class RentedUtf8Line
{
    private readonly ArrayPool<byte>? _pool;
    private byte[]? _buffer;

    private RentedUtf8Line(byte[] buffer, int length, ArrayPool<byte>? pool)
    {
        _buffer = buffer;
        Length = length;
        _pool = pool;
    }

    /// <summary>Copies <paramref name="line"/> into a buffer from <paramref name="pool"/>.</summary>
    public static RentedUtf8Line Rent(ArrayPool<byte> pool, ReadOnlySpan<byte> line)
    {
        ArgumentNullException.ThrowIfNull(pool);
        var buffer = pool.Rent(Math.Max(1, line.Length));
        line.CopyTo(buffer);
        return new RentedUtf8Line(buffer, line.Length, pool);
    }

    /// <summary>Wraps bytes that no pool owns. <see cref="Return"/> only drops the reference.</summary>
    public static RentedUtf8Line Unpooled(byte[] utf8, int length)
    {
        ArgumentNullException.ThrowIfNull(utf8);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, utf8.Length);
        return new RentedUtf8Line(utf8, length, pool: null);
    }

    /// <summary>The UTF-8 byte length of the line.</summary>
    public int Length { get; }

    /// <summary>True after <see cref="Return"/>.</summary>
    public bool IsReturned => Volatile.Read(ref _buffer) is null;

    /// <summary>The line bytes. Valid only until <see cref="Return"/>.</summary>
    public ReadOnlySpan<byte> Span =>
        (Volatile.Read(ref _buffer) ?? throw new ObjectDisposedException(nameof(RentedUtf8Line)))
            .AsSpan(0, Length);

    /// <summary>The line bytes. Valid only until <see cref="Return"/>.</summary>
    public ReadOnlyMemory<byte> Memory =>
        (Volatile.Read(ref _buffer) ?? throw new ObjectDisposedException(nameof(RentedUtf8Line)))
            .AsMemory(0, Length);

    public void Return()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
            _pool?.Return(buffer);
    }
}

/// <summary>The result of <see cref="BoundedNdjsonLineReader.ReadLineUtf8Async"/>.</summary>
internal readonly record struct BoundedNdjsonUtf8ReadResult
{
    public BoundedNdjsonReadStatus Status { get; init; }

    /// <summary>The line. The caller owns it and returns it to the pool.</summary>
    public RentedUtf8Line? Line { get; init; }

    public bool IsEndOfStream => Status == BoundedNdjsonReadStatus.EndOfStream;

    public bool IsOverflow => Status == BoundedNdjsonReadStatus.Overflow;
}

/// <summary>
/// Read one NDJSON line into a buffer that starts small and grows on demand.
/// <see cref="MaxLineBytes"/> is the limit that rejects an oversized line.
/// It is not a preallocation.
/// max_frame_size, then allocates exactly claimed_len; the limit is not
/// a preallocation. Hypa NDJSON has no length prefix, so Span.IndexOf
/// plus a scan cursor is that sequence (wire.rs:1564-1598 read_message
/// uses claimed_len and read_exact_or_eof; it does not rescan a prefix
/// after a partial fill).
/// </summary>
public sealed class BoundedNdjsonLineReader
{
    /// <summary>
    // / First allocation for one connection.
    /// checks the claimed length against max_frame_size, then allocates that
    /// length. The limit is not a preallocation.
    /// </summary>
    public const int InitialBufferBytes = 16 * 1024;

    private readonly Stream _stream;
    private byte[] _buffer;
    private readonly int _maxLineBytes;
    private int _start;
    private int _end;
    private int _scan;
    private int _newlineScanBytes;

    public BoundedNdjsonLineReader(Stream stream, int maxLineBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maxLineBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxLineBytes), "Max line size must be at least 1.");

        _stream = stream;
        _maxLineBytes = maxLineBytes;
        _buffer = new byte[Math.Min(maxLineBytes, InitialBufferBytes)];
    }

    public int MaxLineBytes => _maxLineBytes;

    /// <summary>Current buffer capacity. Starts at 16 KiB and doubles toward <see cref="MaxLineBytes"/>.</summary>
    public int BufferCapacity => _buffer.Length;

    /// <summary>True when a complete line is already in the buffer (no socket read).</summary>
    public bool HasBufferedLine => IndexOfNewline() >= 0;

    /// <summary>First unread byte that has not been searched for a newline.</summary>
    internal int ScanCursor => _scan;

    /// <summary>Bytes passed to newline IndexOf. Tests prove no prefix rescan.</summary>
    internal int NewlineScanBytes => _newlineScanBytes;

    public async ValueTask<BoundedNdjsonReadResult> ReadLineAsync(CancellationToken ct = default)
    {
        var next = await NextLineAsync(ct).ConfigureAwait(false);
        if (next.Status != BoundedNdjsonReadStatus.Line)
            return new BoundedNdjsonReadResult { Status = next.Status };

        var text = next.Length == 0 ? string.Empty : Encoding.UTF8.GetString(_buffer, next.From, next.Length);
        return BoundedNdjsonReadResult.FromLine(text, next.Length);
    }

    /// <summary>
    /// Reads one line as UTF-8 bytes in a buffer from <paramref name="pool"/>.
    /// The reader does not make a string. The caller owns the line and must
    /// return it to the pool. The limit and the overflow rules are the same
    /// as for <see cref="ReadLineAsync"/>.
    /// </summary>
    internal async ValueTask<BoundedNdjsonUtf8ReadResult> ReadLineUtf8Async(
        ArrayPool<byte> pool,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pool);
        var next = await NextLineAsync(ct).ConfigureAwait(false);
        if (next.Status != BoundedNdjsonReadStatus.Line)
            return new BoundedNdjsonUtf8ReadResult { Status = next.Status };

        return new BoundedNdjsonUtf8ReadResult
        {
            Status = BoundedNdjsonReadStatus.Line,
            Line = RentedUtf8Line.Rent(pool, _buffer.AsSpan(next.From, next.Length)),
        };
    }

    /// <summary>
    /// The place of one line in <see cref="_buffer"/>, without the newline
    /// and a CR before it. The bytes stay valid until the next read.
    /// </summary>
    private readonly record struct LineRange(BoundedNdjsonReadStatus Status, int From, int Length);

    private async ValueTask<LineRange> NextLineAsync(CancellationToken ct)
    {
        while (true)
        {
            var nl = IndexOfNewline();
            if (nl >= 0)
                return TakeLine(nl);

            Compact();
            if (_end - _start >= _buffer.Length && !TryGrow())
            {
                await SkipUntilNewlineAsync(ct).ConfigureAwait(false);
                return new LineRange(BoundedNdjsonReadStatus.Overflow, 0, 0);
            }

            var n = await _stream.ReadAsync(_buffer.AsMemory(_end, _buffer.Length - _end), ct)
                .ConfigureAwait(false);
            if (n == 0)
            {
                if (_end == _start)
                    return new LineRange(BoundedNdjsonReadStatus.EndOfStream, 0, 0);

                // EOF without a newline: emit the leftover (StreamReader-compatible).
                // The indices reset, but the bytes stay in place until the next read.
                var leftover = LineOf(_start, _end);
                _start = 0;
                _end = 0;
                _scan = 0;
                return leftover;
            }

            _end += n;
        }
    }

    private int IndexOfNewline()
    {
        var from = _scan < _start ? _start : _scan;
        var length = _end - from;
        if (length <= 0)
            return -1;

        _newlineScanBytes += length;
        var relative = _buffer.AsSpan(from, length).IndexOf((byte)'\n');
        if (relative < 0)
        {
            _scan = _end;
            return -1;
        }

        _scan = from + relative;
        return _scan;
    }

    private LineRange TakeLine(int newlineIndex)
    {
        var line = LineOf(_start, newlineIndex);
        _start = newlineIndex + 1;
        _scan = _start;
        if (_start == _end)
        {
            _start = 0;
            _end = 0;
            _scan = 0;
        }

        return line;
    }

    private LineRange LineOf(int from, int to)
    {
        var len = to - from;
        if (len > 0 && _buffer[to - 1] == (byte)'\r')
            len--;
        return new LineRange(BoundedNdjsonReadStatus.Line, from, len);
    }

    private void Compact()
    {
        if (_start == 0)
            return;

        var remaining = _end - _start;
        if (remaining > 0)
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, remaining);
        _scan = Math.Max(0, _scan - _start);
        _end = remaining;
        _start = 0;
    }

    /// <summary>
    /// Double the buffer when a line does not fit, and stop at the limit.
    /// Compact() runs first, so _start is 0 and the indices stay valid.
    /// The scan cursor keeps its value, so the next IndexOf starts where the
    // / last one stopped.
    /// message size and keeps max_frame_size as the limit.
    /// </summary>
    private bool TryGrow()
    {
        var capacity = _buffer.Length;
        if (capacity >= _maxLineBytes)
            return false;

        var next = capacity > _maxLineBytes / 2 ? _maxLineBytes : capacity * 2;
        var grown = new byte[next];
        Buffer.BlockCopy(_buffer, 0, grown, 0, _end);
        _buffer = grown;
        return true;
    }

    /// <summary>
    /// Discard the rest of an oversized line, including a later newline.
    /// Keep bytes after that newline for the next read.
    /// without allocating an oversized buffer.
    /// </summary>
    private async ValueTask SkipUntilNewlineAsync(CancellationToken ct)
    {
        _start = 0;
        _end = 0;
        _scan = 0;
        while (true)
        {
            var n = await _stream.ReadAsync(_buffer.AsMemory(), ct).ConfigureAwait(false);
            if (n == 0)
                return;

            var nl = _buffer.AsSpan(0, n).IndexOf((byte)'\n');
            if (nl < 0)
                continue;

            var leftover = n - nl - 1;
            if (leftover > 0)
                Buffer.BlockCopy(_buffer, nl + 1, _buffer, 0, leftover);
            _start = 0;
            _end = leftover;
            _scan = 0;
            return;
        }
    }
}
