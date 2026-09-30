using System.Runtime.InteropServices;
using Hypa.AgentRuntime.Infrastructure;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class LinuxOpenFlagsTests : IDisposable
{
    private const int ORdonly = 0;
    private const int OCloexec = 0x80000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hypa-oflags-" + Guid.NewGuid().ToString("N"));

    public LinuxOpenFlagsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string pathname, int flags);

    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int fd);

    [SkippableFact]
    public void Directory_and_no_follow_flags_open_a_directory_and_refuse_a_symlink()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux open flag bits.");

        var fd = Open(_dir, ORdonly | LinuxOpenFlags.Directory | LinuxOpenFlags.NoFollow | OCloexec);
        Assert.True(fd >= 0, "directory open failed errno=" + Marshal.GetLastPInvokeError());
        Close(fd);

        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_dir, "link");
        File.CreateSymbolicLink(link, target);
        var viaLink = Open(link, ORdonly | LinuxOpenFlags.Directory | LinuxOpenFlags.NoFollow | OCloexec);
        Assert.True(viaLink < 0, "a symlink must not open with the no-follow flag");
        if (viaLink >= 0)
            Close(viaLink);
    }
}
