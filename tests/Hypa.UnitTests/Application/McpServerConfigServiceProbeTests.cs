using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Mcp;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Application;

[Trait("Category", "McpServerConfigServiceProbe")]
public sealed class McpServerConfigServiceProbeTests
{
    private readonly IMcpServerConfigReader _reader = Substitute.For<IMcpServerConfigReader>();
    private readonly IMcpServerConfigWriter _writer = Substitute.For<IMcpServerConfigWriter>();
    private readonly McpConfigValidationService _validator = new();
    private readonly IMcpServerProbe _probe = Substitute.For<IMcpServerProbe>();

    private McpServerConfigService Sut => new(_reader, _writer, _validator, _probe);

    private static readonly McpServerAddRequest RemoteNoneRequest = new(
        Name: "remote",
        Transport: "streamableHttp",
        Endpoint: "https://example.com",
        AuthType: "none",
        Auth: new McpServerAddAuthOptions(),
        Tls: null,
        ConnectTimeoutSeconds: null,
        RequestTimeoutSeconds: null,
        Replace: false,
        DryRun: false);

    private static readonly McpServerAddRequest StdioNoneRequest = new(
        Name: "local",
        Transport: "stdio",
        Endpoint: "hypa serve",
        AuthType: "none",
        Auth: new McpServerAddAuthOptions(),
        Tls: null,
        ConnectTimeoutSeconds: null,
        RequestTimeoutSeconds: null,
        Replace: false,
        DryRun: false);

    private static McpServerProbeResult Reachable =>
        new(McpServerProbeStatus.Reachable, "Reachable: tools/list succeeded.");

    private static McpServerProbeResult AuthRequired =>
        new(McpServerProbeStatus.AuthRequired, "Server returned 401 Unauthorized.",
            new McpAuthGuidance("bearer", null, null, null, null,
                ["hypa mcp auth check --server remote"]));

