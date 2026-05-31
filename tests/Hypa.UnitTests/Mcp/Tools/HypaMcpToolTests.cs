using Hypa.Infrastructure.Mcp;
using Hypa.Infrastructure.Mcp.Auth;
using Hypa.Infrastructure.Mcp.Tools;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Mcp;
using Hypa.Runtime.Domain.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Hypa.UnitTests.Mcp.Tools;

public sealed class HypaMcpToolTests
{
    private readonly IMcpDispatcher _dispatcher = Substitute.For<IMcpDispatcher>();
    private readonly IMcpServerDefinitionRepository _repo = Substitute.For<IMcpServerDefinitionRepository>();
    private readonly IMcpAuthProvider _authProvider = Substitute.For<IMcpAuthProvider>();
    private readonly IEvidenceLedger _ledger = Substitute.For<IEvidenceLedger>();
    private readonly ISessionResolver _sessionResolver = Substitute.For<ISessionResolver>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly SecretRedactionRegistry _redactionRegistry = new();
    private readonly McpProxyService _proxyService;

    public HypaMcpToolTests()
    {
        _clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        _proxyService = new McpProxyService(
            _dispatcher,
            new McpResponseCompressionService(),
            new McpToolSearchIndex(),
            _clock);

        _sessionResolver
            .ResolveAsync(Arg.Any<SessionResolveOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<ContextSession, Error>.Fail(new Error("NoSession", "no session")));
    }

    private Task<CallToolResult> Execute(
        string action,
        string? server = null,
        string? tool = null,
        string? arguments = null,
        string? hint = null,
        string? requests = null,
        string? query = null) =>
        HypaMcpTool.ExecuteAsync(
            _proxyService, _repo, _authProvider, _ledger, _sessionResolver,
            _redactionRegistry,
            NullLogger<HypaMcpTool>.Instance,
            CancellationToken.None,
            action, server, tool, arguments, hint, requests, query);

    private static string TextOf(CallToolResult r) => McpToolResult.TextOf(r);

    [Fact]
    public async Task Unknown_action_returns_InvalidRequest()
    {
        var result = await Execute("bogus");

        Assert.True(result.IsError);
        Assert.Contains(McpErrorCodes.InvalidRequest, TextOf(result));
    }

    [Theory]
    [InlineData(null, "echo")]
    [InlineData("", "echo")]
    [InlineData("srv", null)]
    [InlineData("srv", "")]
    public async Task Invoke_missing_required_params_returns_InvalidRequest(string? server, string? tool)
    {
        var result = await Execute("invoke", server: server, tool: tool);

        Assert.True(result.IsError);
        Assert.Contains(McpErrorCodes.InvalidRequest, TextOf(result));
    }

    [Fact]
    public async Task Batch_missing_requests_returns_InvalidRequest()
    {
        var result = await Execute("batch", requests: null);

        Assert.True(result.IsError);
        Assert.Contains(McpErrorCodes.InvalidRequest, TextOf(result));
    }

    [Fact]
    public async Task Batch_malformed_json_returns_InvalidRequest_without_exception_detail()
    {
        var result = await Execute("batch", requests: "not-json");

        Assert.True(result.IsError);
        var text = TextOf(result);
        Assert.Contains(McpErrorCodes.InvalidRequest, text);
        Assert.DoesNotContain("Exception", text);
        Assert.DoesNotContain("JsonReaderException", text);
    }

    [Fact]
    public async Task Batch_empty_array_returns_InvalidRequest()
    {
        var result = await Execute("batch", requests: "[]");

        Assert.True(result.IsError);
        Assert.Contains(McpErrorCodes.InvalidRequest, TextOf(result));
    }

    [Fact]
    public async Task Search_missing_query_returns_InvalidRequest()
    {
        var result = await Execute("search", query: null);

        Assert.True(result.IsError);
        Assert.Contains(McpErrorCodes.InvalidRequest, TextOf(result));
    }

    [Fact]
    public async Task AuthCheck_missing_server_returns_InvalidRequest()
    {
        var result = await Execute("auth_check", server: null);

        Assert.True(result.IsError);
        Assert.Contains(McpErrorCodes.InvalidRequest, TextOf(result));
    }

    [Fact]
    public async Task AuthCheck_config_load_failure_returns_SchemaUnavailable()
    {
        _repo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Fail(new Error("IoError", "disk failure")));

        var result = await Execute("auth_check", server: "svc");

