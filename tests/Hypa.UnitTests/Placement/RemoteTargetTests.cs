using Hypa.Placement.Domain;
using Xunit;

namespace Hypa.UnitTests.Placement;

public sealed class RemoteTargetTests
{
    [Fact]
    public void Empty_target_is_rejected()
    {
        Assert.False(RemoteTarget.TryValidate("", out _, out var error));
        Assert.Equal("missing value for --remote", error);
    }

    [Fact]
    public void Control_characters_are_rejected()
    {
        Assert.False(RemoteTarget.TryValidate("host\nalias", out _, out var error));
        Assert.Equal("SSH target must contain no control characters", error);
    }

    [Fact]
    public void Long_target_is_rejected()
    {
        var target = new string('a', RemoteTarget.MaxUtf8Bytes + 1);
        Assert.False(RemoteTarget.TryValidate(target, out _, out var error));
        Assert.Equal($"SSH target must be at most {RemoteTarget.MaxUtf8Bytes} bytes", error);
    }

    [Fact]
    public void Password_userinfo_is_rejected()
    {
        Assert.False(RemoteTarget.TryValidate("user:secret@host", out _, out var error));
        Assert.Equal("SSH target must not contain a password", error);
    }
}
