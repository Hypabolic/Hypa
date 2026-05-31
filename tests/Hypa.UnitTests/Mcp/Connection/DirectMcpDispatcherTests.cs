using Hypa.Infrastructure.Mcp.Connection;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Mcp.Connection;

public sealed class DirectMcpDispatcherTests
{
    private readonly IMcpServerDefinitionRepository _repo = Substitute.For<IMcpServerDefinitionRepository>();
    private readonly IMcpClientConnectionFactory _factory = Substitute.For<IMcpClientConnectionFactory>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly McpConfigValidationService _validator = new();
    private readonly DirectMcpDispatcher _sut;

    public DirectMcpDispatcherTests()
    {
        _clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        _sut = new DirectMcpDispatcher(
            _repo,
            _factory,
            _validator,
            _clock,
            NullLogger<DirectMcpDispatcher>.Instance);
    }

    private static McpServerDefinition Server(string name = "svc") =>
        new(name,
            new McpTransportConfig(McpTransportKind.Http, "https://example.com/mcp"),
            new NoneAuthConfig());

    private static McpProxyRequest Request(string server = "svc", string tool = "echo") =>
        new(server, tool, new JsonPayload("{}"));

    private IMcpClientFacade FakeClient(
        IList<McpClientTool>? tools = null,
        CallToolResult? result = null)
    {
        var client = Substitute.For<IMcpClientFacade>();
        client.ListToolsAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IList<McpClientTool>>(tools ?? []));
        client.CallToolAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<CallToolResult>(result ?? new CallToolResult()));
        return client;
    }

    [Fact]
    public async Task GetSchemaAsync_MapsToolsFromAllServers()
    {
        var servers = new[] { Server("a"), Server("b") };
        _repo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok(servers)));

        var toolA = CreateMcpClientTool("tool_a", "Does A");
        var toolB = CreateMcpClientTool("tool_b", "Does B");

        var clientA = FakeClient([toolA]);
        var clientB = FakeClient([toolB]);
        _factory.GetOrCreateAsync(servers[0], Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IMcpClientFacade, McpProxyError>.Ok(clientA)));
        _factory.GetOrCreateAsync(servers[1], Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IMcpClientFacade, McpProxyError>.Ok(clientB)));

        var manifest = await _sut.GetSchemaAsync(default);

        Assert.Equal(2, manifest.Servers.Count);
        Assert.Equal("a", manifest.Servers[0].ServerName);
        Assert.Single(manifest.Servers[0].Tools);
        Assert.Equal("tool_a", manifest.Servers[0].Tools[0].Name);
        Assert.Equal("b", manifest.Servers[1].ServerName);
        Assert.Equal("tool_b", manifest.Servers[1].Tools[0].Name);
    }

    [Fact]
    public async Task InvokeAsync_ReturnsResult_WithLatency()
    {
        var server = Server();
        _repo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok(
                (IReadOnlyList<McpServerDefinition>)[server])));

        var sdkResult = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "pong" }]
        };
        var client = FakeClient(result: sdkResult);
        _factory.GetOrCreateAsync(server, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IMcpClientFacade, McpProxyError>.Ok(client)));

        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddMilliseconds(42);
        _clock.UtcNow.Returns(start, end);

        var result = await _sut.InvokeAsync(Request(), default);

        Assert.False(result.IsError);
        Assert.Equal("svc", result.ServerName);
        Assert.Equal("echo", result.ToolName);
        Assert.Equal("pong", result.CompressedResponse);
        Assert.Equal(start, result.Latency.StartedAt);
        Assert.Equal(TimeSpan.FromMilliseconds(42), result.Latency.Elapsed);
    }

    [Fact]
    public async Task InvokeAsync_OnSdkException_InvalidatesAndReturnsError()
    {
        var server = Server();
        _repo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok(
                (IReadOnlyList<McpServerDefinition>)[server])));

        var client = Substitute.For<IMcpClientFacade>();
        client.CallToolAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<CallToolResult>>(_ => throw new InvalidOperationException("upstream error"));

        _factory.GetOrCreateAsync(server, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IMcpClientFacade, McpProxyError>.Ok(client)));

        var result = await _sut.InvokeAsync(Request(), default);

        Assert.True(result.IsError);
        Assert.Equal(McpErrorCodes.ToolInvocationFailed, result.Error!.Code);
        await _factory.Received(1).InvalidateAsync("svc");
    }

    [Fact]
    public async Task InvokeBatchAsync_FansOutToAllRequests()
    {
        var server = Server();
        _repo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok(
                (IReadOnlyList<McpServerDefinition>)[server])));

        var sdkResult = new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };
        var client = FakeClient(result: sdkResult);
        _factory.GetOrCreateAsync(server, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IMcpClientFacade, McpProxyError>.Ok(client)));

        var requests = new[]
        {
            Request("svc", "tool1"),
            Request("svc", "tool2"),
            Request("svc", "tool3"),
        };

        var results = await _sut.InvokeBatchAsync(requests, default);

        Assert.Equal(3, results.Count);
        Assert.Equal("tool1", results[0].ToolName);
        Assert.Equal("tool2", results[1].ToolName);
        Assert.Equal("tool3", results[2].ToolName);
    }

    [Fact]
    public async Task SearchToolsAsync_FiltersOnNameAndDescription()
    {
        var server = Server();
        _repo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok(
                (IReadOnlyList<McpServerDefinition>)[server])));

        IList<McpClientTool> tools =
        [
            CreateMcpClientTool("list_files", "Lists files in a directory"),
            CreateMcpClientTool("run_command", "Executes a shell command"),
            CreateMcpClientTool("read_file", "Reads file content"),
        ];
        var clientWithTools = FakeClient(tools);
        _factory.GetOrCreateAsync(server, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IMcpClientFacade, McpProxyError>.Ok(clientWithTools)));

        var results = await _sut.SearchToolsAsync("file", default);

        Assert.Equal(2, results.Count);
        var names = results.Select(r => r.ToolName).ToHashSet();
        Assert.Contains("list_files", names);
        Assert.Contains("read_file", names);
    }

    [Fact]
    public async Task InvokeAsync_UnknownServer_ReturnsError()
    {
        _repo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok(
                (IReadOnlyList<McpServerDefinition>)[Server("other")])));

        var result = await _sut.InvokeAsync(Request("missing"), default);

        Assert.True(result.IsError);
        Assert.Equal(McpErrorCodes.UnknownServer, result.Error!.Code);
    }

    [Fact]
    public async Task InvokeAsync_ConnectionFactoryFailure_ReturnsStructuredError()
    {
        var server = Server();
        _repo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok(
                (IReadOnlyList<McpServerDefinition>)[server])));
        _factory.GetOrCreateAsync(server, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IMcpClientFacade, McpProxyError>.Fail(
                new McpProxyError(McpErrorCodes.ConnectionFailed, "refused", server.Name))));

        var result = await _sut.InvokeAsync(Request(), default);

        Assert.True(result.IsError);
        Assert.Equal(McpErrorCodes.ConnectionFailed, result.Error!.Code);
    }

    [Fact]
    public async Task InvokeAsync_Timeout_ReturnsTimeoutErrorAndInvalidatesClient()
    {
        var server = Server();
        _repo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok(
                (IReadOnlyList<McpServerDefinition>)[server])));

        var client = Substitute.For<IMcpClientFacade>();
        client.CallToolAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<CallToolResult>>(_ => throw new OperationCanceledException(CancellationToken.None));
        _factory.GetOrCreateAsync(server, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IMcpClientFacade, McpProxyError>.Ok(client)));

        var result = await _sut.InvokeAsync(Request(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(McpErrorCodes.Timeout, result.Error!.Code);
        await _factory.Received(1).InvalidateAsync("svc");
    }

    [Fact]
    public async Task GetSchemaAsync_OneServerUnavailable_IncludesOtherServers()
    {
        var servers = new[] { Server("ok"), Server("bad") };
        _repo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok(
                (IReadOnlyList<McpServerDefinition>)servers)));

        var goodClient = FakeClient([CreateMcpClientTool("my_tool", "desc")]);
        _factory.GetOrCreateAsync(servers[0], Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IMcpClientFacade, McpProxyError>.Ok(goodClient)));
        _factory.GetOrCreateAsync(servers[1], Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IMcpClientFacade, McpProxyError>.Fail(
                new McpProxyError(McpErrorCodes.ConnectionFailed, "refused", "bad"))));

        var manifest = await _sut.GetSchemaAsync(default);

        Assert.Single(manifest.Servers);
        Assert.Equal("ok", manifest.Servers[0].ServerName);
    }

    [Fact]
    public async Task InvokeBatchAsync_PartialFailure_PreservesInputOrder()
    {
        var server = Server();
        _repo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok(
                (IReadOnlyList<McpServerDefinition>)[server])));

        var successResult = new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };
        var failClient = Substitute.For<IMcpClientFacade>();
        failClient.CallToolAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<CallToolResult>>(_ => throw new InvalidOperationException("boom"));
        var successClient = FakeClient(result: successResult);

        _factory.GetOrCreateAsync(server, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(Result<IMcpClientFacade, McpProxyError>.Ok(successClient)),
                Task.FromResult(Result<IMcpClientFacade, McpProxyError>.Ok(failClient)),
                Task.FromResult(Result<IMcpClientFacade, McpProxyError>.Ok(successClient)));

        var requests = new[]
        {
            Request("svc", "tool1"),
            Request("svc", "tool2"),
            Request("svc", "tool3"),
        };

        var results = await _sut.InvokeBatchAsync(requests, default);

        Assert.Equal(3, results.Count);
        Assert.Equal("tool1", results[0].ToolName);
        Assert.False(results[0].IsError);
        Assert.Equal("tool2", results[1].ToolName);
        Assert.True(results[1].IsError);
        Assert.Equal("tool3", results[2].ToolName);
        Assert.False(results[2].IsError);
    }

    [Fact]
    public async Task InvokeAsync_InvalidServerConfig_ReturnsInvalidRequestBeforeFactory()
    {
        var invalidServer = new McpServerDefinition(
            "svc",
            new McpTransportConfig(McpTransportKind.Stdio, null),
            new NoneAuthConfig());
        _repo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok(
                (IReadOnlyList<McpServerDefinition>)[invalidServer])));

        var result = await _sut.InvokeAsync(Request(), default);

        Assert.True(result.IsError);
        Assert.Equal(McpErrorCodes.InvalidRequest, result.Error!.Code);
        await _factory.DidNotReceiveWithAnyArgs().GetOrCreateAsync(default!, default);
    }

    [Fact]
    public async Task GetSchemaAsync_InvalidServerConfig_SkipsServerWithoutContactingFactory()
    {
        var validServer = Server("good");
        var invalidServer = new McpServerDefinition(
            "bad",
            new McpTransportConfig(McpTransportKind.Stdio, null),
            new NoneAuthConfig());

        _repo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok(
                (IReadOnlyList<McpServerDefinition>)[validServer, invalidServer])));

        var goodClient = FakeClient([CreateMcpClientTool("my_tool", "desc")]);
        _factory.GetOrCreateAsync(validServer, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IMcpClientFacade, McpProxyError>.Ok(goodClient)));

        var manifest = await _sut.GetSchemaAsync(default);

        Assert.Single(manifest.Servers);
        Assert.Equal("good", manifest.Servers[0].ServerName);
        await _factory.DidNotReceive().GetOrCreateAsync(invalidServer, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_RemoteToolError_SetsIsErrorTrue()
    {
        var server = Server();
        _repo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok(
                (IReadOnlyList<McpServerDefinition>)[server])));

        var sdkResult = new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "something failed" }],
        };
        var fakeClient = FakeClient(result: sdkResult);
        _factory.GetOrCreateAsync(server, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IMcpClientFacade, McpProxyError>.Ok(fakeClient)));

        var result = await _sut.InvokeAsync(Request(), default);

        Assert.True(result.IsError);
        Assert.Equal(McpErrorCodes.RemoteToolError, result.Error!.Code);
    }

    [Fact]
    public async Task GetSchemaAsync_ConnectionFailure_PopulatesManifestErrors()
    {
        var server = Server("bad");
        _repo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok(
                (IReadOnlyList<McpServerDefinition>)[server])));
        _factory.GetOrCreateAsync(server, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IMcpClientFacade, McpProxyError>.Fail(
                new McpProxyError(McpErrorCodes.ConnectionFailed, "refused", "bad"))));

        var manifest = await _sut.GetSchemaAsync(default);

        Assert.Empty(manifest.Servers);
        Assert.NotNull(manifest.Errors);
        Assert.Single(manifest.Errors!);
        Assert.Equal("bad", manifest.Errors![0].ServerName);
        Assert.Equal(McpErrorCodes.ConnectionFailed, manifest.Errors![0].Code);
    }

    [Fact]
    public async Task GetSchemaAsync_ListToolsThrows_PopulatesSchemaUnavailableError()
    {
        var server = Server("svc");
        _repo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok(
                (IReadOnlyList<McpServerDefinition>)[server])));

        var client = Substitute.For<IMcpClientFacade>();
        client.ListToolsAsync(Arg.Any<CancellationToken>())
            .Returns<ValueTask<IList<McpClientTool>>>(_ => throw new InvalidOperationException("upstream unavailable"));
        _factory.GetOrCreateAsync(server, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IMcpClientFacade, McpProxyError>.Ok(client)));

        var manifest = await _sut.GetSchemaAsync(default);

        Assert.Empty(manifest.Servers);
        Assert.NotNull(manifest.Errors);
        Assert.Single(manifest.Errors!);
        Assert.Equal("svc", manifest.Errors![0].ServerName);
        Assert.Equal(McpErrorCodes.SchemaUnavailable, manifest.Errors![0].Code);
        await _factory.Received(1).InvalidateAsync("svc");
    }

    [Fact]
    public async Task GetSchemaAsync_AllServersSucceed_ErrorsIsNull()
    {
        var server = Server("svc");
        _repo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok(
                (IReadOnlyList<McpServerDefinition>)[server])));
        var client = FakeClient([CreateMcpClientTool("t", "d")]);
        _factory.GetOrCreateAsync(server, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IMcpClientFacade, McpProxyError>.Ok(client)));

        var manifest = await _sut.GetSchemaAsync(default);

        Assert.Single(manifest.Servers);
        Assert.Null(manifest.Errors);
    }

    [Fact]
    public async Task InvokeAsync_AuthenticatedServer_PassesServerDefinitionWithAuthToFactory()
    {
        var authConfig = new BearerAuthConfig("env:TOKEN");
        var server = new McpServerDefinition(
            "svc",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com/mcp"),
            authConfig);
        _repo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok(
                (IReadOnlyList<McpServerDefinition>)[server])));

        // Pre-create the fake client before configuring the factory mock to avoid
        // NSubstitute's prohibition on substitute creation inside Returns callbacks.
        var fakeClient = FakeClient();
        McpServerDefinition? capturedServer = null;
        _factory.GetOrCreateAsync(
                Arg.Do<McpServerDefinition>(s => capturedServer = s),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IMcpClientFacade, McpProxyError>.Ok(fakeClient)));

        await _sut.InvokeAsync(Request(), default);

        Assert.NotNull(capturedServer);
        Assert.IsType<BearerAuthConfig>(capturedServer.Auth);
    }

    [Fact]
    public async Task InvokeAsync_RepoLoadFails_ReturnsError()
    {
        _repo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<McpServerDefinition>, Error>.Fail(
                new Error("IoError", "disk failure"))));

        var result = await _sut.InvokeAsync(Request(), default);

        Assert.True(result.IsError);
        await _factory.DidNotReceiveWithAnyArgs().GetOrCreateAsync(default!, default);
    }

    private static McpClientTool CreateMcpClientTool(string name, string description)
    {
        var fakeClient = Substitute.For<McpClient>();
        var protocolTool = new Tool { Name = name, Description = description };
        return new McpClientTool(fakeClient, protocolTool, null!);
    }
}
