using Hypa.Infrastructure.Mcp.Auth;
using ModelContextProtocol.Authentication;
using Xunit;

namespace Hypa.UnitTests.Mcp.Auth;

public sealed class McpOAuthTokenStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hypa-token-test-{Guid.NewGuid():N}");

    public McpOAuthTokenStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private McpOAuthTokenStore Store(string serverName = "test-server") =>
        new(serverName, _dir);

    private static TokenContainer Token(int expiresIn = 3600) => new()
    {
        TokenType = "Bearer",
        AccessToken = "access-abc",
        RefreshToken = "refresh-xyz",
        ExpiresIn = expiresIn,
        ObtainedAt = DateTimeOffset.UtcNow,
        Scope = "read",
    };

    [Fact]
    public async Task RoundTrip_StoreAndRetrieve_ReturnsSameToken()
    {
        var store = Store();
        var token = Token();

        await store.StoreTokensAsync(token, CancellationToken.None);
        var retrieved = await store.GetTokensAsync(CancellationToken.None);

        Assert.NotNull(retrieved);
        Assert.Equal("access-abc", retrieved.AccessToken);
        Assert.Equal("refresh-xyz", retrieved.RefreshToken);
        Assert.Equal("read", retrieved.Scope);
    }

    [Fact]
    public async Task GetTokens_MissingFile_ReturnsNull()
    {
        var store = Store("no-file-server");

        var result = await store.GetTokensAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetTokens_ExpiredToken_ReturnsNull()
    {
        var store = Store("expired-server");
        var expired = new TokenContainer
        {
            TokenType = "Bearer",
            AccessToken = "old-token",
            ExpiresIn = 3600,
            ObtainedAt = DateTimeOffset.UtcNow.AddHours(-2),
        };

        await store.StoreTokensAsync(expired, CancellationToken.None);
        var result = await store.GetTokensAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetTokens_NoExpiresIn_ReturnsToken()
    {
        var store = Store("no-expiry-server");
        var noExpiry = MinToken("forever-token");

        await store.StoreTokensAsync(noExpiry, CancellationToken.None);
        var result = await store.GetTokensAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("forever-token", result.AccessToken);
    }

    [Fact]
    public async Task MultipleServers_IsolatedByServerName()
    {
        var storeA = new McpOAuthTokenStore("server-a", _dir);
        var storeB = new McpOAuthTokenStore("server-b", _dir);

        await storeA.StoreTokensAsync(MinToken("token-a"), CancellationToken.None);
        await storeB.StoreTokensAsync(MinToken("token-b"), CancellationToken.None);

        var a = await storeA.GetTokensAsync(CancellationToken.None);
        var b = await storeB.GetTokensAsync(CancellationToken.None);

        Assert.Equal("token-a", a?.AccessToken);
        Assert.Equal("token-b", b?.AccessToken);
    }

    private static TokenContainer MinToken(string accessToken) => new()
    {
        TokenType = "Bearer",
        AccessToken = accessToken,
        ObtainedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task FilePermissions_AreUserOnly_OnUnix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var store = Store("perm-server");
        await store.StoreTokensAsync(Token(), CancellationToken.None);

        var filePath = Path.Combine(_dir, "mcp-oauth-tokens.json");
        Assert.True(File.Exists(filePath));

        var mode = File.GetUnixFileMode(filePath);
        Assert.True(mode.HasFlag(UnixFileMode.UserRead));
        Assert.True(mode.HasFlag(UnixFileMode.UserWrite));
        Assert.False(mode.HasFlag(UnixFileMode.GroupRead));
        Assert.False(mode.HasFlag(UnixFileMode.OtherRead));
    }

    [Fact]
    public async Task VersionMismatch_ReturnsNull()
    {
        var filePath = Path.Combine(_dir, "mcp-oauth-tokens.json");
        await File.WriteAllTextAsync(filePath, """{"version":99,"tokens":{}}""");

        var store = Store("any-server");
        var result = await store.GetTokensAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task DcrCredentials_StoreAndRetrieve_ReturnsSameValues()
    {
        var store = Store("dcr-store-server");
        await store.StoreTokensAsync(MinToken("access-token"), CancellationToken.None);
        await store.StoreDcrCredentialsAsync("my-client-id", "my-client-secret", CancellationToken.None);

        var (clientId, secret) = await store.GetDcrCredentialsAsync(CancellationToken.None);
        Assert.Equal("my-client-id", clientId);
        Assert.Equal("my-client-secret", secret);
    }

    [Fact]
    public async Task DcrCredentials_MissingFile_ReturnsNullTuple()
    {
        var store = Store("no-dcr-file-server");
        var (clientId, secret) = await store.GetDcrCredentialsAsync(CancellationToken.None);
        Assert.Null(clientId);
        Assert.Null(secret);
    }

    [Fact]
    public async Task DcrCredentials_NoTokenEntry_ReturnsNullTuple()
    {
        // No tokens stored for this server.
        var store = Store("no-dcr-entry-server");
        var (clientId, secret) = await store.GetDcrCredentialsAsync(CancellationToken.None);
        Assert.Null(clientId);
        Assert.Null(secret);
    }

    [Fact]
    public async Task DcrCredentials_StoreWhenNoTokenEntry_NothingPersisted()
    {
        // Store DCR credentials without a token entry.
        var store = Store("dcr-no-token-server");
        await store.StoreDcrCredentialsAsync("some-id", "some-secret", CancellationToken.None);

        // The file should not exist because StoreDcrCredentials returns early.
        var filePath = Path.Combine(_dir, "mcp-oauth-tokens.json");
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task DcrCredentials_PersistsAlongsideTokens()
    {
        var storeA = new McpOAuthTokenStore("dcr-multi-a", _dir);
        var storeB = new McpOAuthTokenStore("dcr-multi-b", _dir);

        await storeA.StoreTokensAsync(MinToken("token-a"), CancellationToken.None);
        await storeA.StoreDcrCredentialsAsync("id-a", "secret-a", CancellationToken.None);
        await storeB.StoreTokensAsync(MinToken("token-b"), CancellationToken.None);

        // Verify A has DCR credentials.
        var (aId, aSecret) = await storeA.GetDcrCredentialsAsync(CancellationToken.None);
        Assert.Equal("id-a", aId);
        Assert.Equal("secret-a", aSecret);

        // Verify B does not have DCR credentials.
        var (bId, bSecret) = await storeB.GetDcrCredentialsAsync(CancellationToken.None);
        Assert.Null(bId);
        Assert.Null(bSecret);

        // Verify tokens are still accessible for both.
        var aToken = await storeA.GetTokensAsync(CancellationToken.None);
        var bToken = await storeB.GetTokensAsync(CancellationToken.None);
        Assert.Equal("token-a", aToken?.AccessToken);
        Assert.Equal("token-b", bToken?.AccessToken);
    }

    [Fact]
    public async Task DcrCredentials_VersionUpgrade_ReadsCorrectly()
    {
        // Write a version 1 file (old schema without DCR fields).
        var filePath = Path.Combine(_dir, "mcp-oauth-tokens.json");
        await File.WriteAllTextAsync(filePath, """
            {"version":1,"tokens":{"legacy-server":{"tokenType":"Bearer","accessToken":"old-token","refreshToken":null,"expiresIn":3600,"obtainedAt":"2025-01-01T00:00:00+00:00","scope":"read"}}}
            """);

        var store = Store("legacy-server");
        var (clientId, secret) = await store.GetDcrCredentialsAsync(CancellationToken.None);
        Assert.Null(clientId);
        Assert.Null(secret);
    }
}
