using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Mcp;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Application;

[Trait("Category", "McpServerConfig")]
public sealed class McpServerConfigServiceTests
{
    private readonly IMcpServerConfigReader _reader = Substitute.For<IMcpServerConfigReader>();
    private readonly IMcpServerConfigWriter _writer = Substitute.For<IMcpServerConfigWriter>();
    private readonly McpConfigValidationService _validator = new();
    private readonly IMcpServerProbe _probe = Substitute.For<IMcpServerProbe>();
    private McpServerConfigService Sut => new(_reader, _writer, _validator, _probe);

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

    public McpServerConfigServiceTests()
    {
        _reader.ReadEditableAsync(Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([]));
        _writer.WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>())
            .Returns(Result<Unit, Error>.Ok(Unit.Value));
        _probe.ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>())
            .Returns(new McpServerProbeResult(McpServerProbeStatus.Reachable, "ok"));
    }

    [Fact]
    public async Task AddAsync_NewServer_WritesAndReturnsSuccess()
    {
        var result = await Sut.AddAsync(StdioNoneRequest, default);

        Assert.True(result.Success);
        Assert.NotNull(result.Server);
        Assert.Equal("local", result.Server.Name);
        await _writer.Received(1).WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), default);
    }

    [Fact]
    public async Task AddAsync_AddsFirstServerToEmptyConfig()
    {
        var result = await Sut.AddAsync(StdioNoneRequest, default);

        Assert.True(result.Success);
        await _writer.Received(1).WriteAsync(
            Arg.Is<IReadOnlyList<McpServerDefinition>>(list => list.Count == 1 && list[0].Name == "local"),
            default);
    }

    [Fact]
    public async Task AddAsync_PreservesExistingServers()
    {
        var existing = new McpServerDefinition(
            "existing",
            new McpTransportConfig(McpTransportKind.Stdio, "cmd"),
            new NoneAuthConfig());
        _reader.ReadEditableAsync(default).Returns(
            Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([existing]));

        var result = await Sut.AddAsync(StdioNoneRequest, default);

        Assert.True(result.Success);
        await _writer.Received(1).WriteAsync(
            Arg.Is<IReadOnlyList<McpServerDefinition>>(list => list.Count == 2),
            default);
    }

    [Fact]
    public async Task AddAsync_DuplicateName_WithoutReplace_RejectsDuplicate()
    {
        var existing = new McpServerDefinition(
            "local",
            new McpTransportConfig(McpTransportKind.Stdio, "cmd"),
            new NoneAuthConfig());
        _reader.ReadEditableAsync(default).Returns(
            Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([existing]));

        var result = await Sut.AddAsync(StdioNoneRequest, default);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("DuplicateServer"));
        await _writer.DidNotReceive().WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), default);
    }

    [Fact]
    public async Task AddAsync_DuplicateName_WithReplace_ReplacesServer()
    {
        var existing = new McpServerDefinition(
            "local",
            new McpTransportConfig(McpTransportKind.Stdio, "old-cmd"),
            new NoneAuthConfig());
        _reader.ReadEditableAsync(default).Returns(
            Result<IReadOnlyList<McpServerDefinition>, Error>.Ok([existing]));

        var request = StdioNoneRequest with { Replace = true };
        var result = await Sut.AddAsync(request, default);

        Assert.True(result.Success);
        await _writer.Received(1).WriteAsync(
            Arg.Is<IReadOnlyList<McpServerDefinition>>(list =>
                list.Count == 1 && list[0].Transport.Endpoint == "hypa serve"),
            default);
    }

    [Fact]
    public async Task AddAsync_DryRun_ValidatesButDoesNotWrite()
    {
        var request = StdioNoneRequest with { DryRun = true };
        var result = await Sut.AddAsync(request, default);

        Assert.True(result.Success);
        Assert.NotNull(result.Server);
        await _writer.DidNotReceive().WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), default);
    }

    // Secret-ref validation

    [Theory]
    [InlineData("env:TOKEN")]
    [InlineData("file:/run/secrets/token")]
    [InlineData("ENV:TOKEN")]
    public async Task AddAsync_ValidSecretRef_Accepts(string tokenRef)
    {
        var request = new McpServerAddRequest(
            "s", "streamableHttp", "https://example.com", "bearer",
            new McpServerAddAuthOptions(TokenRef: tokenRef),
            null, null, null, false, false);

        var result = await Sut.AddAsync(request, default);
        Assert.True(result.Success);
    }

    [Theory]
    [InlineData("MY_RAW_TOKEN")]
    [InlineData("TOKEN")]
    [InlineData("secret123")]
    public async Task AddAsync_BareSecretRef_RejectsWithInvalidSecretRef(string bareRef)
    {
        var request = new McpServerAddRequest(
            "s", "streamableHttp", "https://example.com", "bearer",
            new McpServerAddAuthOptions(TokenRef: bareRef),
            null, null, null, false, false);

        var result = await Sut.AddAsync(request, default);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("InvalidSecretRef"));
    }

    // Auth-mode required field validation

    [Fact]
    public async Task AddAsync_Bearer_MissingTokenRef_RejectsMissingOption()
    {
        var request = new McpServerAddRequest(
            "s", "streamableHttp", "https://example.com", "bearer",
            new McpServerAddAuthOptions(),
            null, null, null, false, false);

        var result = await Sut.AddAsync(request, default);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("MissingOption") && e.Contains("--token-ref"));
    }

    [Fact]
    public async Task AddAsync_ApiKey_MissingHeaderName_RejectsMissingOption()
    {
        var request = new McpServerAddRequest(
            "s", "streamableHttp", "https://example.com", "apiKey",
            new McpServerAddAuthOptions(ValueRef: "env:KEY"),
            null, null, null, false, false);

        var result = await Sut.AddAsync(request, default);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("MissingOption") && e.Contains("--header-name"));
    }

    [Fact]
    public async Task AddAsync_ApiKey_MissingValueRef_RejectsMissingOption()
    {
        var request = new McpServerAddRequest(
            "s", "streamableHttp", "https://example.com", "apiKey",
            new McpServerAddAuthOptions(HeaderName: "X-Api-Key"),
            null, null, null, false, false);

        var result = await Sut.AddAsync(request, default);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("MissingOption") && e.Contains("--value-ref"));
    }

    [Fact]
    public async Task AddAsync_Basic_MissingBothRefs_ReportsAllErrors()
    {
        var request = new McpServerAddRequest(
            "s", "streamableHttp", "https://example.com", "basic",
            new McpServerAddAuthOptions(),
            null, null, null, false, false);

        var result = await Sut.AddAsync(request, default);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("--username-ref"));
        Assert.Contains(result.Errors, e => e.Contains("--password-ref"));
    }

    [Fact]
    public async Task AddAsync_OAuth2ClientCredentials_MissingTokenUrl_Rejects()
    {
        var request = new McpServerAddRequest(
            "s", "streamableHttp", "https://example.com", "oauth2ClientCredentials",
            new McpServerAddAuthOptions(ClientIdRef: "env:CID", ClientSecretRef: "env:CS"),
            null, null, null, false, false);

        var result = await Sut.AddAsync(request, default);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("--token-url"));
    }

    [Fact]
    public async Task AddAsync_OAuth2DeviceCode_MissingClientId_Rejects()
    {
        var request = new McpServerAddRequest(
            "s", "streamableHttp", "https://example.com", "oauth2DeviceCode",
            new McpServerAddAuthOptions(
                AuthUrl: "https://auth/device",
                TokenUrl: "https://auth/token"),
            null, null, null, false, false);

        var result = await Sut.AddAsync(request, default);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("--client-id"));
    }

    // TLS combined with service

    [Fact]
    public async Task AddAsync_TlsCertWithoutKey_RejectsInvalidConfig()
    {
        var request = new McpServerAddRequest(
            "s", "streamableHttp", "https://example.com", "none",
            new McpServerAddAuthOptions(),
            new McpServerAddTlsOptions(null, "/cert.pem", null),
            null, null, false, false);

        var result = await Sut.AddAsync(request, default);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("InvalidConfig"));
    }

    [Fact]
    public async Task AddAsync_TlsOnStdio_RejectsInvalidConfig()
    {
        var request = new McpServerAddRequest(
            "s", "stdio", "hypa serve", "none",
            new McpServerAddAuthOptions(),
            new McpServerAddTlsOptions("/ca.pem", null, null),
            null, null, false, false);

        var result = await Sut.AddAsync(request, default);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("InvalidConfig"));
    }

    // Timeout validation via McpConfigValidationService pass-through

    [Fact]
    public async Task AddAsync_ValidRequest_MapsTransportCorrectly()
    {
        var result = await Sut.AddAsync(StdioNoneRequest, default);

        Assert.True(result.Success);
        Assert.Equal(McpTransportKind.Stdio, result.Server!.Transport.Kind);
        Assert.Equal("hypa serve", result.Server.Transport.Endpoint);
    }

    [Fact]
    public async Task AddAsync_StreamableHttpTransport_MapsToHttpKind()
    {
        var request = new McpServerAddRequest(
            "s", "streamableHttp", "https://example.com", "none",
            new McpServerAddAuthOptions(),
            null, null, null, false, false);

        var result = await Sut.AddAsync(request, default);

        Assert.True(result.Success);
        Assert.Equal(McpTransportKind.Http, result.Server!.Transport.Kind);
    }

    // mTLS required refs

    [Fact]
    public async Task AddAsync_Mtls_MissingBothRefs_RejectsMissingOption()
    {
        var request = new McpServerAddRequest(
            "s", "streamableHttp", "https://example.com", "mtls",
            new McpServerAddAuthOptions(),
            null, null, null, false, false);

        var result = await Sut.AddAsync(request, default);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("MissingOption") && e.Contains("--client-cert-ref"));
        Assert.Contains(result.Errors, e => e.Contains("MissingOption") && e.Contains("--client-key-ref"));
    }

    [Fact]
    public async Task AddAsync_Mtls_WithBothRefs_Succeeds()
    {
        var request = new McpServerAddRequest(
            "s", "streamableHttp", "https://example.com", "mtls",
            new McpServerAddAuthOptions(ClientCertRef: "env:CERT", ClientKeyRef: "env:KEY"),
            null, null, null, false, false);

        var result = await Sut.AddAsync(request, default);
        Assert.True(result.Success);
    }

    // Timeout validation

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task AddAsync_NonPositiveConnectTimeout_RejectsInvalidOption(int timeout)
    {
        var request = StdioNoneRequest with { ConnectTimeoutSeconds = timeout };

        var result = await Sut.AddAsync(request, default);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("InvalidOption") && e.Contains("connect-timeout-seconds"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task AddAsync_NonPositiveRequestTimeout_RejectsInvalidOption(int timeout)
    {
        var request = StdioNoneRequest with { RequestTimeoutSeconds = timeout };

        var result = await Sut.AddAsync(request, default);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("InvalidOption") && e.Contains("request-timeout-seconds"));
    }

    [Fact]
    public async Task AddAsync_PositiveTimeouts_Succeeds()
    {
        var request = StdioNoneRequest with { ConnectTimeoutSeconds = 10, RequestTimeoutSeconds = 30 };

        var result = await Sut.AddAsync(request, default);
        Assert.True(result.Success);
        Assert.Equal(TimeSpan.FromSeconds(10), result.Server!.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), result.Server.RequestTimeout);
    }

    [Fact]
    public async Task AddAsync_WriterFailure_ReturnsError()
    {
        _writer.WriteAsync(Arg.Any<IReadOnlyList<McpServerDefinition>>(), Arg.Any<CancellationToken>())
            .Returns(Result<Unit, Error>.Fail(new Error("WriteFailed", "disk full")));

        var result = await Sut.AddAsync(StdioNoneRequest, default);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("disk full"));
    }

    [Fact]
    public async Task AddAsync_StdioRequest_DoesNotInvokeProbe()
    {
        var result = await Sut.AddAsync(StdioNoneRequest, default);

        Assert.True(result.Success);
        await _probe.DidNotReceive().ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddAsync_PreValidationFailure_DoesNotInvokeProbe()
    {
        var request = new McpServerAddRequest(
            "s", "streamableHttp", "https://example.com", "bearer",
            new McpServerAddAuthOptions(),
            null, null, null, false, false);

        var result = await Sut.AddAsync(request, default);

        Assert.False(result.Success);
        await _probe.DidNotReceive().ProbeAsync(Arg.Any<McpServerDefinition>(), Arg.Any<CancellationToken>());
    }
}
