using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Xunit;

namespace Hypa.UnitTests.Connectivity;

public sealed class DevicePairingStatePathsTests
{
    [Fact]
    public void Resolve_uses_login_home_when_env_is_empty()
    {
        var path = DevicePairingStatePaths.Resolve(null, null, null, "/tmp/hypa-login");
        Assert.EndsWith(
            Path.Combine(".local", "state", "hypa", DevicePairingPaths.StateSegment),
            path,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveLoginHome_is_available_on_this_host()
    {
        var home = DevicePairingStatePaths.ResolveLoginHome();
        Assert.False(string.IsNullOrWhiteSpace(home));
        Assert.True(Directory.Exists(home));
        var directory = DevicePairingStatePaths.Resolve(null, null, null, home);
        Assert.False(string.IsNullOrWhiteSpace(directory));
    }
}
