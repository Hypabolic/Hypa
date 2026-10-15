using Hypa.Runtime.Application;
using Xunit;

namespace Hypa.UnitTests.Application;

public sealed class CurrentDirectoryTests
{
    [Fact]
    public void TryGet_ReturnsCurrentDirectory_WhenItExists()
    {
        Assert.Equal(Directory.GetCurrentDirectory(), CurrentDirectory.TryGet());
    }

    [Fact]
    public void TryGet_ReturnsNull_WhenCurrentDirectoryWasDeleted()
    {
        // Windows cannot delete a directory that is a process's working directory.
        if (OperatingSystem.IsWindows()) return;

        // Test parallelization is disabled assembly-wide, so changing the process cwd is safe here.
        var previous = Directory.GetCurrentDirectory();
        var deleted = Path.Combine(Path.GetTempPath(), $"hypa-cwd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(deleted);
        try
        {
            Directory.SetCurrentDirectory(deleted);
            Directory.Delete(deleted);

            Assert.Null(CurrentDirectory.TryGet());
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
            if (Directory.Exists(deleted)) Directory.Delete(deleted);
        }
    }
}
