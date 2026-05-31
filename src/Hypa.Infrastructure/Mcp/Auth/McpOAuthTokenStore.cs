using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;

namespace Hypa.Infrastructure.Mcp.Auth;

internal sealed class McpOAuthTokenStore : ITokenCache
{
    private const int CurrentVersion = 2;

    private readonly string _serverName;
    private readonly string _filePath;
    private readonly SecretRedactionRegistry _redactionRegistry;
    private readonly ILogger<McpOAuthTokenStore> _logger;

    public McpOAuthTokenStore(string serverName, string storagePath)
        : this(serverName, storagePath, new SecretRedactionRegistry(), Microsoft.Extensions.Logging.Abstractions.NullLogger<McpOAuthTokenStore>.Instance)
    { }

    public McpOAuthTokenStore(
        string serverName,
        string storagePath,
        SecretRedactionRegistry redactionRegistry,
        ILogger<McpOAuthTokenStore> logger)
    {
        _serverName = serverName;
        _filePath = Path.Combine(storagePath, "mcp-oauth-tokens.json");
        _redactionRegistry = redactionRegistry;
        _logger = logger;
    }

    public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
            return null;

        McpOAuthTokenFileJson? file;
        try
        {
            await using var stream = File.OpenRead(_filePath);
            file = await JsonSerializer.DeserializeAsync(
                stream,
                McpOAuthTokenJsonContext.Default.McpOAuthTokenFileJson,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read mcp-oauth-tokens.json");
            return null;
        }

        if (file is null)
            return null;

        if (file.Version > CurrentVersion)
        {
            _logger.LogWarning("mcp-oauth-tokens.json version {Version} is newer than supported {Supported}; ignoring tokens",
                file.Version, CurrentVersion);
            return null;
        }

        if (!file.Tokens.TryGetValue(_serverName, out var entry))
            return null;

        var token = ToTokenContainer(entry);

        if (IsExpired(token))
            return null;

        if (!string.IsNullOrEmpty(token.AccessToken))
            _redactionRegistry.Register(token.AccessToken);
        if (!string.IsNullOrEmpty(token.RefreshToken))
            _redactionRegistry.Register(token.RefreshToken);

        return token;
    }

    public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(dir);

        var existing = await LoadFileAsync(cancellationToken);
        existing[_serverName] = ToEntry(tokens);

        var file = new McpOAuthTokenFileJson(CurrentVersion, existing);
        var tempPath = Path.Combine(dir, $"mcp-oauth-tokens.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    file,
                    McpOAuthTokenJsonContext.Default.McpOAuthTokenFileJson,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            SetSecurePermissions(tempPath);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        if (!string.IsNullOrEmpty(tokens.AccessToken))
            _redactionRegistry.Register(tokens.AccessToken);
        if (!string.IsNullOrEmpty(tokens.RefreshToken))
            _redactionRegistry.Register(tokens.RefreshToken);
    }

    private async Task<Dictionary<string, McpOAuthTokenEntryJson>> LoadFileAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
            return new Dictionary<string, McpOAuthTokenEntryJson>();

        try
        {
            await using var stream = File.OpenRead(_filePath);
            var file = await JsonSerializer.DeserializeAsync(
                stream,
                McpOAuthTokenJsonContext.Default.McpOAuthTokenFileJson,
                ct);

            if (file is null || file.Version > CurrentVersion)
                return new Dictionary<string, McpOAuthTokenEntryJson>();

            return file.Tokens;
        }
        catch
        {
            return new Dictionary<string, McpOAuthTokenEntryJson>();
        }
    }

    private static bool IsExpired(TokenContainer token)
    {
        if (token.ExpiresIn is null)
            return false;

        var expiresAt = token.ObtainedAt.AddSeconds(token.ExpiresIn.Value);
        return DateTimeOffset.UtcNow >= expiresAt;
    }

    private static void SetSecurePermissions(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best effort; file permissions are not critical for correctness
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static TokenContainer ToTokenContainer(McpOAuthTokenEntryJson entry) => new()
    {
        TokenType = entry.TokenType,
        AccessToken = entry.AccessToken,
        RefreshToken = entry.RefreshToken!,
        ExpiresIn = entry.ExpiresIn,
        ObtainedAt = DateTimeOffset.TryParse(entry.ObtainedAt, out var dt) ? dt : DateTimeOffset.UtcNow,
        Scope = entry.Scope!,
    };

    private static McpOAuthTokenEntryJson ToEntry(TokenContainer t) => new(
        t.TokenType ?? "Bearer",
        t.AccessToken,
        t.RefreshToken,
        t.ExpiresIn,
        t.ObtainedAt.ToString("O"),
        t.Scope);

    public async Task StoreDcrCredentialsAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(dir);

        var existing = await LoadFileAsync(ct);

        if (!existing.TryGetValue(_serverName, out var entry))
        {
            _logger.LogWarning(
                "Cannot persist DCR credentials for server '{Server}': no token entry found. " +
                "The SDK may not have stored tokens yet.",
                _serverName);
            return;
        }

        entry = entry with { DcrClientId = clientId, DcrClientSecret = clientSecret };
        existing[_serverName] = entry;

        var file = new McpOAuthTokenFileJson(CurrentVersion, existing);
        var tempPath = Path.Combine(dir, $"mcp-oauth-tokens.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    file,
                    McpOAuthTokenJsonContext.Default.McpOAuthTokenFileJson,
                    ct);
                await stream.FlushAsync(ct);
            }

            SetSecurePermissions(tempPath);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        _redactionRegistry.Register(clientSecret);
    }

    public async ValueTask<(string? ClientId, string? Secret)> GetDcrCredentialsAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
            return (null, null);

        McpOAuthTokenFileJson? file;
        try
        {
            await using var stream = File.OpenRead(_filePath);
            file = await JsonSerializer.DeserializeAsync(
                stream,
                McpOAuthTokenJsonContext.Default.McpOAuthTokenFileJson,
                ct);
        }
        catch
        {
            return (null, null);
        }

        if (file is null || !file.Tokens.TryGetValue(_serverName, out var entry))
            return (null, null);

        if (!string.IsNullOrEmpty(entry.DcrClientSecret))
            _redactionRegistry.Register(entry.DcrClientSecret);

        return (entry.DcrClientId, entry.DcrClientSecret);
    }
}
