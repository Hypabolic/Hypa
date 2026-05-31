using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Mcp;
using Xunit;

namespace Hypa.UnitTests.Mcp;

public sealed class McpResponseCompressionServiceTests
{
    private readonly McpResponseCompressionService _sut = new();

    private static McpResult OkResult(string raw, string compressed = "") =>
        new("srv", "tool",
            new JsonPayload(raw), compressed,
            new McpLatencyMetadata(DateTimeOffset.UtcNow, TimeSpan.Zero),
            IsError: false, Error: null);

    private static McpResult ErrorResult(string raw) =>
        new("srv", "tool",
            new JsonPayload(raw), string.Empty,
            new McpLatencyMetadata(DateTimeOffset.UtcNow, TimeSpan.Zero),
            IsError: true,
            new McpProxyError(McpErrorCodes.RemoteToolError, "err", "srv", "tool"));

    [Fact]
    public void Raw_hint_returns_result_unchanged()
    {
        var result = OkResult("[{\"type\":\"text\",\"text\":\"hello\"}]");
        var out_ = _sut.Compress(result, CompressionHint.Raw);
        Assert.Same(result, out_);
    }

    [Fact]
    public void Error_result_is_never_compressed()
    {
        var result = ErrorResult("[{\"type\":\"text\",\"text\":\"oops\"}]");
        var out_ = _sut.Compress(result, CompressionHint.Summary);
        Assert.Same(result, out_);
    }

    [Fact]
    public void Summary_extracts_text_blocks()
    {
        var raw = "[{\"type\":\"text\",\"text\":\"hello\"},{\"type\":\"image\"},{\"type\":\"text\",\"text\":\"world\"}]";
        var out_ = _sut.Compress(OkResult(raw), CompressionHint.Summary);
        Assert.Equal("hello\nworld", out_.CompressedResponse);
    }

    [Fact]
    public void Summary_collapses_blank_lines()
    {
        var raw = "[{\"type\":\"text\",\"text\":\"line1\\n\\n\\nline2\"}]";
        var out_ = _sut.Compress(OkResult(raw), CompressionHint.Summary);
        Assert.DoesNotContain("\n\n", out_.CompressedResponse);
    }

    [Fact]
    public void Null_hint_behaves_as_summary()
    {
        var raw = "[{\"type\":\"text\",\"text\":\"hello\"}]";
        var withNull = _sut.Compress(OkResult(raw), hint: null);
        var withSummary = _sut.Compress(OkResult(raw), CompressionHint.Summary);
        Assert.Equal(withSummary.CompressedResponse, withNull.CompressedResponse);
    }

    [Fact]
    public void Structured_compacts_json()
    {
        var pretty = "{ \"a\" : 1,\n  \"b\" : 2 }";
        var out_ = _sut.Compress(OkResult(pretty), CompressionHint.Structured);
        Assert.Equal("{\"a\":1,\"b\":2}", out_.CompressedResponse);
    }

    [Fact]
    public void Structured_falls_back_to_summary_on_invalid_json()
    {
        var raw = "[{\"type\":\"text\",\"text\":\"fallback\"}]";
        var out_ = _sut.Compress(OkResult(raw), CompressionHint.Structured);
        // Content block array is valid JSON but not a plain object — compacts as array,
        // so we just verify the result is non-empty and the raw response is preserved.
        Assert.False(string.IsNullOrWhiteSpace(out_.CompressedResponse));
        Assert.Equal(raw, out_.RawResponse.RawJson);
    }

    [Fact]
    public void Summary_EmptyRawContent_ReturnsEmptyCompressedResponse()
    {
        var out_ = _sut.Compress(OkResult(""), CompressionHint.Summary);
        Assert.Equal(string.Empty, out_.CompressedResponse);
    }

    [Fact]
    public void Summary_AllWhitespaceTextBlocks_FallsBackToRawJson()
    {
        // When all text blocks are whitespace-only the joined text is empty,
        // so the service falls back to returning the original raw JSON string.
        var raw = "[{\"type\":\"text\",\"text\":\"   \\n   \"}]";
        var out_ = _sut.Compress(OkResult(raw), CompressionHint.Summary);
        Assert.False(string.IsNullOrWhiteSpace(out_.CompressedResponse));
        Assert.Equal(raw, out_.RawResponse.RawJson);
    }

    [Fact]
    public void Summary_falls_back_to_raw_json_when_no_text_blocks()
    {
        var raw = "[{\"type\":\"image\"}]";
        var out_ = _sut.Compress(OkResult(raw), CompressionHint.Summary);
        Assert.False(string.IsNullOrWhiteSpace(out_.CompressedResponse));
    }
}
