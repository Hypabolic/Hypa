using Hypa.Infrastructure.Mcp.Auth;
using Hypa.Infrastructure.Mcp.Secrets;
using ModelContextProtocol.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hypa.UnitTests.Mcp.Secrets;

public sealed class EnvironmentSecretResolverTests : IDisposable
{
    private const string TestEnvVar = "HYPA_TEST_SECRET_VAR_12345";
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"hypa-secret-{Guid.NewGuid():N}.txt");
    private readonly string _tokenDir = Path.Combine(Path.GetTempPath(), $"hypa-token-resolver-{Guid.NewGuid():N}");
    private readonly McpOAuthTokenStoreFactory _factory;
    private readonly EnvironmentSecretResolver _sut;

    public EnvironmentSecretResolverTests()
    {
        Directory.CreateDirectory(_tokenDir);
        _factory = new McpOAuthTokenStoreFactory(
            _tokenDir,
            new SecretRedactionRegistry(),
            NullLogger<McpOAuthTokenStore>.Instance);
        _sut = new(
            _factory,
            NullLogger<EnvironmentSecretResolver>.Instance);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TestEnvVar, null);
        if (File.Exists(_tempFile))
            File.Delete(_tempFile);
        try { Directory.Delete(_tokenDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task ResolveAsync_EnvPrefix_ReturnsVariableValue()
    {
        Environment.SetEnvironmentVariable(TestEnvVar, "supersecret");
        var result = await _sut.ResolveAsync($"env:{TestEnvVar}", CancellationToken.None);
        Assert.Equal("supersecret", result);
    }

    [Fact]
    public async Task ResolveAsync_EnvPrefix_UnsetVariable_ReturnsNull()
    {
        Environment.SetEnvironmentVariable(TestEnvVar, null);
        var result = await _sut.ResolveAsync($"env:{TestEnvVar}", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_FilePrefix_ReturnsTrimmedContent()
    {
        await File.WriteAllTextAsync(_tempFile, "  token-value  \n");
        var result = await _sut.ResolveAsync($"file:{_tempFile}", CancellationToken.None);
        Assert.Equal("token-value", result);
    }

    [Fact]
    public async Task ResolveAsync_FilePrefix_MissingFile_ReturnsNull()
    {
        var result = await _sut.ResolveAsync("file:/nonexistent/path/secret.txt", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_NoPrefix_ReturnsLiteralValue()
    {
        var result = await _sut.ResolveAsync("literal-token", CancellationToken.None);
        Assert.Equal("literal-token", result);
    }

    [Fact]
    public async Task ResolveAsync_DcrPrefix_WithStoredCredentials_ReturnsSecret()
    {
        var serverName = "dcr-test-server";
        var store = new McpOAuthTokenStore(serverName, _tokenDir);
        await store.StoreTokensAsync(new TokenContainer
        {
            TokenType = "Bearer",
            AccessToken = "access-token",
            ObtainedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        await store.StoreDcrCredentialsAsync("dcr-client-id", "dcr-secret-value", CancellationToken.None);

        var result = await _sut.ResolveAsync($"hypa:dcr:{serverName}", CancellationToken.None);
        Assert.Equal("dcr-secret-value", result);
    }

    [Fact]
    public async Task ResolveAsync_DcrPrefix_NoStoredCredentials_ReturnsNull()
    {
        var result = await _sut.ResolveAsync("hypa:dcr:nonexistent-server", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_DcrPrefix_MissingTokenEntry_ReturnsNull()
    {
        // Store DCR credentials for a server that has no token entry.
        var store = new McpOAuthTokenStore("no-token-server", _tokenDir);
        await store.StoreDcrCredentialsAsync("some-id", "some-secret", CancellationToken.None);

        var result = await _sut.ResolveAsync("hypa:dcr:no-token-server", CancellationToken.None);
        // StoreDcrCredentials returns early if no token entry exists, so nothing was persisted.
        Assert.Null(result);
    }
}
