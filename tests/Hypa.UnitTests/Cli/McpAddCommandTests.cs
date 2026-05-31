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

[Trait("Category", "McpAddCommand")]
[Collection("SequentialEnvTests")]
public sealed class McpAddCommandTests
{
    private readonly IMcpServerConfigReader _reader = Substitute.For<IMcpServerConfigReader>();
    private readonly IMcpServerConfigWriter _writer = Substitute.For<IMcpServerConfigWriter>();
    private readonly IMcpServerDefinitionRepository _serverRepo = Substitute.For<IMcpServerDefinitionRepository>();
    private readonly IMcpAuthProvider _authProvider = Substitute.For<IMcpAuthProvider>();
    private readonly IMcpServerProbe _probe = Substitute.For<IMcpServerProbe>();

    public McpAddCommandTests()
    {
        _reader.ReadEditableAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([]));
        _writer.WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>())
            .Returns(Result<Unit, Error>.Ok(Unit.Value));
        _serverRepo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([]));
        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new McpServerProbeResult(McpServerProbeStatus.Reachable, "Reachable: tools/list succeeded."));
    }

    private RootCommand BuildRoot()
    {
        var validator = new McpConfigValidationService();
        var configService = new McpServerConfigService(_reader, _writer, validator, _probe);
        var dispatcher = Substitute.For<IMcpDispatcher>();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        var proxyService = new McpProxyService(dispatcher, new McpResponseCompressionService(), new McpToolSearchIndex(), clock);
        var command = new McpCommand(proxyService, _serverRepo, _authProvider, configService, NullLogger<McpCommand>.Instance);
        var root = new RootCommand();
        root.AddCommand(command.Build());
        return root;
    }

    // Flag-driven success paths

    [Fact]
    public async Task FlagDriven_Stdio_None_Succeeds()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stdout = capture.Stdout;

        var exit = await root.InvokeAsync(["mcp", "add", "local",
            "--transport", "stdio", "--endpoint", "hypa serve", "--auth", "none"]);

        Assert.Equal(0, exit);
        Assert.Contains("Added MCP server: local", stdout.ToString());
        Assert.Contains("Run: hypa mcp auth check --server local", stdout.ToString());
    }

    [Fact]
    public async Task FlagDriven_ApiKey_WithEnvRef_Succeeds()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stdout = capture.Stdout;

        var exit = await root.InvokeAsync(["mcp", "add", "api-server",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com",
            "--auth", "apiKey",
            "--header-name", "x-api-key",
            "--value-ref", "env:MY_KEY"]);

        Assert.Equal(0, exit);
        Assert.Contains("Added MCP server: api-server", stdout.ToString());
    }

    [Fact]
    public async Task FlagDriven_Bearer_WithFileRef_Succeeds()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stdout = capture.Stdout;

        var exit = await root.InvokeAsync(["mcp", "add", "bearer-server",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com",
            "--auth", "bearer",
            "--token-ref", "file:/run/secrets/token"]);

        Assert.Equal(0, exit);
        Assert.Equal(0, exit);
        Assert.Contains("Added MCP server: bearer-server", stdout.ToString());
    }

    [Fact]
    public async Task FlagDriven_TransportAlias_Http_NormalizedToHttpAutoDetect()
    {
        var root = BuildRoot();

        var exit = await root.InvokeAsync(["mcp", "add", "s",
            "--transport", "http",
            "--endpoint", "https://example.com",
            "--auth", "none"]);

        Assert.Equal(0, exit);
        await _writer.Received(1).WriteAsync(
            Arg.Is<IReadOnlyList<McpServerDefinition>>(list =>
                list[0].Transport.Kind == McpTransportKind.HttpAutoDetect),
            Arg.Any<CancellationToken>());
    }

    // Dry-run output

    [Fact]
    public async Task DryRun_PrintsJsonAndDoesNotWrite()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stdout = capture.Stdout;

        var exit = await root.InvokeAsync(["mcp", "add", "preview",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com",
            "--auth", "bearer",
            "--token-ref", "env:TOKEN",
            "--dry-run"]);

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains("preview", output);
        Assert.Contains("streamableHttp", output);
        Assert.Contains("bearer", output);
        Assert.Contains("env:TOKEN", output);
        await _writer.DidNotReceive().WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DryRun_OutputDoesNotContainNullFields()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stdout = capture.Stdout;

        await root.InvokeAsync(["mcp", "add", "preview",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com",
            "--auth", "bearer",
            "--token-ref", "env:TOKEN",
            "--dry-run"]);

        Assert.DoesNotContain("null", stdout.ToString());
    }

    // Invalid option combinations

    [Fact]
    public async Task DryRunAndLogin_RejectsWithInvalidOption()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stderr = capture.Stderr;

        var exit = await root.InvokeAsync(["mcp", "add", "s",
            "--transport", "stdio", "--endpoint", "cmd", "--auth", "none",
            "--dry-run", "--login"]);

        Assert.Equal(1, exit);
        Assert.Contains("InvalidOption", stderr.ToString());
        Assert.Contains("--dry-run", stderr.ToString());
        Assert.Contains("--login", stderr.ToString());
    }

    [Fact]
    public async Task LoginWithNonDeviceCodeAuth_RejectsWithInvalidOption()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stderr = capture.Stderr;

        var exit = await root.InvokeAsync(["mcp", "add", "s",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com",
            "--auth", "bearer",
            "--token-ref", "env:T",
            "--login"]);

        Assert.Equal(1, exit);
        Assert.Contains("InvalidOption", stderr.ToString());
        Assert.Contains("oauth2DeviceCode", stderr.ToString());
    }

    // Non-interactive missing option errors

    [Fact]
    public async Task MissingAuth_DefaultsToNone()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stdout = capture.Stdout;

        var exit = await root.InvokeAsync(["mcp", "add", "s",
            "--transport", "stdio", "--endpoint", "cmd"]);

        Assert.Equal(0, exit);
        Assert.Contains("Added MCP server: s", stdout.ToString());
    }

    [Fact]
    public async Task NonInteractive_MissingTransport_RejectsMissingOption()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stderr = capture.Stderr;

        var exit = await root.InvokeAsync(["mcp", "add", "s",
            "--endpoint", "cmd", "--auth", "none"]);

        Assert.Equal(1, exit);
        Assert.Contains("MissingOption", stderr.ToString());
        Assert.Contains("--transport", stderr.ToString());
    }

    [Fact]
    public async Task NonInteractive_MissingEndpoint_RejectsMissingOption()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stderr = capture.Stderr;

        var exit = await root.InvokeAsync(["mcp", "add", "s",
            "--transport", "stdio", "--auth", "none"]);

        Assert.Equal(1, exit);
        Assert.Contains("MissingOption", stderr.ToString());
        Assert.Contains("--endpoint", stderr.ToString());
    }

    // Secret ref validation

    [Fact]
    public async Task BareSecretRef_RejectsWithInvalidSecretRef()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stderr = capture.Stderr;

        var exit = await root.InvokeAsync(["mcp", "add", "s",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com",
            "--auth", "bearer",
            "--token-ref", "MY_RAW_TOKEN"]);

        Assert.Equal(1, exit);
        Assert.Contains("InvalidSecretRef", stderr.ToString());
    }

    // Duplicate handling

    [Fact]
    public async Task Duplicate_WithoutReplace_RejectsDuplicateServer()
    {
        var existing = new McpServerDefinition(
            "local",
            new McpTransportConfig(McpTransportKind.Stdio, "cmd"),
            new NoneAuthConfig());
        _reader.ReadEditableAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([existing]));

        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stderr = capture.Stderr;

        var exit = await root.InvokeAsync(["mcp", "add", "local",
            "--transport", "stdio", "--endpoint", "hypa serve", "--auth", "none"]);

        Assert.Equal(1, exit);
        Assert.Contains("DuplicateServer", stderr.ToString());
        Assert.Contains("--replace", stderr.ToString());
    }

    [Fact]
    public async Task Duplicate_WithReplace_Succeeds()
    {
        var existing = new McpServerDefinition(
            "local",
            new McpTransportConfig(McpTransportKind.Stdio, "old-cmd"),
            new NoneAuthConfig());
        _reader.ReadEditableAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([existing]));

        var root = BuildRoot();

        var exit = await root.InvokeAsync(["mcp", "add", "local",
            "--transport", "stdio", "--endpoint", "hypa serve", "--auth", "none",
            "--replace"]);

        Assert.Equal(0, exit);
        await _writer.Received(1).WriteAsync(
            Arg.Is<IReadOnlyList<McpServerDefinition>>(list =>
                list.Count == 1 && list[0].Transport.Endpoint == "hypa serve"),
            Arg.Any<CancellationToken>());
    }

    // mTLS required refs

    [Fact]
    public async Task Mtls_MissingBothRefs_RejectsMissingOption()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stderr = capture.Stderr;

        var exit = await root.InvokeAsync(["mcp", "add", "s",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com",
            "--auth", "mtls"]);

        Assert.Equal(1, exit);
        Assert.Contains("MissingOption", stderr.ToString());
        Assert.Contains("--client-cert-ref", stderr.ToString());
        Assert.Contains("--client-key-ref", stderr.ToString());
    }

    [Fact]
    public async Task Mtls_WithBothRefs_Succeeds()
    {
        var root = BuildRoot();

        var exit = await root.InvokeAsync(["mcp", "add", "s",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com",
            "--auth", "mtls",
            "--client-cert-ref", "env:CERT",
            "--client-key-ref", "env:KEY"]);

        Assert.Equal(0, exit);
    }

    // Timeout validation

    [Fact]
    public async Task NegativeConnectTimeout_Rejects()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stderr = capture.Stderr;

        var exit = await root.InvokeAsync(["mcp", "add", "s",
            "--transport", "stdio", "--endpoint", "cmd", "--auth", "none",
            "--connect-timeout-seconds", "-1"]);

        Assert.Equal(1, exit);
        Assert.Contains("connect-timeout-seconds", stderr.ToString());
    }

    [Fact]
    public async Task ZeroRequestTimeout_Rejects()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stderr = capture.Stderr;

        var exit = await root.InvokeAsync(["mcp", "add", "s",
            "--transport", "stdio", "--endpoint", "cmd", "--auth", "none",
            "--request-timeout-seconds", "0"]);

        Assert.Equal(1, exit);
        Assert.Contains("request-timeout-seconds", stderr.ToString());
    }

    // OAuth2 device-code login delegation

    [Fact]
    public async Task Login_OAuth2DeviceCode_DelegatesAuthAfterWrite()
    {
        var oauth2Def = new McpServerDefinition(
            "github",
            new McpTransportConfig(McpTransportKind.Http, "https://mcp.github.example.com"),
            new OAuth2DeviceCodeConfig(
                "https://github.com/login/device/code",
                "https://github.com/login/oauth/access_token",
                "Iv1.example"));

        _serverRepo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([oauth2Def]));
        _authProvider.GetAuthContextAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new McpAuthContext(new Dictionary<string, string>()));

        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stdout = capture.Stdout;

        var exit = await root.InvokeAsync(["mcp", "add", "github",
            "--transport", "streamableHttp",
            "--endpoint", "https://mcp.github.example.com",
            "--auth", "oauth2DeviceCode",
            "--auth-url", "https://github.com/login/device/code",
            "--token-url", "https://github.com/login/oauth/access_token",
            "--client-id", "Iv1.example",
            "--login"]);

        Assert.Equal(0, exit);
        await _writer.Received(1).WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>());
        await _authProvider.Received(1).GetAuthContextAsync(
            Arg.Is<McpServerDefinition>(d => d.Name == "github"),
            Arg.Any<CancellationToken>());
        var outStr = stdout.ToString();
        Assert.Contains("Added MCP server: github", outStr);
        Assert.Contains("Authenticated: github", outStr);
        Assert.Contains("Run: hypa mcp schema --server github", outStr);
    }

    [Fact]
    public async Task Login_OAuth2LoginFailure_KeepsConfigAndPrintsRecovery()
    {
        var oauth2Def = new McpServerDefinition(
            "github",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new OAuth2DeviceCodeConfig(
                "https://auth/device",
                "https://auth/token",
                "client-id"));

        _serverRepo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([oauth2Def]));
        _authProvider.GetAuthContextAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns<McpAuthContext>(_ => throw new InvalidOperationException("device flow timed out"));

        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stderr = capture.Stderr;

        var exit = await root.InvokeAsync(["mcp", "add", "github",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com",
            "--auth", "oauth2DeviceCode",
            "--auth-url", "https://auth/device",
            "--token-url", "https://auth/token",
            "--client-id", "client-id",
            "--login"]);

        // Config was written even though login failed
        await _writer.Received(1).WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>());
        var errOutput = stderr.ToString();
        Assert.Contains("AuthLoginFailed", errOutput);
        Assert.Contains("auth login failed for 'github'.", errOutput);
        Assert.Contains("hypa mcp auth login --server github", errOutput);
    }

    // Probe-aware tests (Step G)

    [Fact]
    public async Task Remote_DefaultProbe_Reachable_Succeeds()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stdout = capture.Stdout;

        var exit = await root.InvokeAsync(["mcp", "add", "remote",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com",
            "--auth", "none"]);

        Assert.Equal(0, exit);
        Assert.Contains("Added MCP server: remote", stdout.ToString());
    }

    [Fact]
    public async Task Remote_MissingAuth_DefaultsToNoneAndProbes()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stdout = capture.Stdout;

        var exit = await root.InvokeAsync(["mcp", "add", "remote",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com"]);

        Assert.Equal(0, exit);
        Assert.Contains("Added MCP server: remote", stdout.ToString());
        await _probe.Received(1).ProbeAsync(
            Arg.Is<McpServerDefinition>(s => s.Auth is NoneAuthConfig),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Remote_DefaultProbe_AuthRequired_FailsAndPrintsGuidance()
    {
        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new McpServerProbeResult(
                McpServerProbeStatus.AuthRequired,
                "Server returned 401 Unauthorized.",
                new McpAuthGuidance(null, null, null, null, null,
                    ["hypa mcp add remote --auth bearer --token-ref env:TOKEN",
                     "hypa mcp add remote --auth oauth2DeviceCode <required options> --login"])));

        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stderr = capture.Stderr;

        var exit = await root.InvokeAsync(["mcp", "add", "remote",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com",
            "--auth", "none"]);

        Assert.Equal(1, exit);
        var errStr = stderr.ToString();
        Assert.Contains("AuthRequired:", errStr);
        Assert.Contains("Try one of:", errStr);
        Assert.Contains("hypa mcp add remote --auth bearer", errStr);
        Assert.Contains("hypa mcp add remote --auth oauth2DeviceCode", errStr);
        Assert.Contains("Use --no-probe", errStr);
    }

    [Fact]
    public async Task Remote_NoProbe_SkipsProbeAndWrites()
    {
        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns<McpServerProbeResult>(_ => throw new InvalidOperationException("probe must not be called"));

        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stdout = capture.Stdout;

        var exit = await root.InvokeAsync(["mcp", "add", "remote",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com",
            "--auth", "none",
            "--no-probe"]);

        Assert.Equal(0, exit);
        Assert.Contains("Added MCP server: remote", stdout.ToString());
    }

    [Fact]
    public async Task Remote_Timeout_FailsAndPrintsRetryHint()
    {
        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new McpServerProbeResult(McpServerProbeStatus.Timeout, "Connection timed out."));

        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stderr = capture.Stderr;

        var exit = await root.InvokeAsync(["mcp", "add", "remote",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com",
            "--auth", "none"]);

        Assert.Equal(1, exit);
        var errStr = stderr.ToString();
        Assert.Contains("Timeout:", errStr);
        Assert.Contains("--no-probe", errStr);
    }

    [Fact]
    public async Task Remote_ConnectionFailed_FailsAndPrintsRetryHint()
    {
        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new McpServerProbeResult(McpServerProbeStatus.ConnectionFailed, "Failed to reach 'remote'."));

        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stderr = capture.Stderr;

        var exit = await root.InvokeAsync(["mcp", "add", "remote",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com",
            "--auth", "none"]);

        Assert.Equal(1, exit);
        var errStr = stderr.ToString();
        Assert.Contains("ConnectionFailed:", errStr);
        Assert.Contains("--no-probe", errStr);
    }

    [Fact]
    public async Task Stdio_DoesNotProbe()
    {
        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns<McpServerProbeResult>(_ => throw new InvalidOperationException("probe must not be called"));

        var root = BuildRoot();

        var exit = await root.InvokeAsync(["mcp", "add", "local",
            "--transport", "stdio",
            "--endpoint", "hypa serve",
            "--auth", "none"]);

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task DryRun_DoesNotProbe_AndDoesNotWrite()
    {
        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns<McpServerProbeResult>(_ => throw new InvalidOperationException("probe must not be called"));

        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stdout = capture.Stdout;

        var exit = await root.InvokeAsync(["mcp", "add", "remote",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com",
            "--auth", "none",
            "--dry-run"]);

        Assert.Equal(0, exit);
        await _writer.DidNotReceive().WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Login_OAuth2DeviceCode_SkipsProbe_AndPersists()
    {
        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns<McpServerProbeResult>(_ => throw new InvalidOperationException("probe must not be called"));

        var oauth2Def = new McpServerDefinition(
            "github",
            new McpTransportConfig(McpTransportKind.Http, "https://mcp.github.example.com"),
            new OAuth2DeviceCodeConfig(
                "https://github.com/login/device/code",
                "https://github.com/login/oauth/access_token",
                "Iv1.example"));
        _serverRepo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([oauth2Def]));
        _authProvider.GetAuthContextAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new McpAuthContext(new Dictionary<string, string>()));

        var root = BuildRoot();

        var exit = await root.InvokeAsync(["mcp", "add", "github",
            "--transport", "streamableHttp",
            "--endpoint", "https://mcp.github.example.com",
            "--auth", "oauth2DeviceCode",
            "--auth-url", "https://github.com/login/device/code",
            "--token-url", "https://github.com/login/oauth/access_token",
            "--client-id", "Iv1.example",
            "--login"]);

        Assert.Equal(0, exit);
        await _writer.Received(1).WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Login_NonDeviceCode_RejectedBeforeProbe()
    {
        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns<McpServerProbeResult>(_ => throw new InvalidOperationException("probe must not be called"));

        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stderr = capture.Stderr;

        var exit = await root.InvokeAsync(["mcp", "add", "s",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com",
            "--auth", "bearer",
            "--token-ref", "env:T",
            "--login"]);

        Assert.Equal(1, exit);
        Assert.Contains("InvalidOption", stderr.ToString());
    }

    [Fact]
    public async Task BareSecretRef_RejectedBeforeProbe()
    {
        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns<McpServerProbeResult>(_ => throw new InvalidOperationException("probe must not be called"));

        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stderr = capture.Stderr;

        var exit = await root.InvokeAsync(["mcp", "add", "s",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com",
            "--auth", "bearer",
            "--token-ref", "MY_RAW_TOKEN"]);

        Assert.Equal(1, exit);
        Assert.Contains("InvalidSecretRef", stderr.ToString());
    }

    [Fact]
    public async Task Probe_FailureDoesNotMutateConfig()
    {
        var existing = new McpServerDefinition(
            "existing",
            new McpTransportConfig(McpTransportKind.Stdio, "cmd"),
            new NoneAuthConfig());
        _reader.ReadEditableAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([existing]));

        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new McpServerProbeResult(McpServerProbeStatus.AuthRequired, "auth required"));

        var root = BuildRoot();

        var exit = await root.InvokeAsync(["mcp", "add", "remote",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com",
            "--auth", "none"]);

        Assert.Equal(1, exit);
        await _writer.DidNotReceive().WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Probe_OutputDoesNotContainNullForGuidanceFields()
    {
        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new McpServerProbeResult(
                McpServerProbeStatus.AuthRequired,
                "Server returned 401 Unauthorized.",
                new McpAuthGuidance(null, null, null, null, null, null)));

        var root = BuildRoot();
        using var capture = new ConsoleCapture(); var stderr = capture.Stderr;

        await root.InvokeAsync(["mcp", "add", "remote",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com",
            "--auth", "none"]);

        Assert.DoesNotContain("null", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
    }

}

