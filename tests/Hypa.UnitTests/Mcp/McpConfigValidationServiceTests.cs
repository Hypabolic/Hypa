using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Mcp;
using Xunit;

namespace Hypa.UnitTests.Mcp;

[Trait("Category", "McpConfig")]
public sealed class McpConfigValidationServiceTests
{
    private static readonly McpConfigValidationService _sut = new();

    private static McpServerDefinition MakeServer(
        string name,
        McpTransportConfig? transport = null,
        McpAuthConfig? auth = null) =>
        new(
            name,
            transport ?? new McpTransportConfig(McpTransportKind.Stdio, "my-mcp-server"),
            auth ?? new NoneAuthConfig());

    // Valid configs — one per auth mode

    [Fact]
    public void Validate_NoneAuth_Stdio_IsValid()
    {
        var result = _sut.Validate([MakeServer("s")]);
        Assert.True(result.IsOk);
    }

    [Fact]
    public void Validate_BearerAuth_IsValid()
    {
        var server = MakeServer("s",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new BearerAuthConfig("TOKEN_REF"));

        Assert.True(_sut.Validate([server]).IsOk);
    }

    [Fact]
    public void Validate_ApiKeyAuth_IsValid()
    {
        var server = MakeServer("s",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new ApiKeyAuthConfig("X-Key", "KEY_REF"));

        Assert.True(_sut.Validate([server]).IsOk);
    }

    [Fact]
    public void Validate_BasicAuth_IsValid()
    {
        var server = MakeServer("s",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new BasicAuthConfig("USER_REF", "PASS_REF"));

        Assert.True(_sut.Validate([server]).IsOk);
    }

    [Fact]
    public void Validate_OAuth2ClientCredentials_IsValid()
    {
        var server = MakeServer("s",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new OAuth2ClientCredentialsConfig("https://auth/token", "CID_REF", "CSECRET_REF"));

        Assert.True(_sut.Validate([server]).IsOk);
    }

    [Fact]
    public void Validate_OAuth2DeviceCode_IsValid()
    {
        var server = MakeServer("s",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new OAuth2DeviceCodeConfig("https://auth/device", "https://auth/token", "my-client"));

        Assert.True(_sut.Validate([server]).IsOk);
    }

    [Fact]
    public void Validate_MtlsAuth_BothRefs_IsValid()
    {
        var server = MakeServer("s",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new MtlsConfig("CERT_REF", "KEY_REF"));

        Assert.True(_sut.Validate([server]).IsOk);
    }

    [Fact]
    public void Validate_MtlsAuth_NeitherRef_ProducesErrors()
    {
        var server = MakeServer("s",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new MtlsConfig(null, null));

        var result = _sut.Validate([server]);
        Assert.False(result.IsOk);
        Assert.Contains(result.Error, e => e.Field == "Auth.ClientCertRef");
        Assert.Contains(result.Error, e => e.Field == "Auth.ClientKeyRef");
    }

    // Invalid configs — missing required fields per auth mode

    [Fact]
    public void Validate_BearerAuth_MissingTokenRef_ProducesError()
    {
        var server = MakeServer("s",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new BearerAuthConfig(string.Empty));

        var result = _sut.Validate([server]);
        Assert.False(result.IsOk);
        Assert.Contains(result.Error, e => e.Field == "Auth.TokenRef" && e.ServerName == "s");
    }

    [Fact]
    public void Validate_ApiKeyAuth_MissingHeaderName_ProducesError()
    {
        var server = MakeServer("s",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new ApiKeyAuthConfig(string.Empty, "VALUE_REF"));

        var result = _sut.Validate([server]);
        Assert.False(result.IsOk);
        Assert.Contains(result.Error, e => e.Field == "Auth.HeaderName");
    }

    [Fact]
    public void Validate_ApiKeyAuth_MissingValueRef_ProducesError()
    {
        var server = MakeServer("s",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new ApiKeyAuthConfig("X-Key", string.Empty));

        var result = _sut.Validate([server]);
        Assert.False(result.IsOk);
        Assert.Contains(result.Error, e => e.Field == "Auth.ValueRef");
    }

    [Fact]
    public void Validate_BasicAuth_MissingUsernameRef_ProducesError()
    {
        var server = MakeServer("s",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new BasicAuthConfig(string.Empty, "PASS_REF"));

        var result = _sut.Validate([server]);
        Assert.False(result.IsOk);
        Assert.Contains(result.Error, e => e.Field == "Auth.UsernameRef");
    }

    [Fact]
    public void Validate_BasicAuth_MissingPasswordRef_ProducesError()
    {
        var server = MakeServer("s",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new BasicAuthConfig("USER_REF", string.Empty));

        var result = _sut.Validate([server]);
        Assert.False(result.IsOk);
        Assert.Contains(result.Error, e => e.Field == "Auth.PasswordRef");
    }

