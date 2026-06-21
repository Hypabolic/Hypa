using Hypa.Infrastructure.Mcp.Tools;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Mcp;

public sealed class HypaCompressToolTests
{
    private readonly ITokenCounter _tokenCounter = Substitute.For<ITokenCounter>();
    private readonly IEvidenceLedger _ledger = Substitute.For<IEvidenceLedger>();
    private readonly ISessionResolver _sessionResolver = Substitute.For<ISessionResolver>();
    private static readonly NullLogger<CompressService> _logger = NullLogger<CompressService>.Instance;

    public HypaCompressToolTests()
    {
        _tokenCounter.EstimateTokens(Arg.Any<string>()).Returns(10);
        _ledger.RecordToolCallAsync(Arg.Any<ToolCallRecord>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _sessionResolver.ResolveAsync(Arg.Any<SessionResolveOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<ContextSession, Error>.Fail(new Error("NONE", "no session")));
    }

    private Task<CallToolResult> Execute(string input, string? kind = null) =>
        HypaCompressTool.ExecuteAsync(
            new CompressService([], _tokenCounter, _ledger, _sessionResolver, _logger),
            CancellationToken.None, input, kind);

    private static string TextOf(CallToolResult r) =>
        string.Concat(r.Content.OfType<TextContentBlock>().Select(c => c.Text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyInput_ReturnsError_WithIsErrorTrue(string input)
    {
        var result = await Execute(input);
        Assert.True(result.IsError);
        Assert.Contains("input is required", TextOf(result));
    }

    [Fact]
    public async Task ValidInput_NoCompressor_ReturnsPassthrough()
    {
        var result = await Execute("hello world output", kind: "generic");
        Assert.True(result.IsError is not true);
        var text = TextOf(result);
        Assert.Contains("SUMMARY", text);
        Assert.Contains("STATS", text);
    }

    [Fact]
    public async Task ValidInput_RecordsEvidence()
    {
        await Execute("some text");
        await _ledger.Received(1).RecordToolCallAsync(
            Arg.Is<ToolCallRecord>(r => r.ToolName == "hypa_compress"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ValidInput_EvidenceHasArgsHashAndOutputHash()
    {
        await Execute("some text");
        await _ledger.Received(1).RecordToolCallAsync(
            Arg.Is<ToolCallRecord>(r =>
                !string.IsNullOrEmpty(r.ArgsHash) &&
                !string.IsNullOrEmpty(r.OutputHash)),
            Arg.Any<CancellationToken>());
    }
}
