using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Mcp;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Mcp;

public sealed class McpProxyServiceTests
{
    private readonly IMcpDispatcher _dispatcher = Substitute.For<IMcpDispatcher>();
    private readonly McpResponseCompressionService _compression = new();
    private readonly McpToolSearchIndex _searchIndex = new();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly McpProxyService _sut;

    public McpProxyServiceTests()
    {
        _clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        _sut = new McpProxyService(_dispatcher, _compression, _searchIndex, _clock);
    }

    private static McpResult SuccessResult(string server, string tool) =>
        new(server, tool,
            new JsonPayload("[{\"type\":\"text\",\"text\":\"ok\"}]"),
            "ok",
            new McpLatencyMetadata(DateTimeOffset.UtcNow, TimeSpan.Zero),
            IsError: false, Error: null);

    [Fact]
    public async Task InvokeAsync_empty_server_name_returns_error_without_dispatching()
    {
        var request = new McpProxyRequest("", "tool", new JsonPayload("{}"));
        var result = await _sut.InvokeAsync(request, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(McpErrorCodes.InvalidRequest, result.Error!.Code);
        await _dispatcher.DidNotReceive().InvokeAsync(Arg.Any<McpProxyRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_empty_tool_name_returns_error_without_dispatching()
    {
        var request = new McpProxyRequest("srv", "", new JsonPayload("{}"));
        var result = await _sut.InvokeAsync(request, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(McpErrorCodes.InvalidRequest, result.Error!.Code);
        await _dispatcher.DidNotReceive().InvokeAsync(Arg.Any<McpProxyRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_success_applies_compression()
    {
        var request = new McpProxyRequest("srv", "echo", new JsonPayload("{}"), CompressionHint.Summary);
        var raw = SuccessResult("srv", "echo");
        _dispatcher.InvokeAsync(request, Arg.Any<CancellationToken>()).Returns(raw);

        var result = await _sut.InvokeAsync(request, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("ok", result.CompressedResponse);
    }

    [Fact]
    public async Task InvokeAsync_raw_hint_skips_compression()
    {
        var request = new McpProxyRequest("srv", "echo", new JsonPayload("{}"), CompressionHint.Raw);
        var raw = SuccessResult("srv", "echo") with { CompressedResponse = "original" };
        _dispatcher.InvokeAsync(request, Arg.Any<CancellationToken>()).Returns(raw);

        var result = await _sut.InvokeAsync(request, CancellationToken.None);

        Assert.Equal("original", result.CompressedResponse);
    }

    [Fact]
    public async Task InvokeBatchAsync_preserves_input_order()
    {
        var requests = new[]
        {
            new McpProxyRequest("srv", "first", new JsonPayload("{}")),
            new McpProxyRequest("srv", "second", new JsonPayload("{}")),
            new McpProxyRequest("srv", "third", new JsonPayload("{}")),
        };

        foreach (var r in requests)
            _dispatcher.InvokeAsync(r, Arg.Any<CancellationToken>()).Returns(SuccessResult(r.ServerName, r.ToolName));

        var results = await _sut.InvokeBatchAsync(requests, CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.Equal("first", results[0].ToolName);
        Assert.Equal("second", results[1].ToolName);
        Assert.Equal("third", results[2].ToolName);
    }

    [Fact]
    public async Task InvokeBatchAsync_one_failure_does_not_affect_others()
    {
        var requests = new[]
        {
            new McpProxyRequest("srv", "ok1", new JsonPayload("{}")),
            new McpProxyRequest("srv", "fail", new JsonPayload("{}")),
            new McpProxyRequest("srv", "ok2", new JsonPayload("{}")),
        };

        _dispatcher.InvokeAsync(requests[0], Arg.Any<CancellationToken>()).Returns(SuccessResult("srv", "ok1"));
        _dispatcher.InvokeAsync(requests[1], Arg.Any<CancellationToken>()).Returns(
            new McpResult("srv", "fail", new JsonPayload("{}"), string.Empty,
                new McpLatencyMetadata(DateTimeOffset.UtcNow, TimeSpan.Zero),
                IsError: true,
                new McpProxyError(McpErrorCodes.RemoteToolError, "boom", "srv", "fail")));
        _dispatcher.InvokeAsync(requests[2], Arg.Any<CancellationToken>()).Returns(SuccessResult("srv", "ok2"));

        var results = await _sut.InvokeBatchAsync(requests, CancellationToken.None);

        Assert.False(results[0].IsError);
        Assert.True(results[1].IsError);
        Assert.False(results[2].IsError);
    }

    [Fact]
    public async Task SearchToolsAsync_returns_ranked_results_from_schema()
    {
        var manifest = new McpSchemaManifest([
            new McpServerSchema("srv", [
                new McpToolSchema("read_file", "reads a file", new JsonPayload("{}")),
                new McpToolSchema("write_file", "writes a file", new JsonPayload("{}")),
            ])
        ]);
        _dispatcher.GetSchemaAsync(Arg.Any<CancellationToken>()).Returns(manifest);

        var results = await _sut.SearchToolsAsync("read file", CancellationToken.None);

        Assert.NotEmpty(results);
        Assert.Equal("read_file", results[0].ToolName);
    }
}