[Trait("Category", "McpAddCommandOAuth")]
[Collection("SequentialEnvTests")]
public sealed class McpAddCommandOAuthTests
{
    private readonly IMcpServerConfigReader _reader = Substitute.For<IMcpServerConfigReader>();
    private readonly IMcpServerConfigWriter _writer = Substitute.For<IMcpServerConfigWriter>();
    private readonly IMcpServerDefinitionRepository _serverRepo = Substitute.For<IMcpServerDefinitionRepository>();
    private readonly IMcpAuthProvider _authProvider = Substitute.For<IMcpAuthProvider>();
    private readonly IMcpServerProbe _probe = Substitute.For<IMcpServerProbe>();
    private readonly IMcpBrowserOAuthFlowProvider _oauthProvider = Substitute.For<IMcpBrowserOAuthFlowProvider>();

    private static readonly McpAuthGuidance McpOAuthGuidance = new(
        SuggestedAuthMode: "mcpOAuth",
        AuthorizationUrl: null,
        TokenUrl: null,
        ClientId: null,
        Scopes: null,
        NextCommands: ["hypa mcp auth login --server test-server"]);

    public McpAddCommandOAuthTests()
    {
        _reader.ReadEditableAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([]));
        _writer.WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>())
            .Returns(Result<Unit, Error>.Ok(Unit.Value));
        _serverRepo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([]));

        // Probe returns AuthRequired + mcpOAuth guidance by default
        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new McpServerProbeResult(
                McpServerProbeStatus.AuthRequired,
                "401 Unauthorized",
                McpOAuthGuidance));
    }

    private RootCommand BuildRoot()
    {
        var validator = new McpConfigValidationService();
        var configService = new McpServerConfigService(_reader, _writer, validator, _probe);
        var dispatcher = Substitute.For<IMcpDispatcher>();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        var proxyService = new McpProxyService(dispatcher, new McpResponseCompressionService(), new McpToolSearchIndex(), clock);
        var command = new McpCommand(
            proxyService, _serverRepo, _authProvider, configService,
            NullLogger<McpCommand>.Instance,
            mcpServerImportService: null,
            browserOAuthFlowProvider: _oauthProvider);
        var root = new RootCommand();
        root.AddCommand(command.Build());
        return root;
    }

    [Fact]
    public async Task OAuthFlow_NonInteractive_ReturnsExitCode4()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync([
            "mcp", "add", "test-server",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com/mcp",
            "--auth", "none",
            "--non-interactive",
        ]);

        Assert.Equal(4, exit);
        Assert.Contains("OAuth", capture.Stderr.ToString());
    }

    [Fact]
    public async Task OAuthFlow_DryRun_ShowsDryRunOutputAndExits0()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync([
            "mcp", "add", "test-server",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com/mcp",
            "--auth", "none",
            "--dry-run",
        ]);

        Assert.Equal(0, exit);
        var stdout = capture.Stdout.ToString();
        Assert.Contains("dry-run", stdout);
        Assert.Contains("mcpOAuth", stdout);
    }

    [Fact]
    public async Task OAuthFlow_Success_CallsAddAsyncTwice_AndPrintsSuccess()
    {
        _oauthProvider.StartFlowAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<McpOAuthConfig>(),
                Arg.Any<McpBrowserOAuthOptions>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IProgress<string>?>())
            .Returns(new McpBrowserOAuthFlowResult(
                Succeeded: true,
                CompletedConfig: new McpOAuthConfig(),
                ToolCount: 12));

        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync([
            "mcp", "add", "test-server",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com/mcp",
            "--auth", "none",
        ]);

        Assert.Equal(0, exit);
        var stdout = capture.Stdout.ToString();
        Assert.Contains("12 tools", stdout);

        // Verify writer was called (second AddAsync persisted config)
        await _writer.Received(1).WriteAsync(
            Arg.Any<IReadOnlyList<McpServerDefinition>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OAuthFlow_FlowFailed_ReturnsExitCode1()
    {
        _oauthProvider.StartFlowAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<McpOAuthConfig>(),
                Arg.Any<McpBrowserOAuthOptions>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IProgress<string>?>())
            .Returns(new McpBrowserOAuthFlowResult(
                Succeeded: false,
                CompletedConfig: null,
                ToolCount: null,
                Error: "Timeout"));

        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync([
            "mcp", "add", "test-server",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com/mcp",
            "--auth", "none",
        ]);

        Assert.Equal(1, exit);
        Assert.Contains("Timeout", capture.Stderr.ToString());
    }

    [Fact]
    public async Task OAuthFlow_NoBrowserFlag_PassedToFlowProvider()
    {
        McpBrowserOAuthOptions? capturedOptions = null;
        _oauthProvider.StartFlowAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<McpOAuthConfig>(),
                Arg.Do<McpBrowserOAuthOptions>(o => capturedOptions = o),
                Arg.Any<CancellationToken>(),
                Arg.Any<IProgress<string>?>())
            .Returns(new McpBrowserOAuthFlowResult(true, new McpOAuthConfig(), 5));

        var root = BuildRoot();
        using var _ = new ConsoleCapture();

        await root.InvokeAsync([
            "mcp", "add", "test-server",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com/mcp",
            "--auth", "none",
            "--no-browser",
        ]);

        Assert.True(capturedOptions?.NoBrowser);
    }

    [Fact]
    public async Task OAuthFlow_Json_Success_OutputsJsonWithToolCount()
    {
        _oauthProvider.StartFlowAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<McpOAuthConfig>(),
                Arg.Any<McpBrowserOAuthOptions>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IProgress<string>?>())
            .Returns(new McpBrowserOAuthFlowResult(true, new McpOAuthConfig(), 7));

        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync([
            "mcp", "add", "test-server",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com/mcp",
            "--auth", "none",
            "--json",
        ]);

        Assert.Equal(0, exit);
        var stdout = capture.Stdout.ToString();
        Assert.Contains("\"success\": true", stdout);
        Assert.Contains("\"mcpOAuth\"", stdout);
        Assert.Contains("7", stdout);
    }

    [Fact]
    public async Task OAuthFlow_Json_NonInteractive_OutputsJsonAuthRequired()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync([
            "mcp", "add", "test-server",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com/mcp",
            "--auth", "none",
            "--non-interactive",
            "--json",
        ]);

        Assert.Equal(4, exit);
        var stdout = capture.Stdout.ToString();
        Assert.Contains("\"success\": false", stdout);
        Assert.Contains("\"AuthRequired\"", stdout);
        Assert.Contains("\"mcpOAuth\"", stdout);
    }

    [Fact]
    public async Task OAuthFlow_Progress_ReportsAuthUrl()
    {
        IProgress<string>? capturedProgress = null;
        _oauthProvider.StartFlowAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<McpOAuthConfig>(),
                Arg.Any<McpBrowserOAuthOptions>(),
                Arg.Any<CancellationToken>(),
                Arg.Do<IProgress<string>?>(p => capturedProgress = p))
            .Returns(new McpBrowserOAuthFlowResult(true, new McpOAuthConfig(), 3));

        var root = BuildRoot();
        using var _ = new ConsoleCapture();

        await root.InvokeAsync([
            "mcp", "add", "test-server",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com/mcp",
            "--auth", "none",
        ]);

        Assert.NotNull(capturedProgress);
    }

    [Fact]
    public async Task OAuthFlow_Json_SetsInteractiveFalse()
    {
        McpBrowserOAuthOptions? capturedOptions = null;
        _oauthProvider.StartFlowAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<McpOAuthConfig>(),
                Arg.Do<McpBrowserOAuthOptions>(o => capturedOptions = o),
                Arg.Any<CancellationToken>(),
                Arg.Any<IProgress<string>?>())
            .Returns(new McpBrowserOAuthFlowResult(true, new McpOAuthConfig(), 2));

        var root = BuildRoot();
        using var _ = new ConsoleCapture();

        await root.InvokeAsync([
            "mcp", "add", "test-server",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com/mcp",
            "--auth", "none",
            "--json",
        ]);

        Assert.NotNull(capturedOptions);
        Assert.False(capturedOptions!.Interactive);
    }

    [Fact]
    public async Task OAuthFlow_NonJson_SetsInteractiveTrue()
    {
        McpBrowserOAuthOptions? capturedOptions = null;
        _oauthProvider.StartFlowAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<McpOAuthConfig>(),
                Arg.Do<McpBrowserOAuthOptions>(o => capturedOptions = o),
                Arg.Any<CancellationToken>(),
                Arg.Any<IProgress<string>?>())
            .Returns(new McpBrowserOAuthFlowResult(true, new McpOAuthConfig(), 2));

        var root = BuildRoot();
        using var _ = new ConsoleCapture();

        await root.InvokeAsync([
            "mcp", "add", "test-server",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com/mcp",
            "--auth", "none",
        ]);

        Assert.NotNull(capturedOptions);
        Assert.True(capturedOptions!.Interactive);
    }

    [Fact]
    public async Task OAuthFlow_Json_NonInteractive_GuidanceOmitsDiscoveryFields()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        await root.InvokeAsync([
            "mcp", "add", "test-server",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com/mcp",
            "--auth", "none",
            "--non-interactive",
            "--json",
        ]);

        var stdout = capture.Stdout.ToString();
        Assert.Contains("mcpOAuth", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("discoveryUrl", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("supportsDynamicClientRegistration", stdout, StringComparison.OrdinalIgnoreCase);
    }
}

