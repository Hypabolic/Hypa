using System.CommandLine;
using System.Text.Json;
using Hypa.Cli.Commands;
using Hypa.Cli.Json;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Cli;

[Trait("Category", "McpDiscoveryCommand")]
[Collection("SequentialEnvTests")]
public sealed class McpDiscoveryCommandTests
{
    private readonly IMcpServerConfigReader _reader = Substitute.For<IMcpServerConfigReader>();
    private readonly IMcpServerConfigWriter _writer = Substitute.For<IMcpServerConfigWriter>();
    private readonly IMcpServerDefinitionRepository _serverRepo = Substitute.For<IMcpServerDefinitionRepository>();
    private readonly IMcpAuthProvider _authProvider = Substitute.For<IMcpAuthProvider>();
    private readonly IMcpServerProbe _probe = Substitute.For<IMcpServerProbe>();
    private readonly IMcpDispatcher _dispatcher = Substitute.For<IMcpDispatcher>();

    public McpDiscoveryCommandTests()
    {
        _reader.ReadEditableAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([]));
        _writer.WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>())
            .Returns(Result<Unit, Error>.Ok(Unit.Value));
        _serverRepo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([]));
        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new McpServerProbeResult(McpServerProbeStatus.Reachable, "ok"));
        _dispatcher.GetSchemaAsync(Arg.Any<CancellationToken>())
            .Returns(new McpSchemaManifest([], null));
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

    // --- mcp list ---

    [Fact]
    public async Task List_ZeroServers_PrintsNoServersConfigured()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync(["mcp", "list"]);

        Assert.Equal(0, exit);
        Assert.Contains("No MCP servers configured.", capture.Stdout.ToString());
    }

    [Fact]
    public async Task List_StdioNoneAuth_PrintsNameTransportAndEndpoint()
    {
        _serverRepo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([
                new McpServerDefinition(
                    "my-server",
                    new McpTransportConfig(McpTransportKind.Stdio, "hypa serve"),
                    new NoneAuthConfig()),
            ]));

        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync(["mcp", "list"]);

        Assert.Equal(0, exit);
        var stdout = capture.Stdout.ToString();
        Assert.Contains("my-server", stdout);
        Assert.Contains("hypa serve", stdout);
        Assert.Contains("None", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_BearerAuth_PrintsBearerLabel()
    {
        _serverRepo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([
                new McpServerDefinition(
                    "api-server",
                    new McpTransportConfig(McpTransportKind.HttpAutoDetect, "https://example.com"),
                    new BearerAuthConfig("env:TOKEN")),
            ]));

        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync(["mcp", "list"]);

        Assert.Equal(0, exit);
        Assert.Contains("BearerAuth", capture.Stdout.ToString());
    }

    [Fact]
    public async Task List_LoadFailure_WritesErrorToStderrAndExitsNonZero()
    {
        _serverRepo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Fail(
                new Error("LoadFailed", "disk error")));

        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync(["mcp", "list"]);

        Assert.NotEqual(0, exit);
        Assert.Contains("error", capture.Stderr.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_Json_ReturnsWellFormedArrayWithCorrectFields()
    {
        _serverRepo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([
                new McpServerDefinition(
                    "json-server",
                    new McpTransportConfig(McpTransportKind.Http, "https://example.com/mcp"),
                    new BearerAuthConfig("env:TOKEN"),
                    Tls: new McpTlsConfig("/etc/ssl/ca.crt", null, null)),
            ]));

        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync(["mcp", "list", "--json"]);

        Assert.Equal(0, exit);
        var items = JsonSerializer.Deserialize<List<McpServerListItemJson>>(
            capture.Stdout.ToString(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(items);
        Assert.Single(items);
        Assert.Equal("json-server", items[0].Name);
        Assert.Equal("BearerAuth", items[0].Auth);
        Assert.True(items[0].HasTls);
    }

    // --- mcp tools ---

    [Fact]
    public async Task Tools_NoTools_PrintsNoToolsFound()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync(["mcp", "tools"]);

        Assert.Equal(0, exit);
        Assert.Contains("No tools found.", capture.Stdout.ToString());
    }

    [Fact]
    public async Task Tools_UnknownServer_PrintsServerNotFoundMessage()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync(["mcp", "tools", "--server", "unknown"]);

        Assert.Equal(0, exit);
        Assert.Contains("not found", capture.Stdout.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Tools_MultipleServers_ListsAllToolsWithServerPrefix()
    {
        _dispatcher.GetSchemaAsync(Arg.Any<CancellationToken>())
            .Returns(new McpSchemaManifest(
            [
                new McpServerSchema("server-a", [
                    new McpToolSchema("tool-one", "Does the first thing", new JsonPayload("{}")),
                ]),
                new McpServerSchema("server-b", [
                    new McpToolSchema("tool-two", "Does the second thing", new JsonPayload("{}")),
                ]),
            ], null));

        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync(["mcp", "tools"]);

        Assert.Equal(0, exit);
        var stdout = capture.Stdout.ToString();
        Assert.Contains("server-a/tool-one", stdout);
        Assert.Contains("server-b/tool-two", stdout);
    }

    [Fact]
    public async Task Tools_LongDescription_IsTruncatedInHumanOutput()
    {
        var longDesc = new string('x', 150);
        _dispatcher.GetSchemaAsync(Arg.Any<CancellationToken>())
            .Returns(new McpSchemaManifest(
            [
                new McpServerSchema("srv", [
                    new McpToolSchema("big-tool", longDesc, new JsonPayload("{}")),
                ]),
            ], null));

        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        await root.InvokeAsync(["mcp", "tools"]);

        var stdout = capture.Stdout.ToString();
        Assert.DoesNotContain(longDesc, stdout);
        Assert.Contains('…', stdout);
    }

    [Fact]
    public async Task Tools_ServerFilter_ShowsOnlyMatchingServersTools()
    {
        _dispatcher.GetSchemaAsync(Arg.Any<CancellationToken>())
            .Returns(new McpSchemaManifest(
            [
                new McpServerSchema("target", [
                    new McpToolSchema("good-tool", "included", new JsonPayload("{}")),
                ]),
                new McpServerSchema("other", [
                    new McpToolSchema("other-tool", "excluded", new JsonPayload("{}")),
                ]),
            ], null));

        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync(["mcp", "tools", "--server", "target"]);

        Assert.Equal(0, exit);
        var stdout = capture.Stdout.ToString();
        Assert.Contains("good-tool", stdout);
        Assert.DoesNotContain("other-tool", stdout);
    }

    [Fact]
    public async Task Tools_Json_DescriptionsAreNotTruncated()
    {
        var fullDesc = new string('d', 200);
        _dispatcher.GetSchemaAsync(Arg.Any<CancellationToken>())
            .Returns(new McpSchemaManifest(
            [
                new McpServerSchema("srv", [
                    new McpToolSchema("my-tool", fullDesc, new JsonPayload("{}")),
                ]),
            ], null));

        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync(["mcp", "tools", "--json"]);

        Assert.Equal(0, exit);
        var items = JsonSerializer.Deserialize<List<McpToolListEntryJson>>(
            capture.Stdout.ToString(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(items);
        Assert.Single(items);
        Assert.Equal(fullDesc, items[0].Description);
    }

    [Fact]
    public async Task Tools_SchemaErrorOnOneServer_ShowsGoodToolsAndWarnsOnStderr()
    {
        _dispatcher.GetSchemaAsync(Arg.Any<CancellationToken>())
            .Returns(new McpSchemaManifest(
            [
                new McpServerSchema("good-srv", [
                    new McpToolSchema("a-tool", "works fine", new JsonPayload("{}")),
                ]),
            ],
            [
                new McpSchemaError("bad-srv", McpErrorCodes.SchemaUnavailable, "connection refused"),
            ]));

        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync(["mcp", "tools"]);

        Assert.Equal(0, exit);
        Assert.Contains("a-tool", capture.Stdout.ToString());
        Assert.Contains("bad-srv", capture.Stderr.ToString());
    }
}
