using System.CommandLine;
using Hypa.Cli.Commands;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// Proves the probe port is wired only to AddAsync — not to auth check, schema, or invoke.
/// </summary>
[Trait("Category", "McpCommandRegression")]
public sealed class McpCommandRegressionTests
{
    private readonly IMcpServerConfigReader _reader = Substitute.For<IMcpServerConfigReader>();
    private readonly IMcpServerConfigWriter _writer = Substitute.For<IMcpServerConfigWriter>();
    private readonly IMcpServerDefinitionRepository _serverRepo = Substitute.For<IMcpServerDefinitionRepository>();
    private readonly IMcpAuthProvider _authProvider = Substitute.For<IMcpAuthProvider>();
    private readonly IMcpDispatcher _dispatcher = Substitute.For<IMcpDispatcher>();
    private readonly IMcpServerProbe _probe = Substitute.For<IMcpServerProbe>();

    private static readonly McpServerDefinition TestServer = new(
        "test-server",
        new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
        new NoneAuthConfig());

    public McpCommandRegressionTests()
    {
        _reader.ReadEditableAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([]));
        _writer.WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>())
            .Returns(Result<Unit, Error>.Ok(Unit.Value));
        _serverRepo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([TestServer]));

        // Any call to ProbeAsync during these commands is a test failure.
        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns<McpServerProbeResult>(_ => throw new InvalidOperationException("probe must not be called from auth check / schema / invoke"));
    }

    private RootCommand BuildRoot()
    {
        var validator = new McpConfigValidationService();
        var configService = new McpServerConfigService(_reader, _writer, validator, _probe);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        var proxyService = new McpProxyService(_dispatcher, new McpResponseCompressionService(), new McpToolSearchIndex(), clock);
        var command = new McpCommand(proxyService, _serverRepo, _authProvider, configService, NullLogger<McpCommand>.Instance);
        var root = new RootCommand();
        root.AddCommand(command.Build());
        return root;
    }

    [Fact]
    public async Task AuthCheck_DoesNotInvokeProbe()
    {
        _authProvider.GetAuthContextAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new McpAuthContext(new Dictionary<string, string>()));

        var root = BuildRoot();
        var exit = await root.InvokeAsync(["mcp", "auth", "check", "--server", "test-server"]);

        Assert.Equal(0, exit);
        await _probe.DidNotReceive().ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Schema_DoesNotInvokeProbe()
    {
        _dispatcher.GetSchemaAsync(Arg.Any<CancellationToken>())
            .Returns(new McpSchemaManifest([], null));

        var root = BuildRoot();
        var exit = await root.InvokeAsync(["mcp", "schema"]);

        Assert.Equal(0, exit);
        await _probe.DidNotReceive().ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Invoke_DoesNotInvokeProbe()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        var latency = new McpLatencyMetadata(DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(10));
        _dispatcher.InvokeAsync(Arg.Any<McpProxyRequest>(), Arg.Any<CancellationToken>())
            .Returns(new McpResult("test-server", "some-tool", new JsonPayload("{}"), "ok", latency, IsError: false, Error: null));

        var root = BuildRoot();
        var exit = await root.InvokeAsync(["mcp", "invoke", "--server", "test-server", "--tool", "some-tool"]);

        Assert.Equal(0, exit);
        await _probe.DidNotReceive().ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>());
    }
}
