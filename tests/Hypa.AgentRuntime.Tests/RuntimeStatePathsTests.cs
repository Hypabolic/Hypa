using Hypa.AgentRuntime.Infrastructure.Persistence;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class RuntimeStatePathsTests
{
    private const UnixFileMode OwnerRwx =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// The state root is also the default socket parent. The socket guard
    /// refuses a group-writable parent, so the root must not follow the umask.
    /// </summary>
    [SkippableFact]
    public void EnsureDirectory_creates_the_state_root_owner_only()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix modes only.");
        var root = Path.Combine(Path.GetTempPath(), "hypa-state-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new RuntimeStatePaths { StateDirectory = Path.Combine(root, "default") };
        try
        {
            paths.EnsureDirectory();

#pragma warning disable CA1416
            Assert.Equal(OwnerRwx, File.GetUnixFileMode(paths.StateDirectory));
#pragma warning restore CA1416
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