[Trait("Category", "McpAddCommandJson")]
[Collection("SequentialEnvTests")]
public sealed class McpAddCommandJsonTests
{
    private readonly IMcpServerConfigReader _reader = Substitute.For<IMcpServerConfigReader>();
    private readonly IMcpServerConfigWriter _writer = Substitute.For<IMcpServerConfigWriter>();
    private readonly IMcpServerDefinitionRepository _serverRepo = Substitute.For<IMcpServerDefinitionRepository>();
    private readonly IMcpAuthProvider _authProvider = Substitute.For<IMcpAuthProvider>();
    private readonly IMcpServerProbe _probe = Substitute.For<IMcpServerProbe>();

    public McpAddCommandJsonTests()
    {
        _reader.ReadEditableAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([]));
        _writer.WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>())
            .Returns(Result<Unit, Error>.Ok(Unit.Value));
        _serverRepo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([]));
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
        var command = new McpCommand(proxyService, _serverRepo, _authProvider, configService, NullLogger<McpCommand>.Instance);
        var root = new RootCommand();
        root.AddCommand(command.Build());
        return root;
    }

    [Fact]
    public async Task Json_OrdinarySuccess_OutputsJsonWithNameAndAuth()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync([
            "mcp", "add", "myserver",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com",
            "--auth", "none",
            "--json",
        ]);

        Assert.Equal(0, exit);
        var stdout = capture.Stdout.ToString();
        Assert.Contains("\"success\": true", stdout);
        Assert.Contains("\"myserver\"", stdout);
        Assert.Contains("\"none\"", stdout);
    }

    [Fact]
    public async Task Json_ProbeFailure_OutputsJsonError()
    {
        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new McpServerProbeResult(
                McpServerProbeStatus.AuthRequired,
                "Server returned 401 Unauthorized.",
                new McpAuthGuidance("bearer", null, null, null, null,
                    ["hypa mcp add myserver --auth bearer --token-ref env:TOKEN"])));

        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync([
            "mcp", "add", "myserver",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com",
            "--auth", "none",
            "--json",
        ]);

        Assert.Equal(1, exit);
        var stdout = capture.Stdout.ToString();
        Assert.Contains("\"success\": false", stdout);
        Assert.Contains("\"bearer\"", stdout);
    }

    [Fact]
    public async Task Json_ValidationFailure_OutputsJsonError()
    {
        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync([
            "mcp", "add", "myserver",
            "--transport", "streamableHttp",
            "--endpoint", "https://example.com",
            "--auth", "bearer",
            "--token-ref", "env:TOKEN",
            "--json",
        ]);

        Assert.Equal(0, exit);
        var stdout = capture.Stdout.ToString();
        Assert.Contains("\"success\": true", stdout);
        Assert.DoesNotContain("error", stdout, StringComparison.OrdinalIgnoreCase);
    }
}

