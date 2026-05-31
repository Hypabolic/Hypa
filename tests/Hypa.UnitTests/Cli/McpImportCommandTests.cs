using System.CommandLine;
using System.Text;
using Hypa.Cli.Commands;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Cli;

[Trait("Category", "McpImportCommand")]
[Collection("SequentialEnvTests")]
public sealed class McpImportCommandTests
{
    private readonly IMcpServerConfigReader _reader = Substitute.For<IMcpServerConfigReader>();
    private readonly IMcpServerConfigWriter _writer = Substitute.For<IMcpServerConfigWriter>();
    private readonly IMcpServerDefinitionRepository _serverRepo = Substitute.For<IMcpServerDefinitionRepository>();
    private readonly IMcpAuthProvider _authProvider = Substitute.For<IMcpAuthProvider>();
    private readonly IMcpServerImportService _importService = Substitute.For<IMcpServerImportService>();
    private readonly IMcpServerProbe _probe = Substitute.For<IMcpServerProbe>();

    public McpImportCommandTests()
    {
        _reader.ReadEditableAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([]));
        _writer.WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>())
            .Returns(Result<Unit, Error>.Ok(Unit.Value));
        _serverRepo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([]));
        _importService.ImportAsync(Arg.Any<McpImportRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result<McpImportReport, Error>.Ok(new McpImportReport([], 0, 0, 0, 0)));
        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new McpServerProbeResult(McpServerProbeStatus.Reachable, "ok"));
    }

    private RootCommand BuildRoot()
    {
        var validator = new McpConfigValidationService();
        var configService = new McpServerConfigService(_reader, _writer, validator, _probe);
        var dispatcher = Substitute.For<IMcpDispatcher>();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        var proxyService = new McpProxyService(dispatcher, new McpResponseCompressionService(), new McpToolSearchIndex(), clock);
        var command = new McpCommand(proxyService, _serverRepo, _authProvider, configService, NullLogger<McpCommand>.Instance, _importService);
        var root = new RootCommand();
        root.AddCommand(command.Build());
        return root;
    }

    [Fact]
    public async Task ImportCommand_DryRun_PrintsPreviewAndCallsServiceWithDryRun()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync(["mcp", "import", "--dry-run"]);

        Assert.Equal(0, exit);
        Assert.Contains("Dry run", capture.Stdout.ToString());

        await _importService.Received(1).ImportAsync(
            Arg.Is<McpImportRequest>(r => r.DryRun == true),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportCommand_AgentClaude_PassesAgentKeyToService()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync(["mcp", "import", "--agent", "claude"]);

        Assert.Equal(0, exit);
        await _importService.Received(1).ImportAsync(
            Arg.Is<McpImportRequest>(r => r.AgentKey == "claude"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportCommand_AgentAll_PassesNullAgentKeyToService()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync(["mcp", "import", "--agent", "all"]);

        Assert.Equal(0, exit);
        await _importService.Received(1).ImportAsync(
            Arg.Is<McpImportRequest>(r => r.AgentKey == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportCommand_UnknownAgent_ReturnsExitCode1()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync(["mcp", "import", "--agent", "unknown-agent"]);

        Assert.Equal(1, exit);
        await _importService.DidNotReceive().ImportAsync(
            Arg.Any<McpImportRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportCommand_NoServersFound_ReturnsSuccess()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync(["mcp", "import"]);

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task ImportCommand_WithImportedServers_PrintsImportedSymbol()
    {
        var def = new McpServerDefinition("github",
            new McpTransportConfig(McpTransportKind.Stdio, "gh-mcp"),
            new NoneAuthConfig(), null, null, null);
        var fp = McpServerImportService.ComputeFingerprint(def);
        var conn = new McpImportedConnection("claude", "global", "github", def, fp,
            McpImportCandidateStatus.Importable, null);
        var sourceResult = new McpImportSourceResult("claude", "global", [conn]);
        _importService.ImportAsync(Arg.Any<McpImportRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result<McpImportReport, Error>.Ok(new McpImportReport([sourceResult], 1, 0, 0, 0)));

        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync(["mcp", "import"]);

        Assert.Equal(0, exit);
        var output = capture.Stdout.ToString();
        Assert.Contains("github", output);
        Assert.Contains("+", output);
    }

    [Fact]
    public async Task ImportCommand_WithConflict_PrintsConflictSymbol()
    {
        var conn = new McpImportedConnection("claude", "global", "conflict-server", null,
            string.Empty, McpImportCandidateStatus.SkippedConflict,
            "conflict — different configuration already exists");
        var sourceResult = new McpImportSourceResult("claude", "global", [conn]);
        _importService.ImportAsync(Arg.Any<McpImportRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result<McpImportReport, Error>.Ok(new McpImportReport([sourceResult], 0, 0, 1, 1)));

        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync(["mcp", "import"]);

        Assert.Equal(0, exit);
        var output = capture.Stdout.ToString();
        Assert.Contains("~", output);
        Assert.Contains("conflict-server", output);
    }

    [Fact]
    public async Task ImportCommand_WithDuplicate_PrintsEqualSymbol()
    {
        var conn = new McpImportedConnection("claude", "global", "my-server", null,
            string.Empty, McpImportCandidateStatus.SkippedDuplicate, "already present");
        var sourceResult = new McpImportSourceResult("claude", "global", [conn]);
        _importService.ImportAsync(Arg.Any<McpImportRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result<McpImportReport, Error>.Ok(new McpImportReport([sourceResult], 0, 1, 1, 0)));

        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync(["mcp", "import"]);

        Assert.Equal(0, exit);
        var output = capture.Stdout.ToString();
        Assert.Contains("=", output);
        Assert.Contains("my-server", output);
    }

    [Fact]
    public async Task ImportCommand_ScopeProject_PassesScopeToService()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync(["mcp", "import", "--scope", "project",
            "--project-root", "/tmp/myproject"]);

        Assert.Equal(0, exit);
        await _importService.Received(1).ImportAsync(
            Arg.Is<McpImportRequest>(r =>
                r.Scope == McpImportScope.Project &&
                r.ProjectRoot == "/tmp/myproject"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportCommand_ScopeProject_NoProjectRoot_ReturnsExitCode1()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync(["mcp", "import", "--scope", "project"]);

        Assert.Equal(1, exit);
        Assert.Contains("--project-root", capture.Stderr.ToString());
        await _importService.DidNotReceive().ImportAsync(
            Arg.Any<McpImportRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportCommand_ScopeAll_NoProjectRoot_ReturnsExitCode1()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync(["mcp", "import", "--scope", "all"]);

        Assert.Equal(1, exit);
        Assert.Contains("--project-root", capture.Stderr.ToString());
        await _importService.DidNotReceive().ImportAsync(
            Arg.Any<McpImportRequest>(), Arg.Any<CancellationToken>());
    }

    private sealed class ConsoleCapture : IDisposable
    {
        private readonly TextWriter _origOut;
        private readonly TextWriter _origErr;
        public StringBuilder Stdout { get; } = new();
        public StringBuilder Stderr { get; } = new();

        public ConsoleCapture()
        {
            _origOut = Console.Out;
            _origErr = Console.Error;
            Console.SetOut(new StringWriter(Stdout));
            Console.SetError(new StringWriter(Stderr));
        }

        public void Dispose()
        {
            Console.SetOut(_origOut);
            Console.SetError(_origErr);
        }
    }
}
