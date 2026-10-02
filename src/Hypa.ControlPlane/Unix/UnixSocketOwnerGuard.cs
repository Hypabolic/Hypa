namespace Hypa.ControlPlane.Unix;

/// <summary>
/// Fail-closed 0700 parent + 0600 socket.
/// Refuses an existing parent that is writable by group or others. Does not chmod it.
/// Windows: create the parent only (peer-cred / mode bits are Unix-only).
/// </summary>
public sealed class UnixSocketOwnerGuard : IUnixSocketModeGuard
{
    public void EnsurePrivateDirectory(string directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException(
                "Socket path has no parent directory. The socket must live in a private directory.");
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "Control-plane socket mode enforcement requires Linux or macOS.");
        }

#pragma warning disable CA1416
        if (Directory.Exists(directory))
            RefuseInsecureExistingParent(directory);

        Directory.CreateDirectory(directory);

        var want = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(directory, want);
        var mode = File.GetUnixFileMode(directory);
        if ((mode & ~want) != 0 || (mode & want) != want)
        {
            throw new IOException(
                $"Socket parent '{directory}' mode is {mode:G}; required 0700 (user rwx only).");
        }
#pragma warning restore CA1416
    }

    /// <summary>
    /// Migrate a directory that an older release created under the umask
    /// (0775 under umask 002). Tightens it to 0700 only when the current user
    /// owns it, so an upgrade does not leave the socket guard refusing it.
    /// Call it only for Hypa's own default runtime directory, never for a
    /// user-supplied socket parent. Returns true when the mode was changed.
    /// </summary>
    public static bool TightenOwnedDirectory(string directory)
    {
        if (string.IsNullOrEmpty(directory)
            || (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            || !Directory.Exists(directory))
        {
            return false;
        }

#pragma warning disable CA1416
        var mode = File.GetUnixFileMode(directory);
        if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0
            || !UnixPrivatePathGuard.IsDirectoryOwnedByCurrentUser(directory))
        {
            return false;
        }

        File.SetUnixFileMode(
            directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        return true;
    }

    public void HardenSocket(string socketPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(socketPath);
        if (OperatingSystem.IsWindows())
            return;

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "Control-plane socket mode enforcement requires Linux or macOS.");
        }

#pragma warning disable CA1416
        var want = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(socketPath, want);
        var mode = File.GetUnixFileMode(socketPath);
        if ((mode & ~want) != 0 || (mode & want) != want)
        {
            throw new IOException(
                $"Control-plane socket '{socketPath}' mode is {mode:G}; required 0600 (user rw only).");
        }
#pragma warning restore CA1416
    }

    private static void RefuseInsecureExistingParent(string directory)
    {
#pragma warning disable CA1416
        var mode = File.GetUnixFileMode(directory);
#pragma warning restore CA1416
        var sharedWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        if ((mode & sharedWrite) == 0)
            return;

        throw new UnauthorizedAccessException(
            $"Socket parent '{directory}' is writable by group or others. " +
            "The socket must live in a private directory (mode 0700). " +
            "Do not place the control-plane socket in a world-writable or group-writable directory.");
    }
}
