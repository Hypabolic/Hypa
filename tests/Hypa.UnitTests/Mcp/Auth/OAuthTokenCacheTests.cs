using Hypa.Infrastructure.Mcp.Auth;
using Xunit;

namespace Hypa.UnitTests.Mcp.Auth;

public sealed class OAuthTokenCacheTests
{
    private readonly OAuthTokenCache _sut = new();

    [Fact]
    public void TryGet_FreshToken_ReturnsToken()
    {
        _sut.Set("key", "my-token", expiresIn: 3600);
        Assert.Equal("my-token", _sut.TryGet("key"));
    }

    [Fact]
    public void TryGet_UnknownKey_ReturnsNull()
    {
        Assert.Null(_sut.TryGet("unknown-key"));
    }

    [Fact]
    public void TryGet_NullExpiresIn_DefaultsToOneHour_ReturnsToken()
    {
        _sut.Set("key", "my-token", expiresIn: null);
        Assert.Equal("my-token", _sut.TryGet("key"));
    }

    [Fact]
    public void TryGet_TokenWithinDefaultSkew_ReturnsNull()
    {
        // Token expires in 30 seconds, default skew is 60 seconds — treated as expired.
        _sut.Set("key", "my-token", expiresIn: 30);
        Assert.Null(_sut.TryGet("key"));
    }

    [Fact]
    public void TryGet_TokenJustOutsideSkew_ReturnsToken()
    {
        // Token expires in 120 seconds, default skew is 60 — still valid.
        _sut.Set("key", "my-token", expiresIn: 120);
        Assert.Equal("my-token", _sut.TryGet("key"));
    }

    [Fact]
    public void TryGet_WithCustomSkew_RespectsCustomSkew()
    {
        // Expires in 30s — invalid under default 60s skew, valid under custom 10s skew.
        _sut.Set("key", "my-token", expiresIn: 30);
        Assert.Equal("my-token", _sut.TryGet("key", skewSeconds: 10));
    }

    [Fact]
    public void TryGet_AfterRemove_ReturnsNull()
    {
        _sut.Set("key", "my-token", expiresIn: 3600);
        _sut.Remove("key");
        Assert.Null(_sut.TryGet("key"));
    }

    [Fact]
    public void Set_OverwritesExistingToken()
    {
        _sut.Set("key", "first-token", expiresIn: 3600);
        _sut.Set("key", "second-token", expiresIn: 3600);
        Assert.Equal("second-token", _sut.TryGet("key"));
    }

    [Fact]
    public void Remove_NonExistentKey_DoesNotThrow()
    {
        // Should not throw even when the key was never set.
        _sut.Remove("ghost-key");
    }

    [Fact]
    public void TryGet_MultipleKeys_AreIndependent()
    {
        _sut.Set("key-a", "token-a", expiresIn: 3600);
        _sut.Set("key-b", "token-b", expiresIn: 3600);

        Assert.Equal("token-a", _sut.TryGet("key-a"));
        Assert.Equal("token-b", _sut.TryGet("key-b"));
    }
}