    public McpServerConfigServiceProbeTests()
    {
        _reader.ReadEditableAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([]));
        _writer.WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>())
            .Returns(Result<Unit, Error>.Ok(Unit.Value));
        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(Reachable);
    }

    [Fact]
    public async Task AddAsync_RemoteWithProbe_ReachableWritesOnce()
    {
        var result = await Sut.AddAsync(RemoteNoneRequest, default);

        Assert.True(result.Success);
        await _writer.Received(1).WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), default);
        Assert.NotNull(result.Probe);
        Assert.Equal(McpServerProbeStatus.Reachable, result.Probe.Status);
    }

    [Fact]
    public async Task AddAsync_RemoteWithProbe_AuthRequiredDoesNotWrite()
    {
        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(AuthRequired);

        var result = await Sut.AddAsync(RemoteNoneRequest, default);

        Assert.False(result.Success);
        await _writer.DidNotReceive().WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), default);
        Assert.Contains(result.Errors, e => e.StartsWith("AuthRequired:", StringComparison.Ordinal));
        Assert.NotNull(result.Probe);
    }

    [Fact]
    public async Task AddAsync_RemoteWithProbe_TimeoutDoesNotWrite()
    {
        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new McpServerProbeResult(McpServerProbeStatus.Timeout, "Connection timed out."));

        var result = await Sut.AddAsync(RemoteNoneRequest, default);

        Assert.False(result.Success);
        await _writer.DidNotReceive().WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), default);
        Assert.Contains(result.Errors, e => e.StartsWith("Timeout:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddAsync_RemoteWithProbe_ConnectionFailedDoesNotWrite()
    {
        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new McpServerProbeResult(McpServerProbeStatus.ConnectionFailed, "Failed to reach 'remote'."));

        var result = await Sut.AddAsync(RemoteNoneRequest, default);

        Assert.False(result.Success);
        await _writer.DidNotReceive().WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), default);
        Assert.Contains(result.Errors, e => e.StartsWith("ConnectionFailed:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddAsync_RemoteWithProbe_InvalidConfigDoesNotWrite()
    {
        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new McpServerProbeResult(McpServerProbeStatus.InvalidConfig, "Invalid config."));

        var result = await Sut.AddAsync(RemoteNoneRequest, default);

        Assert.False(result.Success);
        await _writer.DidNotReceive().WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), default);
        Assert.Contains(result.Errors, e => e.StartsWith("InvalidConfig:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddAsync_RemoteWithProbe_UnknownDoesNotWrite()
    {
        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new McpServerProbeResult(McpServerProbeStatus.Unknown, "HttpClient"));

        var result = await Sut.AddAsync(RemoteNoneRequest, default);

        Assert.False(result.Success);
        await _writer.DidNotReceive().WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), default);
        Assert.Contains(result.Errors, e => e.StartsWith("ProbeFailed:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddAsync_StdioTransport_DoesNotProbe_AndWrites()
    {
        var result = await Sut.AddAsync(StdioNoneRequest, default);

        Assert.True(result.Success);
        await _probe.DidNotReceive().ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>());
        await _writer.Received(1).WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), default);
    }

    [Fact]
    public async Task AddAsync_DryRun_DoesNotProbeOrWrite()
    {
        var request = RemoteNoneRequest with { DryRun = true };

        var result = await Sut.AddAsync(request, default);

        Assert.True(result.Success);
        await _probe.DidNotReceive().ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>());
        await _writer.DidNotReceive().WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), default);
    }

    [Fact]
    public async Task AddAsync_SkipProbe_DoesNotProbe_AndWrites()
    {
        var request = RemoteNoneRequest with { SkipProbe = true };

        var result = await Sut.AddAsync(request, default);

        Assert.True(result.Success);
        await _probe.DidNotReceive().ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>());
        await _writer.Received(1).WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), default);
    }

    [Fact]
    public async Task AddAsync_InvalidConfig_NeverProbes()
    {
        var request = new McpServerAddRequest(
            "s", "streamableHttp", "https://example.com", "bearer",
            new McpServerAddAuthOptions(),
            null, null, null, false, false);

        var result = await Sut.AddAsync(request, default);

        Assert.False(result.Success);
        await _probe.DidNotReceive().ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddAsync_DuplicateName_NeverProbes()
    {
        var existing = new McpServerDefinition(
            "remote",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new NoneAuthConfig());
        _reader.ReadEditableAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([existing]));

        var result = await Sut.AddAsync(RemoteNoneRequest, default);

        Assert.False(result.Success);
        await _probe.DidNotReceive().ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddAsync_PreservesExistingServers_OnReachableProbe()
    {
        var existing = new McpServerDefinition(
            "existing",
            new McpTransportConfig(McpTransportKind.Stdio, "cmd"),
            new NoneAuthConfig());
        _reader.ReadEditableAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([existing]));

        var result = await Sut.AddAsync(RemoteNoneRequest, default);

        Assert.True(result.Success);
        await _writer.Received(1).WriteAsync(
            Arg.Is<IReadOnlyList<McpServerDefinition>>(list => list.Count == 2),
            default);
    }

    [Fact]
    public async Task AddAsync_RemoteWithProbe_AuthRequired_ResultCarriesGuidance()
    {
        var guidance = new McpAuthGuidance("bearer", null, null, null, null, null);
        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new McpServerProbeResult(McpServerProbeStatus.AuthRequired, "auth required", guidance));

        var result = await Sut.AddAsync(RemoteNoneRequest, default);

        Assert.NotNull(result.Probe);
        Assert.Equal("bearer", result.Probe!.AuthGuidance!.SuggestedAuthMode);
    }

    [Fact]
    public async Task AddAsync_RemoteProbeSuccess_WriteFailure_IncludesProbeResult()
    {
        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(Reachable);
        _writer.WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>())
            .Returns(Result<Unit, Error>.Fail(new Error("WriteFailed", "disk full")));

        var result = await Sut.AddAsync(RemoteNoneRequest, default);

        Assert.False(result.Success);
        Assert.NotNull(result.Probe);
        Assert.Equal(McpServerProbeStatus.Reachable, result.Probe!.Status);
    }

    [Fact]
    public async Task AddAsync_RemoteWithProbe_FailureDoesNotMutateExistingConfig()
    {
        var existing = new McpServerDefinition(
            "existing",
            new McpTransportConfig(McpTransportKind.Stdio, "cmd"),
            new NoneAuthConfig());
        _reader.ReadEditableAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([existing]));

        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(AuthRequired);

        var request = RemoteNoneRequest with { Replace = true };
        var result = await Sut.AddAsync(request, default);

        Assert.False(result.Success);
        await _writer.DidNotReceive().WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), default);
    }
}