    [Fact]
    public void Validate_OAuth2ClientCredentials_MissingTokenUrl_ProducesError()
    {
        var server = MakeServer("s",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new OAuth2ClientCredentialsConfig(string.Empty, "CID", "CSECRET"));

        var result = _sut.Validate([server]);
        Assert.False(result.IsOk);
        Assert.Contains(result.Error, e => e.Field == "Auth.TokenUrl");
    }

    [Fact]
    public void Validate_OAuth2ClientCredentials_MissingClientIdRef_ProducesError()
    {
        var server = MakeServer("s",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new OAuth2ClientCredentialsConfig("https://auth/token", string.Empty, "CSECRET"));

        var result = _sut.Validate([server]);
        Assert.False(result.IsOk);
        Assert.Contains(result.Error, e => e.Field == "Auth.ClientIdRef");
    }

    [Fact]
    public void Validate_OAuth2ClientCredentials_MissingClientSecretRef_ProducesError()
    {
        var server = MakeServer("s",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new OAuth2ClientCredentialsConfig("https://auth/token", "CID", string.Empty));

        var result = _sut.Validate([server]);
        Assert.False(result.IsOk);
        Assert.Contains(result.Error, e => e.Field == "Auth.ClientSecretRef");
    }

    [Fact]
    public void Validate_OAuth2DeviceCode_MissingAuthUrl_ProducesError()
    {
        var server = MakeServer("s",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new OAuth2DeviceCodeConfig(string.Empty, "https://auth/token", "client-id"));

        var result = _sut.Validate([server]);
        Assert.False(result.IsOk);
        Assert.Contains(result.Error, e => e.Field == "Auth.AuthUrl");
    }

    [Fact]
    public void Validate_OAuth2DeviceCode_MissingClientId_ProducesError()
    {
        var server = MakeServer("s",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new OAuth2DeviceCodeConfig("https://auth/device", "https://auth/token", string.Empty));

        var result = _sut.Validate([server]);
        Assert.False(result.IsOk);
        Assert.Contains(result.Error, e => e.Field == "Auth.ClientId");
    }

    [Fact]
    public void Validate_MtlsAuth_OnlyCertRef_ProducesKeyRefError()
    {
        var server = MakeServer("s",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new MtlsConfig("CERT_REF", null));

        var result = _sut.Validate([server]);
        Assert.False(result.IsOk);
        Assert.Contains(result.Error, e => e.Field == "Auth.ClientKeyRef");
        Assert.DoesNotContain(result.Error, e => e.Field == "Auth.ClientCertRef");
    }

    [Fact]
    public void Validate_MtlsAuth_OnlyKeyRef_ProducesCertRefError()
    {
        var server = MakeServer("s",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new MtlsConfig(null, "KEY_REF"));

        var result = _sut.Validate([server]);
        Assert.False(result.IsOk);
        Assert.Contains(result.Error, e => e.Field == "Auth.ClientCertRef");
        Assert.DoesNotContain(result.Error, e => e.Field == "Auth.ClientKeyRef");
    }

    // Transport rules

    [Fact]
    public void Validate_Stdio_WithoutEndpoint_ProducesError()
    {
        var server = MakeServer("s", new McpTransportConfig(McpTransportKind.Stdio, null));

        var result = _sut.Validate([server]);
        Assert.False(result.IsOk);
        Assert.Contains(result.Error, e => e.Field == "Transport.Endpoint");
    }

    [Fact]
    public void Validate_Stdio_WithEndpoint_IsValid()
    {
        var server = MakeServer("s", new McpTransportConfig(McpTransportKind.Stdio, "my-mcp-server --port 8080"));

        Assert.True(_sut.Validate([server]).IsOk);
    }

    [Fact]
    public void Validate_Http_WithoutEndpoint_ProducesError()
    {
        var server = MakeServer("s", new McpTransportConfig(McpTransportKind.Http, null));

        var result = _sut.Validate([server]);
        Assert.False(result.IsOk);
        Assert.Contains(result.Error, e => e.Field == "Transport.Endpoint");
    }

    [Fact]
    public void Validate_Sse_WithoutEndpoint_ProducesError()
    {
        var server = MakeServer("s", new McpTransportConfig(McpTransportKind.Sse, null));

        var result = _sut.Validate([server]);
        Assert.False(result.IsOk);
        Assert.Contains(result.Error, e => e.Field == "Transport.Endpoint");
    }

    [Fact]
    public void Validate_Http_WithInvalidUri_ProducesError()
    {
        var server = MakeServer("s", new McpTransportConfig(McpTransportKind.Http, "not-a-uri"));

        var result = _sut.Validate([server]);
        Assert.False(result.IsOk);
        Assert.Contains(result.Error, e => e.Field == "Transport.Endpoint");
    }

