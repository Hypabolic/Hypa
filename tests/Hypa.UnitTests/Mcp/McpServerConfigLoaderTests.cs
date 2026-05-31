using System.Text.Json;
using Hypa.Infrastructure.Mcp.Config;
using Hypa.Runtime.Domain.Mcp;
using Xunit;

namespace Hypa.UnitTests.Mcp;

[Trait("Category", "McpConfig")]
public sealed class McpServerConfigLoaderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"hypa-mcp-test-{Guid.NewGuid():N}");

    public McpServerConfigLoaderTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsEmptyList()
    {
        var loader = new McpServerConfigLoader(_tempDir);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task LoadAsync_EmptyServers_ReturnsEmptyList()
    {
        WriteJson(new { servers = Array.Empty<object>() });

        var loader = new McpServerConfigLoader(_tempDir);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task LoadAsync_StdioServer_NoneAuth_ParsesCorrectly()
    {
        WriteJson(new
        {
            servers = new[]
            {
                new { name = "local", transport = "stdio" }
            }
        });

        var loader = new McpServerConfigLoader(_tempDir);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.True(result.IsOk);
        var server = Assert.Single(result.Value);
        Assert.Equal("local", server.Name);
        Assert.Equal(McpTransportKind.Stdio, server.Transport.Kind);
        Assert.Null(server.Transport.Endpoint);
        Assert.IsType<NoneAuthConfig>(server.Auth);
    }

    [Fact]
    public async Task LoadAsync_BearerAuth_ParsesCorrectly()
    {
        WriteJson(new
        {
            servers = new[]
            {
                new
                {
                    name = "bearer-server",
                    transport = "http",
                    endpoint = "https://example.com/mcp",
                    auth = new { type = "bearer", tokenRef = "MY_TOKEN" }
                }
            }
        });

        var loader = new McpServerConfigLoader(_tempDir);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.True(result.IsOk);
        var server = Assert.Single(result.Value);
        Assert.Equal(McpTransportKind.HttpAutoDetect, server.Transport.Kind);
        var auth = Assert.IsType<BearerAuthConfig>(server.Auth);
        Assert.Equal("MY_TOKEN", auth.TokenRef);
    }

    [Fact]
    public async Task LoadAsync_ApiKeyAuth_ParsesCorrectly()
    {
        WriteJson(new
        {
            servers = new[]
            {
                new
                {
                    name = "apikey-server",
                    transport = "http",
                    endpoint = "https://example.com/mcp",
                    auth = new { type = "apikey", headerName = "X-Api-Key", valueRef = "API_KEY_REF", inQueryString = false }
                }
            }
        });

        var loader = new McpServerConfigLoader(_tempDir);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.True(result.IsOk);
        var server = Assert.Single(result.Value);
        var auth = Assert.IsType<ApiKeyAuthConfig>(server.Auth);
        Assert.Equal("X-Api-Key", auth.HeaderName);
        Assert.Equal("API_KEY_REF", auth.ValueRef);
        Assert.False(auth.InQueryString);
    }

    [Fact]
    public async Task LoadAsync_BasicAuth_ParsesCorrectly()
    {
        WriteJson(new
        {
            servers = new[]
            {
                new
                {
                    name = "basic-server",
                    transport = "http",
                    endpoint = "https://example.com/mcp",
                    auth = new { type = "basic", usernameRef = "USER_REF", passwordRef = "PASS_REF" }
                }
            }
        });

        var loader = new McpServerConfigLoader(_tempDir);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.True(result.IsOk);
        var auth = Assert.IsType<BasicAuthConfig>(Assert.Single(result.Value).Auth);
        Assert.Equal("USER_REF", auth.UsernameRef);
        Assert.Equal("PASS_REF", auth.PasswordRef);
    }

    [Fact]
    public async Task LoadAsync_OAuth2ClientCredentials_ParsesCorrectly()
    {
        WriteJson(new
        {
            servers = new[]
            {
                new
                {
                    name = "oauth2cc-server",
                    transport = "http",
                    endpoint = "https://example.com/mcp",
                    auth = new
                    {
                        type = "oauth2clientcredentials",
                        tokenUrl = "https://auth.example.com/token",
                        clientIdRef = "CLIENT_ID",
                        clientSecretRef = "CLIENT_SECRET",
                        scopes = new[] { "read", "write" }
                    }
                }
            }
        });

        var loader = new McpServerConfigLoader(_tempDir);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.True(result.IsOk);
        var auth = Assert.IsType<OAuth2ClientCredentialsConfig>(Assert.Single(result.Value).Auth);
        Assert.Equal("https://auth.example.com/token", auth.TokenUrl);
        Assert.Equal("CLIENT_ID", auth.ClientIdRef);
        Assert.Equal("CLIENT_SECRET", auth.ClientSecretRef);
        Assert.Equal(["read", "write"], auth.Scopes!);
    }

    [Fact]
    public async Task LoadAsync_OAuth2DeviceCode_ParsesCorrectly()
    {
        WriteJson(new
        {
            servers = new[]
            {
                new
                {
                    name = "oauth2dc-server",
                    transport = "sse",
                    endpoint = "https://example.com/sse",
                    auth = new
                    {
                        type = "oauth2devicecode",
                        authUrl = "https://auth.example.com/device",
                        tokenUrl = "https://auth.example.com/token",
                        clientId = "my-client"
                    }
                }
            }
        });

        var loader = new McpServerConfigLoader(_tempDir);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.True(result.IsOk);
        var auth = Assert.IsType<OAuth2DeviceCodeConfig>(Assert.Single(result.Value).Auth);
        Assert.Equal("https://auth.example.com/device", auth.AuthUrl);
        Assert.Equal("https://auth.example.com/token", auth.TokenUrl);
        Assert.Equal("my-client", auth.ClientId);
    }

    [Fact]
    public async Task LoadAsync_MtlsAuth_ParsesCorrectly()
    {
        WriteJson(new
        {
            servers = new[]
            {
                new
                {
                    name = "mtls-server",
                    transport = "http",
                    endpoint = "https://example.com/mcp",
                    auth = new { type = "mtls", clientCertRef = "CERT_REF", clientKeyRef = "KEY_REF" }
                }
            }
        });

        var loader = new McpServerConfigLoader(_tempDir);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.True(result.IsOk);
        var auth = Assert.IsType<MtlsConfig>(Assert.Single(result.Value).Auth);
        Assert.Equal("CERT_REF", auth.ClientCertRef);
        Assert.Equal("KEY_REF", auth.ClientKeyRef);
    }

    [Fact]
    public async Task LoadAsync_Timeouts_ParsedCorrectly()
    {
        WriteJson(new
        {
            servers = new[]
            {
                new
                {
                    name = "timed-server",
                    transport = "http",
                    endpoint = "https://example.com/mcp",
                    connectTimeoutSeconds = 5,
                    requestTimeoutSeconds = 30
                }
            }
        });

        var loader = new McpServerConfigLoader(_tempDir);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.True(result.IsOk);
        var server = Assert.Single(result.Value);
        Assert.Equal(TimeSpan.FromSeconds(5), server.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), server.RequestTimeout);
    }

    [Fact]
    public async Task LoadAsync_TlsConfig_ParsedCorrectly()
    {
        WriteJson(new
        {
            servers = new[]
            {
                new
                {
                    name = "tls-server",
                    transport = "http",
                    endpoint = "https://example.com/mcp",
                    tls = new { caCertPath = "/certs/ca.pem", clientCertPath = "/certs/client.pem", clientKeyPath = "/certs/client.key" }
                }
            }
        });

        var loader = new McpServerConfigLoader(_tempDir);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.True(result.IsOk);
        var server = Assert.Single(result.Value);
        Assert.NotNull(server.Tls);
        Assert.Equal("/certs/ca.pem", server.Tls.CaCertPath);
        Assert.Equal("/certs/client.pem", server.Tls.ClientCertPath);
        Assert.Equal("/certs/client.key", server.Tls.ClientKeyPath);
    }

    [Fact]
    public async Task LoadAsync_UnknownTransport_MapsToUnknownKind()
    {
        WriteJson(new
        {
            servers = new[]
            {
                new { name = "bad", transport = "grpc" }
            }
        });

        var loader = new McpServerConfigLoader(_tempDir);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(McpTransportKind.Unknown, result.Value[0].Transport.Kind);
    }

    [Fact]
    public async Task LoadAsync_UnknownAuthType_MapsToUnknownAuthConfig()
    {
        WriteJson(new
        {
            servers = new[]
            {
                new
                {
                    name = "bad",
                    transport = "http",
                    endpoint = "https://example.com",
                    auth = new { type = "bearr" }
                }
            }
        });

        var loader = new McpServerConfigLoader(_tempDir);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.True(result.IsOk);
        var auth = Assert.IsType<UnknownAuthConfig>(result.Value[0].Auth);
        Assert.Equal("bearr", auth.Type);
    }

    [Fact]
    public async Task LoadAsync_AuthBlockPresentWithNoType_MapsToUnknownAuthConfig()
    {
        WriteJson(new
        {
            servers = new[]
            {
                new { name = "s", transport = "stdio", auth = new { tokenRef = "X" } }
            }
        });

        var loader = new McpServerConfigLoader(_tempDir);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.True(result.IsOk);
        var auth = Assert.IsType<UnknownAuthConfig>(result.Value[0].Auth);
        Assert.Equal(string.Empty, auth.Type);
    }

    [Fact]
    public async Task LoadAsync_NoAuthBlock_MapsToNoneAuthConfig()
    {
        WriteJson(new
        {
            servers = new[]
            {
                new { name = "s", transport = "stdio" }
            }
        });

        var loader = new McpServerConfigLoader(_tempDir);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.IsType<NoneAuthConfig>(result.Value[0].Auth);
    }

    private void WriteJson(object obj) =>
        File.WriteAllText(
            Path.Combine(_tempDir, "mcp-servers.json"),
            JsonSerializer.Serialize(obj));
}
