using System.Text;
using Hypa.Infrastructure.Mcp.Transport;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Mcp;

public sealed class ContentLengthTransportTests
{
    // ── ContentLengthInputStream ────────────────────────────────────────────

    private static Stream MakeInput(string raw) =>
        new MemoryStream(Encoding.UTF8.GetBytes(raw));

    private static async Task<string> ReadLineAsync(ContentLengthInputStream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        return (await reader.ReadLineAsync())!;
    }

    [Fact]
    public async Task Input_SingleFrame_ReturnsPayload()
    {
        const string payload = """{"jsonrpc":"2.0","id":1,"method":"ping"}""";
        var frame = $"Content-Length: {Encoding.UTF8.GetByteCount(payload)}\r\n\r\n{payload}";
        using var stream = new ContentLengthInputStream(MakeInput(frame));

        var line = await ReadLineAsync(stream);
        Assert.Equal(payload, line);
    }

    [Fact]
    public async Task Input_MultipleFrames_ReturnsAllPayloads()
    {
        const string p1 = """{"id":1}""";
        const string p2 = """{"id":2}""";
        var raw = $"Content-Length: {p1.Length}\r\n\r\n{p1}Content-Length: {p2.Length}\r\n\r\n{p2}";
        using var stream = new ContentLengthInputStream(MakeInput(raw));

        var line1 = await ReadLineAsync(stream);
        var line2 = await ReadLineAsync(stream);
        Assert.Equal(p1, line1);
        Assert.Equal(p2, line2);
    }

    [Fact]
    public async Task Input_ExtraHeadersIgnored_ReturnsPayload()
    {
        const string payload = """{"method":"ping"}""";
        var frame = $"Content-Type: application/json\r\nContent-Length: {payload.Length}\r\n\r\n{payload}";
        using var stream = new ContentLengthInputStream(MakeInput(frame));

        var line = await ReadLineAsync(stream);
        Assert.Equal(payload, line);
    }

    [Fact]
    public async Task Input_ZeroContentLength_ReturnsEmptyLine()
    {
        const string frame = "Content-Length: 0\r\n\r\n";
        using var stream = new ContentLengthInputStream(MakeInput(frame));

        // Should produce an empty line (or EOF) rather than hanging/throwing
        var buf = new byte[16];
        var read = await stream.ReadAsync(buf.AsMemory());
        // Empty payload + '\n' = 1 byte
        Assert.True(read is 0 or 1);
    }

    [Fact]
    public async Task Input_NoContentLengthHeader_ReturnsEmpty()
    {
        // No Content-Length header — body is 0 bytes, produces empty line
        const string frame = "Content-Type: application/json\r\n\r\nignored_body";
        using var stream = new ContentLengthInputStream(MakeInput(frame));

        var buf = new byte[256];
        var read = await stream.ReadAsync(buf.AsMemory());
        Assert.True(read is 0 or 1); // '\n' from empty payload
    }

    [Fact]
    public async Task Input_EofMidFrame_DoesNotThrow()
    {
        // Truncated frame — should not throw, returns what was read
        const string frame = "Content-Length: 100\r\n\r\n{partial";
        using var stream = new ContentLengthInputStream(MakeInput(frame));

        var buf = new byte[256];
        var ex = await Record.ExceptionAsync(() => stream.ReadAsync(buf.AsMemory()).AsTask());
        Assert.Null(ex);
    }

    [Fact]
    public async Task Input_EmptyStream_ReturnsZero()
    {
        using var stream = new ContentLengthInputStream(new MemoryStream());
        var buf = new byte[16];
        var read = await stream.ReadAsync(buf.AsMemory());
        Assert.Equal(0, read);
    }

    // ── ContentLengthOutputStream ───────────────────────────────────────────

    [Fact]
    public async Task Output_SingleMessage_WritesContentLengthFrame()
    {
        using var sink = new MemoryStream();
        await using var stream = new ContentLengthOutputStream(sink);

        var payload = Encoding.UTF8.GetBytes("""{"id":1}""" + "\n");
        await stream.WriteAsync(payload.AsMemory());
        await stream.FlushAsync();

        var result = Encoding.ASCII.GetString(sink.ToArray());
        Assert.StartsWith("Content-Length:", result, StringComparison.Ordinal);
        Assert.Contains("\r\n\r\n", result);
        Assert.Contains("""{"id":1}""", result);
    }

    [Fact]
    public async Task Output_FrameLength_MatchesPayloadBytes()
    {
        using var sink = new MemoryStream();
        await using var stream = new ContentLengthOutputStream(sink);

        const string payload = """{"jsonrpc":"2.0","result":null}""";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(payload + "\n").AsMemory());
        await stream.FlushAsync();

        var raw = Encoding.ASCII.GetString(sink.ToArray());
        var headerLine = raw.Split("\r\n")[0];
        var clStr = headerLine["Content-Length:".Length..].Trim();
        Assert.True(int.TryParse(clStr, out var cl));
        Assert.Equal(Encoding.UTF8.GetByteCount(payload), cl);
    }

    [Fact]
    public async Task Output_MultipleMessages_ProducesMultipleFrames()
    {
        using var sink = new MemoryStream();
        await using var stream = new ContentLengthOutputStream(sink);

        await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"id\":1}\n").AsMemory());
        await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"id\":2}\n").AsMemory());
        await stream.FlushAsync();

        var raw = Encoding.ASCII.GetString(sink.ToArray());
        Assert.Equal(2, CountOccurrences(raw, "Content-Length:"));
    }

    [Fact]
    public async Task Output_PartialWritesThenNewline_ProducesOneFrame()
    {
        using var sink = new MemoryStream();
        await using var stream = new ContentLengthOutputStream(sink);

        // SDK may write JSON in chunks followed by a separate '\n' write
        await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"id\":").AsMemory());
        await stream.WriteAsync(Encoding.UTF8.GetBytes("42}").AsMemory());
        await stream.WriteAsync(new byte[] { (byte)'\n' }.AsMemory());
        await stream.FlushAsync();

        var raw = Encoding.ASCII.GetString(sink.ToArray());
        Assert.Equal(1, CountOccurrences(raw, "Content-Length:"));
        Assert.Contains("{\"id\":42}", raw);
    }

    // ── Round-trip ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RoundTrip_EncodeDecodeRestoresPayload()
    {
        const string original = """{"jsonrpc":"2.0","id":42,"method":"tools/list"}""";

        // Encode via output stream
        using var wire = new MemoryStream();
        await using var output = new ContentLengthOutputStream(wire);
        await output.WriteAsync(Encoding.UTF8.GetBytes(original + "\n").AsMemory());
        await output.FlushAsync();

        // Decode via input stream
        wire.Position = 0;
        using var input = new ContentLengthInputStream(wire);
        var decoded = await ReadLineAsync(input);

        Assert.Equal(original, decoded);
    }

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }
}