    // Unknown transport

    [Fact]
    public void Validate_UnknownTransport_ProducesError()
    {
        var server = MakeServer("s", new McpTransportConfig(McpTransportKind.Unknown, null));

        var result = _sut.Validate([server]);
        Assert.False(result.IsOk);
        Assert.Contains(result.Error, e => e.Field == "Transport.Kind" && e.ServerName == "s");
    }

    // Unknown auth type

    [Fact]
    public void Validate_UnknownAuthType_ProducesError()
    {
        var server = MakeServer("s",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new UnknownAuthConfig("bearr"));

        var result = _sut.Validate([server]);
        Assert.False(result.IsOk);
        var error = Assert.Single(result.Error, e => e.Field == "Auth.Type");
        Assert.Contains("bearr", error.Message);
    }

    [Fact]
    public void Validate_AuthBlockPresentWithNoType_ProducesRequiredError()
    {
        var server = MakeServer("s",
            new McpTransportConfig(McpTransportKind.Stdio, null),
            new UnknownAuthConfig(string.Empty));

        var result = _sut.Validate([server]);
        Assert.False(result.IsOk);
        var error = Assert.Single(result.Error, e => e.Field == "Auth.Type");
        Assert.Contains("required", error.Message);
    }

    // Blank server names

    [Fact]
    public void Validate_EmptyServerName_ProducesError()
    {
        var server = MakeServer(string.Empty);

        var result = _sut.Validate([server]);
        Assert.False(result.IsOk);
        Assert.Contains(result.Error, e => e.Field == "Name");
    }

    [Fact]
    public void Validate_WhitespaceServerName_ProducesError()
    {
        var server = MakeServer("   ");

        var result = _sut.Validate([server]);
        Assert.False(result.IsOk);
        Assert.Contains(result.Error, e => e.Field == "Name");
    }

    // Duplicate names

    [Fact]
    public void Validate_DuplicateServerNames_ProducesError()
    {
        var servers = new[]
        {
            MakeServer("duplicate"),
            MakeServer("duplicate"),
        };

        var result = _sut.Validate(servers);
        Assert.False(result.IsOk);
        Assert.Contains(result.Error, e => e.ServerName == "duplicate" && e.Field == "Name");
    }

    [Fact]
    public void Validate_DuplicateServerNames_CaseInsensitive_ProducesError()
    {
        var servers = new[]
        {
            MakeServer("MyServer"),
            MakeServer("myserver"),
        };

        var result = _sut.Validate(servers);
        Assert.False(result.IsOk);
        Assert.Contains(result.Error, e => e.Field == "Name");
    }

    // TLS rules

    [Fact]
    public void Validate_Tls_OnRemoteTransport_BothCertAndKey_IsValid()
    {
        var server = new McpServerDefinition(
            "s",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new NoneAuthConfig(),
            new McpTlsConfig(null, "/cert.pem", "/key.pem"));

        Assert.True(_sut.Validate([server]).IsOk);
    }

    [Fact]
    public void Validate_Tls_OnRemoteTransport_OnlyCaCert_IsValid()
    {
        var server = new McpServerDefinition(
            "s",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new NoneAuthConfig(),
            new McpTlsConfig("/ca.pem", null, null));

        Assert.True(_sut.Validate([server]).IsOk);
    }

    [Fact]
    public void Validate_Tls_OnStdioTransport_ProducesError()
    {
        var server = new McpServerDefinition(
            "s",
            new McpTransportConfig(McpTransportKind.Stdio, "cmd"),
            new NoneAuthConfig(),
            new McpTlsConfig("/ca.pem", null, null));

        var result = _sut.Validate([server]);
        Assert.False(result.IsOk);
        Assert.Contains(result.Error, e => e.Field == "Tls");
    }

    [Fact]
    public void Validate_Tls_CertWithoutKey_ProducesError()
    {
        var server = new McpServerDefinition(
            "s",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new NoneAuthConfig(),
            new McpTlsConfig(null, "/cert.pem", null));

        var result = _sut.Validate([server]);
        Assert.False(result.IsOk);
        Assert.Contains(result.Error, e => e.Field == "Tls.ClientCert");
    }

    [Fact]
    public void Validate_Tls_KeyWithoutCert_ProducesError()
    {
        var server = new McpServerDefinition(
            "s",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new NoneAuthConfig(),
            new McpTlsConfig(null, null, "/key.pem"));

        var result = _sut.Validate([server]);
        Assert.False(result.IsOk);
        Assert.Contains(result.Error, e => e.Field == "Tls.ClientCert");
    }

    [Fact]
    public void Validate_NullTls_IsValid()
    {
        var server = new McpServerDefinition(
            "s",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new NoneAuthConfig(),
            Tls: null);

        Assert.True(_sut.Validate([server]).IsOk);
    }
}