        Assert.True(result.IsError);
        var text = TextOf(result);
        Assert.Contains(McpErrorCodes.SchemaUnavailable, text);
        Assert.DoesNotContain("disk failure", text);
    }

    [Fact]
    public async Task AuthCheck_unknown_server_returns_UnknownServer()
    {
        _repo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([]));

        var result = await Execute("auth_check", server: "missing");

        Assert.True(result.IsError);
        Assert.Contains(McpErrorCodes.UnknownServer, TextOf(result));
    }

    [Fact]
    public async Task AuthCheck_provider_exception_returns_AuthRequired_without_exception_detail()
    {
        var definition = new McpServerDefinition(
            "svc",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com/mcp"),
            new NoneAuthConfig());

        _repo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([definition]));

        _authProvider
            .GetAuthContextAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("token endpoint unreachable — internal detail"));

        var result = await Execute("auth_check", server: "svc");

        Assert.True(result.IsError);
        var text = TextOf(result);
        Assert.Contains(McpErrorCodes.AuthRequired, text);
        Assert.DoesNotContain("token endpoint unreachable", text);
        Assert.DoesNotContain("internal detail", text);
    }

    [Fact]
    public async Task Invoke_success_returns_formatted_output()
    {
        var mcpResult = new McpResult(
            "svc", "echo",
            new JsonPayload("[{\"type\":\"text\",\"text\":\"pong\"}]"),
            "pong",
            new McpLatencyMetadata(DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(42)),
            IsError: false, Error: null);

        _dispatcher.InvokeAsync(Arg.Any<McpProxyRequest>(), Arg.Any<CancellationToken>())
            .Returns(mcpResult);

        var result = await Execute("invoke", server: "svc", tool: "echo");

        Assert.True(result.IsError is not true);
        var text = TextOf(result);
        Assert.Contains("SUMMARY", text);
        Assert.Contains("DETAILS", text);
        Assert.Contains("STATS", text);
        Assert.Contains("pong", text);
    }

    [Fact]
    public async Task Invoke_success_records_to_evidence_ledger()
    {
        var mcpResult = new McpResult(
            "svc", "echo",
            new JsonPayload("{}"), "ok",
            new McpLatencyMetadata(DateTimeOffset.UtcNow, TimeSpan.Zero),
            IsError: false, Error: null);

        _dispatcher.InvokeAsync(Arg.Any<McpProxyRequest>(), Arg.Any<CancellationToken>())
            .Returns(mcpResult);

        await Execute("invoke", server: "svc", tool: "echo");

        await _ledger.Received(1).RecordToolCallAsync(
            Arg.Is<ToolCallRecord>(r =>
                r.ToolName == "hypa_mcp" &&
                !string.IsNullOrEmpty(r.ArgsHash) &&
                !string.IsNullOrEmpty(r.OutputHash)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Invoke_success_args_json_does_not_contain_raw_tool_arguments()
    {
        // Only action/server/tool/hint/query are captured in argsJson — not the raw `arguments`
        // value, which may contain secrets. Verify the ledger record's Args field omits `arguments`.
        var mcpResult = new McpResult(
            "svc", "echo",
            new JsonPayload("{}"), "ok",
            new McpLatencyMetadata(DateTimeOffset.UtcNow, TimeSpan.Zero),
            IsError: false, Error: null);

        _dispatcher.InvokeAsync(Arg.Any<McpProxyRequest>(), Arg.Any<CancellationToken>())
            .Returns(mcpResult);

        await Execute("invoke", server: "svc", tool: "echo", arguments: """{"password":"secret-value"}""");

        await _ledger.Received(1).RecordToolCallAsync(
            Arg.Is<ToolCallRecord>(r => !r.Args.Contains("secret-value")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Invoke_success_registered_secrets_are_redacted_in_evidence_result()
    {
        // Secrets registered with SecretRedactionRegistry must not appear in evidence Result text.
        const string secretToken = "super-secret-output-token";
        _redactionRegistry.Register(secretToken);

        var mcpResult = new McpResult(
            "svc", "echo",
            new JsonPayload("{}"), $"response contains {secretToken} value",
            new McpLatencyMetadata(DateTimeOffset.UtcNow, TimeSpan.Zero),
            IsError: false, Error: null);

        _dispatcher.InvokeAsync(Arg.Any<McpProxyRequest>(), Arg.Any<CancellationToken>())
            .Returns(mcpResult);

        await Execute("invoke", server: "svc", tool: "echo");

        await _ledger.Received(1).RecordToolCallAsync(
            Arg.Is<ToolCallRecord>(r => !r.Result.Contains(secretToken)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Batch_success_and_failure_returns_ordered_summary_table()
    {
        var ok = new McpResult(
            "svc", "tool1",
            new JsonPayload("{}"), "done",
            new McpLatencyMetadata(DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(10)),
            IsError: false, Error: null);

        var err = new McpResult(
            "svc", "tool2",
            new JsonPayload("{}"), string.Empty,
            new McpLatencyMetadata(DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(5)),
            IsError: true,
            new McpProxyError(McpErrorCodes.ConnectionFailed, "refused", "svc", "tool2"));

        _dispatcher.InvokeAsync(
                Arg.Is<McpProxyRequest>(r => r.ToolName == "tool1"),
                Arg.Any<CancellationToken>())
            .Returns(ok);

        _dispatcher.InvokeAsync(
                Arg.Is<McpProxyRequest>(r => r.ToolName == "tool2"),
                Arg.Any<CancellationToken>())
            .Returns(err);

        var result = await Execute(
            "batch",
            requests: """[{"server":"svc","tool":"tool1"},{"server":"svc","tool":"tool2"}]""");

        Assert.True(result.IsError is not true);
        var text = TextOf(result);
        Assert.Contains("SUMMARY", text);
        Assert.Contains("RESULTS", text);
        Assert.Contains("tool1", text);
        Assert.Contains("OK", text);
        Assert.Contains("tool2", text);
        Assert.Contains("ERROR", text);
        // [0] before [1] in the output (order preserved)
        Assert.True(text.IndexOf("[0]", StringComparison.Ordinal) < text.IndexOf("[1]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Batch_string_arguments_are_forwarded_as_object_json()
    {
        var mcpResult = new McpResult(
            "svc", "tool1",
            new JsonPayload("{}"), "done",
            new McpLatencyMetadata(DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(10)),
            IsError: false, Error: null);

        _dispatcher.InvokeAsync(
                Arg.Is<McpProxyRequest>(r =>
                    r.ToolName == "tool1" &&
                    r.Arguments.RawJson == "{\"command\":\"echo ok\"}"),
                Arg.Any<CancellationToken>())
            .Returns(mcpResult);

        var result = await Execute(
            "batch",
            requests: """[{"server":"svc","tool":"tool1","arguments":"{\"command\":\"echo ok\"}"}]""");

        Assert.True(result.IsError is not true);
        await _dispatcher.Received(1).InvokeAsync(
            Arg.Is<McpProxyRequest>(r => r.Arguments.RawJson == "{\"command\":\"echo ok\"}"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Search_returns_matching_results()
    {
        var manifest = new McpSchemaManifest(
        [
            new McpServerSchema("svc",
            [
                new McpToolSchema("read_file", "Read a file from disk", new JsonPayload("{}")),
                new McpToolSchema("run_command", "Execute a shell command", new JsonPayload("{}")),
            ]),
        ]);

        _dispatcher.GetSchemaAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(manifest));

        var result = await Execute("search", query: "read file");

        Assert.True(result.IsError is not true);
        var text = TextOf(result);
        Assert.Contains("SUMMARY", text);
        Assert.Contains("RESULTS", text);
        Assert.Contains("read_file", text);
    }

    [Fact]
    public async Task Schema_returns_schema_output_with_tool_names()
    {
        var manifest = new McpSchemaManifest(
        [
            new McpServerSchema("svc",
            [
                new McpToolSchema("list_files", "List files", new JsonPayload("{}")),
            ]),
        ]);

        _dispatcher.GetSchemaAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(manifest));

        var result = await Execute("schema");

        Assert.True(result.IsError is not true);
        var text = TextOf(result);
        Assert.Contains("SUMMARY", text);
        Assert.Contains("SCHEMA", text);
        Assert.Contains("svc", text);
        Assert.Contains("list_files", text);
    }

    [Fact]
    public async Task Invoke_CancelledToken_ThrowsOperationCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _dispatcher.InvokeAsync(Arg.Any<McpProxyRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            HypaMcpTool.ExecuteAsync(
                _proxyService, _repo, _authProvider, _ledger, _sessionResolver,
                _redactionRegistry,
                NullLogger<HypaMcpTool>.Instance,
                cts.Token,
                "invoke", "svc", "echo"));
    }

    [Fact]
    public async Task Invoke_error_result_carries_stable_code()
    {
        var definition = new McpServerDefinition(
            "svc",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com/mcp"),
            new NoneAuthConfig());

        var error = new McpResult(
            "svc", "echo",
            new JsonPayload("{}"),
            string.Empty,
            new McpLatencyMetadata(DateTimeOffset.UtcNow, TimeSpan.Zero),
            IsError: true,
            new McpProxyError(McpErrorCodes.ConnectionFailed, "Failed to connect to server 'svc'.", "svc", "echo"));

        _dispatcher.InvokeAsync(Arg.Any<McpProxyRequest>(), Arg.Any<CancellationToken>())
            .Returns(error);

        var result = await Execute("invoke", server: "svc", tool: "echo");

        Assert.True(result.IsError);
        Assert.Contains(McpErrorCodes.ConnectionFailed, TextOf(result));
    }
}
