using Hypa.Cli.Mux;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The attach client creates the socket parent before it spawns the mux.
/// A umask-mode parent (0775 under umask 002) made the server refuse to
/// start, and the mux.log sink refused it too, so no log was written.
/// </summary>
public sealed class MuxSocketDirectoryTests
{
    private const UnixFileMode OwnerRwx =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [SkippableFact]
    public void Supervisor_creates_the_socket_directory_owner_only()
    {
        SkipUnlessUnix();
        var root = NewTempRoot();
        var dir = Path.Combine(root, "runtime", "default");
        try
        {
            ProcessMuxSupervisor.EnsurePrivateSocketDirectory(dir, "default");

#pragma warning disable CA1416
            Assert.Equal(OwnerRwx, File.GetUnixFileMode(dir));
#pragma warning restore CA1416
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    public void Supervisor_refuses_a_group_writable_socket_directory_without_chmod()
    {
        SkipUnlessUnix();
        var dir = NewTempRoot();
        var shared = OwnerRwx | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        try
        {
#pragma warning disable CA1416
            File.SetUnixFileMode(dir, shared);
#pragma warning restore CA1416

            var ex = Assert.Throws<MuxAttachException>(() =>
                ProcessMuxSupervisor.EnsurePrivateSocketDirectory(dir, "default"));

            Assert.Contains("writable by group or others", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("chmod", ex.Message, StringComparison.Ordinal);
#pragma warning disable CA1416
            Assert.Equal(shared, File.GetUnixFileMode(dir));
#pragma warning restore CA1416
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Fix_hint_names_chmod_only_for_the_default_runtime_directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hypa-runtime", "default");

        Assert.Equal(
            $" Fix it with: chmod 700 '{dir}'",
            ProcessMuxSupervisor.PrivateDirectoryFixHint(dir, dir));
        Assert.Equal("", ProcessMuxSupervisor.PrivateDirectoryFixHint(dir, Path.GetTempPath()));
        Assert.Equal("", ProcessMuxSupervisor.PrivateDirectoryFixHint(dir, null));
    }

    [Fact]
    public void Not_ready_message_points_at_a_log_that_exists()
    {
        var dir = NewTempRoot();
        var socketPath = Path.Combine(dir, "hypa.sock");
        var logPath = Path.Combine(dir, "mux.log");
        File.WriteAllText(logPath, "{}\n");
        try
        {
            Assert.Equal(
                $"Mux server did not become ready at {socketPath}. See {logPath}.",
                ProcessMuxSupervisor.NotReadyMessage(socketPath, logPath, "work"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Not_ready_message_without_a_log_names_the_foreground_command()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hypa-missing-" + Guid.NewGuid().ToString("N"));
        var socketPath = Path.Combine(dir, "hypa.sock");
        var logPath = Path.Combine(dir, "mux.log");

        Assert.Equal(
            $"Mux server did not become ready at {socketPath}. " +
            $"It wrote no log at {logPath}. Run 'hypa mux serve --session work' to see the startup error.",
            ProcessMuxSupervisor.NotReadyMessage(socketPath, logPath, "work"));
    }

    private static void SkipUnlessUnix() =>
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix modes only.");

    private static string NewTempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hypa-mux-dir-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
