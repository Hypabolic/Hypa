using System.Collections.Concurrent;

namespace Hypa.Infrastructure.Mcp.Auth;

internal sealed class OAuthTokenCache
{
    private readonly ConcurrentDictionary<string, CachedOAuthToken> _cache = new();

    public string? TryGet(string key, int skewSeconds = 60)
    {
        if (_cache.TryGetValue(key, out var cached))
        {
            if (cached.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(skewSeconds))
                return cached.AccessToken;
        }
        return null;
    }

    public void Set(string key, string accessToken, int? expiresIn)
    {
        var expiresAt = expiresIn.HasValue
            ? DateTimeOffset.UtcNow.AddSeconds(expiresIn.Value)
            : DateTimeOffset.UtcNow.AddHours(1);

        _cache[key] = new CachedOAuthToken(accessToken, expiresAt);
    }

    public void Remove(string key) => _cache.TryRemove(key, out _);

    internal record CachedOAuthToken(string AccessToken, DateTimeOffset ExpiresAt);
}
