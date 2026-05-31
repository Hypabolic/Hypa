using System.Text.Json;
using Hypa.Infrastructure.Mcp.Config;
using Hypa.Runtime.Domain.Mcp;
using Xunit;

namespace Hypa.UnitTests.Infrastructure;

[Trait("Category", "McpServerConfig")]
public sealed class McpServerConfigWriterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"hypa-writer-test-{Guid.NewGuid():N}");

    public McpServerConfigWriterTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private McpServerConfigWriter Sut => new(_tempDir);

    private static McpServerDefinition StdioNone(string name = "local") =>
        new(name, new McpTransportConfig(McpTransportKind.Stdio, "hypa serve"), new NoneAuthConfig());

    private static McpServerDefinition HttpBearer(string name = "remote") =>
        new(name,
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new BearerAuthConfig("env:TOKEN"));

    // Read tests

    [Fact]
    public async Task ReadEditableAsync_MissingFile_ReturnsEmptyList()
    {
        var result = await Sut.ReadEditableAsync(default);

        Assert.True(result.IsOk);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task ReadEditableAsync_ExistingFile_ReturnsServers()
    {
        await Sut.WriteAsync([StdioNone()], default);

        var result = await Sut.ReadEditableAsync(default);

        Assert.True(result.IsOk);
        var server = Assert.Single(result.Value);
        Assert.Equal("local", server.Name);
    }

    // Write tests

    [Fact]
    public async Task WriteAsync_MissingDirectory_CreatesDirectoryAndFile()
    {
        var subDir = Path.Combine(_tempDir, "nested", "config");
        var writer = new McpServerConfigWriter(subDir);

        var result = await writer.WriteAsync([StdioNone()], default);

        Assert.True(result.IsOk);
        Assert.True(File.Exists(Path.Combine(subDir, "mcp-servers.json")));
    }

    [Fact]
    public async Task WriteAsync_CreatesValidJsonFile()
    {
        await Sut.WriteAsync([StdioNone()], default);

        var json = await File.ReadAllTextAsync(Path.Combine(_tempDir, "mcp-servers.json"));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("servers", out _));
    }

    [Fact]
    public async Task WriteAsync_PreservesExistingEntries()
    {
        await Sut.WriteAsync([StdioNone("a")], default);
        var readResult = await Sut.ReadEditableAsync(default);
        var updated = readResult.Value.Append(StdioNone("b")).ToList();

        await Sut.WriteAsync(updated, default);

        var result = await Sut.ReadEditableAsync(default);
        Assert.Equal(2, result.Value.Count);
        Assert.Contains(result.Value, s => s.Name == "a");
        Assert.Contains(result.Value, s => s.Name == "b");
    }

    [Fact]
    public async Task WriteAsync_LeavesNoTempFileOnSuccess()
    {
        await Sut.WriteAsync([StdioNone()], default);

        var tmpFiles = Directory.GetFiles(_tempDir, "*.tmp");
        Assert.Empty(tmpFiles);
    }

    // Round-trip tests per auth mode

    [Fact]
    public async Task RoundTrip_NoneAuth_Stdio()
    {
        await Sut.WriteAsync([StdioNone()], default);
        var result = await Sut.ReadEditableAsync(default);

        var server = Assert.Single(result.Value);
        Assert.Equal(McpTransportKind.Stdio, server.Transport.Kind);
        Assert.Equal("hypa serve", server.Transport.Endpoint);
        Assert.IsType<NoneAuthConfig>(server.Auth);
    }

    [Fact]
    public async Task RoundTrip_BearerAuth()
    {
        await Sut.WriteAsync([HttpBearer()], default);
        var result = await Sut.ReadEditableAsync(default);

        var server = Assert.Single(result.Value);
        Assert.Equal(McpTransportKind.Http, server.Transport.Kind);
        var bearer = Assert.IsType<BearerAuthConfig>(server.Auth);
        Assert.Equal("env:TOKEN", bearer.TokenRef);
    }

    [Fact]
    public async Task RoundTrip_ApiKeyAuth()
    {
        var def = new McpServerDefinition(
            "api",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new ApiKeyAuthConfig("X-Api-Key", "env:KEY", false));

        await Sut.WriteAsync([def], default);
        var result = await Sut.ReadEditableAsync(default);

        var server = Assert.Single(result.Value);
        var ak = Assert.IsType<ApiKeyAuthConfig>(server.Auth);
        Assert.Equal("X-Api-Key", ak.HeaderName);
        Assert.Equal("env:KEY", ak.ValueRef);
    }

    [Fact]
    public async Task RoundTrip_BasicAuth()
    {
        var def = new McpServerDefinition(
            "basic",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new BasicAuthConfig("env:USER", "env:PASS"));

        await Sut.WriteAsync([def], default);
        var result = await Sut.ReadEditableAsync(default);

        var server = Assert.Single(result.Value);
        var ba = Assert.IsType<BasicAuthConfig>(server.Auth);
        Assert.Equal("env:USER", ba.UsernameRef);
        Assert.Equal("env:PASS", ba.PasswordRef);
    }

    [Fact]
    public async Task RoundTrip_OAuth2ClientCredentials()
    {
        var def = new McpServerDefinition(
            "cc",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new OAuth2ClientCredentialsConfig(
                "https://auth/token",
                "env:CLIENT_ID",
                "env:CLIENT_SECRET",
                ["repo.read", "user.read"]));

        await Sut.WriteAsync([def], default);
        var result = await Sut.ReadEditableAsync(default);

        var server = Assert.Single(result.Value);
        var cc = Assert.IsType<OAuth2ClientCredentialsConfig>(server.Auth);
        Assert.Equal("https://auth/token", cc.TokenUrl);
        Assert.Equal("env:CLIENT_ID", cc.ClientIdRef);
        Assert.Equal(2, cc.Scopes?.Length);
    }

    [Fact]
    public async Task RoundTrip_OAuth2DeviceCode()
    {
        var def = new McpServerDefinition(
            "dc",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new OAuth2DeviceCodeConfig(
                "https://auth/device",
                "https://auth/token",
                "my-client-id"));

        await Sut.WriteAsync([def], default);
        var result = await Sut.ReadEditableAsync(default);

        var server = Assert.Single(result.Value);
        var dc = Assert.IsType<OAuth2DeviceCodeConfig>(server.Auth);
        Assert.Equal("https://auth/device", dc.AuthUrl);
        Assert.Equal("my-client-id", dc.ClientId);
    }

    [Fact]
    public async Task RoundTrip_MtlsAuth()
    {
        var def = new McpServerDefinition(
            "mtls",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new MtlsConfig("env:CERT", "env:KEY"));

        await Sut.WriteAsync([def], default);
        var result = await Sut.ReadEditableAsync(default);

        var server = Assert.Single(result.Value);
        var m = Assert.IsType<MtlsConfig>(server.Auth);
        Assert.Equal("env:CERT", m.ClientCertRef);
        Assert.Equal("env:KEY", m.ClientKeyRef);
    }

    [Fact]
    public async Task RoundTrip_TransportCanonicalCasing_StreamableHttp()
    {
        var def = new McpServerDefinition(
            "http",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new NoneAuthConfig());

        await Sut.WriteAsync([def], default);
        var json = await File.ReadAllTextAsync(Path.Combine(_tempDir, "mcp-servers.json"));

        Assert.Contains("streamableHttp", json);
    }

    [Fact]
    public async Task RoundTrip_HttpAutoDetect_CanonicalCasing()
    {
        var def = new McpServerDefinition(
            "auto",
            new McpTransportConfig(McpTransportKind.HttpAutoDetect, "https://example.com"),
            new NoneAuthConfig());

        await Sut.WriteAsync([def], default);
        var json = await File.ReadAllTextAsync(Path.Combine(_tempDir, "mcp-servers.json"));

        Assert.Contains("httpAutoDetect", json);
    }

    [Fact]
    public async Task RoundTrip_Tls_PreservesFields()
    {
        var def = new McpServerDefinition(
            "tls",
            new McpTransportConfig(McpTransportKind.Http, "https://example.com"),
            new NoneAuthConfig(),
            Tls: new McpTlsConfig("/ca.pem", "/cert.pem", "/key.pem"));

        await Sut.WriteAsync([def], default);
        var result = await Sut.ReadEditableAsync(default);

        var server = Assert.Single(result.Value);
        Assert.NotNull(server.Tls);
        Assert.Equal("/ca.pem", server.Tls.CaCertPath);
        Assert.Equal("/cert.pem", server.Tls.ClientCertPath);
        Assert.Equal("/key.pem", server.Tls.ClientKeyPath);
    }

    [Fact]
    public async Task RoundTrip_Timeouts_Preserved()
    {
        var def = new McpServerDefinition(
            "t",
            new McpTransportConfig(McpTransportKind.Stdio, "cmd"),
            new NoneAuthConfig(),
            ConnectTimeout: TimeSpan.FromSeconds(10),
            RequestTimeout: TimeSpan.FromSeconds(30));

        await Sut.WriteAsync([def], default);
        var result = await Sut.ReadEditableAsync(default);

        var server = Assert.Single(result.Value);
        Assert.Equal(TimeSpan.FromSeconds(10), server.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), server.RequestTimeout);
    }

    [Fact]
    public async Task WriteAsync_EmptyList_WritesValidJson()
    {
        await Sut.WriteAsync([], default);

        var result = await Sut.ReadEditableAsync(default);
        Assert.True(result.IsOk);
        Assert.Empty(result.Value);
    }
}
