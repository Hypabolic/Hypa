using Hypa.Infrastructure.Mcp.Auth;
using Xunit;

namespace Hypa.UnitTests.Mcp.Auth;

public sealed class SecretRedactionRegistryTests
{
    private readonly SecretRedactionRegistry _sut = new();

    [Fact]
    public void Redact_RegisteredSecret_IsReplaced()
    {
        _sut.Register("my-token-abc");
        var result = _sut.Redact("Authorization: Bearer my-token-abc");
        Assert.Equal("Authorization: Bearer [REDACTED]", result);
    }

    [Fact]
    public void Redact_MultipleSecrets_AllReplaced()
    {
        _sut.Register("secret1");
        _sut.Register("password99");
        var result = _sut.Redact("user=secret1 pass=password99");
        Assert.Equal("user=[REDACTED] pass=[REDACTED]", result);
    }

    [Fact]
    public void Redact_NoRegisteredSecrets_TextUnchanged()
    {
        var text = "nothing to see here";
        var result = _sut.Redact(text);
        Assert.Equal(text, result);
    }

    [Fact]
    public void Redact_UnregisteredValue_NotReplaced()
    {
        _sut.Register("known-secret");
        var result = _sut.Redact("some other value");
        Assert.Equal("some other value", result);
    }

    [Fact]
    public void Register_EmptyString_DoesNotRedactAnything()
    {
        _sut.Register(string.Empty);
        var result = _sut.Redact("something");
        Assert.Equal("something", result);
    }
}