[Trait("Category", "McpAuthLoginOAuth")]
[Collection("SequentialEnvTests")]
public sealed class McpAuthLoginOAuthTests
{
    private readonly IMcpServerConfigReader _reader = Substitute.For<IMcpServerConfigReader>();
    private readonly IMcpServerConfigWriter _writer = Substitute.For<IMcpServerConfigWriter>();
    private readonly IMcpServerDefinitionRepository _serverRepo = Substitute.For<IMcpServerDefinitionRepository>();
    private readonly IMcpAuthProvider _authProvider = Substitute.For<IMcpAuthProvider>();
    private readonly IMcpServerProbe _probe = Substitute.For<IMcpServerProbe>();
    private readonly IMcpBrowserOAuthFlowProvider _oauthProvider = Substitute.For<IMcpBrowserOAuthFlowProvider>();

    private static readonly McpServerDefinition OAuthServer = new(
        Name: "my-oauth-server",
        Transport: new McpTransportConfig(McpTransportKind.Http, "https://example.com/mcp"),
        Auth: new McpOAuthConfig(ClientId: "client-id"));

    public McpAuthLoginOAuthTests()
    {
        _reader.ReadEditableAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([]));
        _writer.WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>())
            .Returns(Result<Unit, Error>.Ok(Unit.Value));
        _serverRepo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([OAuthServer]));
        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new McpServerProbeResult(McpServerProbeStatus.Reachable, "ok"));
    }

    private RootCommand BuildRoot(IMcpBrowserOAuthFlowProvider? provider = null, bool noProvider = false)
    {
        var validator = new McpConfigValidationService();
        var configService = new McpServerConfigService(_reader, _writer, validator, _probe);
        var dispatcher = Substitute.For<IMcpDispatcher>();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        var proxyService = new McpProxyService(dispatcher, new McpResponseCompressionService(), new McpToolSearchIndex(), clock);
        var command = new McpCommand(
            proxyService, _serverRepo, _authProvider, configService,
            NullLogger<McpCommand>.Instance,
            mcpServerImportService: null,
            browserOAuthFlowProvider: noProvider ? null : (provider ?? _oauthProvider));
        var root = new RootCommand();
        root.AddCommand(command.Build());
        return root;
    }

    [Fact]
    public async Task AuthLogin_McpOAuth_Success_Prints_LoginSuccessful()
    {
        _oauthProvider.StartFlowAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<McpOAuthConfig>(),
                Arg.Any<McpBrowserOAuthOptions>(), Arg.Any<CancellationToken>(),
                Arg.Any<IProgress<string>?>())
            .Returns(new McpBrowserOAuthFlowResult(Succeeded: true, CompletedConfig: new McpOAuthConfig(), ToolCount: 5));

        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync(["mcp", "auth", "login", "--server", "my-oauth-server"]);

        Assert.Equal(0, exit);
        Assert.Contains("Login successful", capture.Stdout.ToString());
    }

    [Fact]
    public async Task AuthLogin_McpOAuth_FlowFailure_ReturnsExitCode1_AndPrintsError()
    {
        _oauthProvider.StartFlowAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<McpOAuthConfig>(),
                Arg.Any<McpBrowserOAuthOptions>(), Arg.Any<CancellationToken>(),
                Arg.Any<IProgress<string>?>())
            .Returns(new McpBrowserOAuthFlowResult(Succeeded: false, CompletedConfig: null, ToolCount: null, Error: "Timed out"));

        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync(["mcp", "auth", "login", "--server", "my-oauth-server"]);

        Assert.Equal(1, exit);
        Assert.Contains("Timed out", capture.Stderr.ToString());
    }

    [Fact]
    public async Task AuthLogin_McpOAuth_MissingProvider_ReturnsExitCode1()
    {
        var root = BuildRoot(noProvider: true);
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync(["mcp", "auth", "login", "--server", "my-oauth-server"]);

        Assert.Equal(1, exit);
        Assert.Contains("not available", capture.Stderr.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthLogin_McpOAuth_PassesTlsFromServerDefinition()
    {
        var tlsServer = OAuthServer with { Tls = new McpTlsConfig("/ca.pem", null, null) };
        _serverRepo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([tlsServer]));

        McpBrowserOAuthOptions? capturedOptions = null;
        _oauthProvider.StartFlowAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<McpOAuthConfig>(),
                Arg.Do<McpBrowserOAuthOptions>(o => capturedOptions = o),
                Arg.Any<CancellationToken>(),
                Arg.Any<IProgress<string>?>())
            .Returns(new McpBrowserOAuthFlowResult(true, new McpOAuthConfig(), 3));

        var root = BuildRoot();
        using var _ = new ConsoleCapture();

        await root.InvokeAsync(["mcp", "auth", "login", "--server", "my-oauth-server"]);

        Assert.NotNull(capturedOptions);
        Assert.Equal("/ca.pem", capturedOptions!.Tls?.CaCertPath);
    }

    [Fact]
    public async Task AuthLogin_McpOAuth_NonOAuthServer_ReturnsExitCode1_AndMentionsSupportedModes()
    {
        var bearerServer = OAuthServer with { Name = "bearer-server", Auth = new BearerAuthConfig("env:TOKEN") };
        _serverRepo.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([bearerServer]));

        var root = BuildRoot();
        using var capture = new ConsoleCapture();

        var exit = await root.InvokeAsync(["mcp", "auth", "login", "--server", "bearer-server"]);

        Assert.Equal(1, exit);
        Assert.Contains("oauth2DeviceCode", capture.Stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mcpOAuth", capture.Stderr.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthLogin_McpOAuth_ProgressIsPassedToFlowProvider()
    {
        IProgress<string>? capturedProgress = null;
        _oauthProvider.StartFlowAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<McpOAuthConfig>(),
                Arg.Any<McpBrowserOAuthOptions>(),
                Arg.Any<CancellationToken>(),
                Arg.Do<IProgress<string>?>(p => capturedProgress = p))
            .Returns(new McpBrowserOAuthFlowResult(true, new McpOAuthConfig(), 2));

        var root = BuildRoot();
        using var _ = new ConsoleCapture();

        await root.InvokeAsync(["mcp", "auth", "login", "--server", "my-oauth-server"]);

        Assert.NotNull(capturedProgress);
    }
}

internal sealed class ConsoleCapture : IDisposable
{
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;
    public readonly System.Text.StringBuilder Stdout = new();
    public readonly System.Text.StringBuilder Stderr = new();

    public ConsoleCapture()
    {
        Console.SetOut(new System.IO.StringWriter(Stdout));
        Console.SetError(new System.IO.StringWriter(Stderr));
    }

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
    }
}
