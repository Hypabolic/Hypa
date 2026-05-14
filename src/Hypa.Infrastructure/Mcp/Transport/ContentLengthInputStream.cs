using System.Text;

namespace Hypa.Infrastructure.Mcp.Transport;

/// <summary>
/// Adapts a Content-Length-framed stdin (MCP spec / Claude Code client) to
/// the newline-delimited JSON format expected by the MCP C# SDK's StreamServerTransport.
///
/// Each incoming message is: "Content-Length: N\r\n\r\n{N bytes of JSON}"
/// Each outgoing read to the SDK yields:  "{N bytes of JSON}\n"
/// </summary>
internal sealed class ContentLengthInputStream : Stream
{
    private readonly Stream _inner;
    private byte[] _pending = [];
    private int _pendingPos;

    internal ContentLengthInputStream(Stream inner) => _inner = inner;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_pendingPos < _pending.Length)
            return Drain(buffer);

        var payload = await ReadNextFrameAsync(ct);
        if (payload is null)
            return 0; // EOF

        // Expose payload + '\n' as the next JSON line
        _pending = new byte[payload.Length + 1];
        payload.CopyTo(_pending, 0);
        _pending[payload.Length] = (byte)'\n';
        _pendingPos = 0;

        return Drain(buffer);
    }

    private int Drain(Memory<byte> buffer)
    {
        var count = Math.Min(buffer.Length, _pending.Length - _pendingPos);
        _pending.AsMemory(_pendingPos, count).CopyTo(buffer);
        _pendingPos += count;
        return count;
    }

    private async Task<byte[]?> ReadNextFrameAsync(CancellationToken ct)
    {
        int contentLength = -1;

        while (true)
        {
            var line = await ReadHeaderLineAsync(ct);
            if (line is null)
                return null; // EOF before any header

            if (line.Length == 0)
                break; // blank line → end of headers

            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line["Content-Length:".Length..].Trim(), out var n))
            {
                contentLength = n;
            }
            // Ignore unrecognised headers (e.g. Content-Type)
        }

        if (contentLength <= 0)
            return [];

        var body = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            var chunk = await _inner.ReadAsync(body.AsMemory(read, contentLength - read), ct);
            if (chunk == 0)
                break; // premature EOF: return what we have
            read += chunk;
        }

        return body;
    }

    // Reads one header line, stripping the trailing \r.  Returns null on EOF.
    private async Task<string?> ReadHeaderLineAsync(CancellationToken ct)
    {
        var buf = new List<byte>(128);
        var oneByte = new byte[1];

        while (true)
        {
            var read = await _inner.ReadAsync(oneByte.AsMemory(), ct);
            if (read == 0)
                return buf.Count == 0 ? null : Encoding.ASCII.GetString([.. buf]).TrimEnd('\r');

            if (oneByte[0] == '\n')
                return Encoding.ASCII.GetString([.. buf]).TrimEnd('\r');

            buf.Add(oneByte[0]);
        }
    }

    // Stream boilerplate
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
