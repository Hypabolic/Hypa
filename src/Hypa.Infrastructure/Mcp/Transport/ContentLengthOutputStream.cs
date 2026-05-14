using System.Text;

namespace Hypa.Infrastructure.Mcp.Transport;

/// <summary>
/// Adapts the MCP C# SDK's newline-delimited JSON output to the
/// Content-Length framing required by the MCP spec and Claude Code's client.
///
/// The SDK writes: "{JSON bytes}\n"
/// This stream writes: "Content-Length: N\r\n\r\n{N bytes of JSON}"
/// </summary>
internal sealed class ContentLengthOutputStream : Stream
{
    private readonly Stream _inner;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly MemoryStream _lineBuffer = new();

    internal ContentLengthOutputStream(Stream inner) => _inner = inner;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lineBuffer.Dispose();
            _writeLock.Dispose();
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        // Locate newline before acquiring the lock so we can work with Memory<T> across await.
        var nlIdx = buffer.Span.IndexOf((byte)'\n');

        await _writeLock.WaitAsync(ct);
        try
        {
            if (nlIdx < 0)
            {
                _lineBuffer.Write(buffer.Span);
                return;
            }

            // Write everything before '\n', flush the frame, then buffer the remainder.
            _lineBuffer.Write(buffer[..nlIdx].Span);
            await FlushFrameAsync(ct);

            if (nlIdx + 1 < buffer.Length)
                _lineBuffer.Write(buffer[(nlIdx + 1)..].Span);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public override async Task FlushAsync(CancellationToken ct) =>
        await _inner.FlushAsync(ct);

    private async Task FlushFrameAsync(CancellationToken ct)
    {
        var payload = _lineBuffer.ToArray();
        _lineBuffer.SetLength(0);
        _lineBuffer.Position = 0;

        if (payload.Length == 0)
            return;

        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        await _inner.WriteAsync(header.AsMemory(), ct);
        await _inner.WriteAsync(payload.AsMemory(), ct);
        await _inner.FlushAsync(ct);
    }

    // Stream boilerplate
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
}
