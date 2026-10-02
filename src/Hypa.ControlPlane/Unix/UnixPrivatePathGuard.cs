using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Hypa.ControlPlane.Unix;

/// <summary>
/// Owner-validated private directory checks for bridge sockets.
/// </summary>
internal static class UnixPrivatePathGuard
{
    private const int DarwinStatModeOffset = 4;
    private const int DarwinStatUidOffset = 16;
    private const int LinuxArm64StatModeOffset = 16;
    private const int LinuxArm64StatUidOffset = 24;
    private const int LinuxLp64StatModeOffset = 24;
    private const int LinuxLp64StatUidOffset = 28;

    internal static void EnsureBridgeDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "Bridge directory validation requires Linux or macOS.");
        }

        RejectLegacySharedBridgeAncestor(directory);

        var guard = new UnixSocketOwnerGuard();
        guard.EnsurePrivateDirectory(directory);
        ValidateDirectoryOwnedPrivate(directory, geteuid());
    }

    internal static bool IsDirectoryOwnedByCurrentUser(string path) =>
        (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        && TryGetStat(path, out var stMode, out var ownerUid)
        && (stMode & 0xF000u) == 0x4000u
        && ownerUid == geteuid();

    internal static void ValidateBridgeConnectPath(string socketPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(socketPath);
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        if (!Path.IsPathRooted(socketPath))
        {
            throw new UnauthorizedAccessException(
                $"Bridge connect path must be absolute: '{socketPath}'.");
        }

        if (!TryGetStat(socketPath, out var stMode, out var ownerUid))
        {
            throw new UnauthorizedAccessException(
                $"Bridge connect path '{socketPath}' is not a private socket inode.");
        }

        const uint S_IFMT = 0xF000;
        const uint S_IFSOCK = 0xC000;
        const uint S_IRWXUGO = 0x1FF;
        const uint MODE_0600 = 0x180;
        if ((stMode & S_IFMT) != S_IFSOCK)
        {
            throw new UnauthorizedAccessException(
                $"Bridge connect path '{socketPath}' is not a socket.");
        }

        if (ownerUid != geteuid())
        {
            throw new UnauthorizedAccessException(
                $"Bridge connect path '{socketPath}' is not owned by the current euid.");
        }

        if ((stMode & S_IRWXUGO) != MODE_0600)
        {
            throw new UnauthorizedAccessException(
                $"Bridge connect path '{socketPath}' mode must be exactly 0600.");
        }

        var parent = Path.GetDirectoryName(socketPath);
        if (string.IsNullOrEmpty(parent))
        {
            throw new UnauthorizedAccessException(
                $"Bridge connect path '{socketPath}' has no parent directory.");
        }

        EnsureBridgeDirectory(parent);
    }

    private static void RejectLegacySharedBridgeAncestor(string path)
    {
        var normalized = Path.GetFullPath(path).Replace('\\', '/');
        if (normalized.Contains("/hypa-ssh-bridge/", StringComparison.Ordinal)
            || normalized.EndsWith("/hypa-ssh-bridge", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "Shared bridge ancestor paths are not allowed. Use an atomic private directory.");
        }
    }

    private static void ValidateDirectoryOwnedPrivate(string path, uint selfUid)
    {
        if (!Directory.Exists(path))
            return;

        if (!TryGetStat(path, out var stMode, out var ownerUid))
        {
            throw new UnauthorizedAccessException(
                $"Bridge path ancestor '{path}' is not readable.");
        }

        const uint S_IFMT = 0xF000;
        const uint S_IFDIR = 0x4000;
        const uint S_IRWXUGO = 0x1FF;
        const uint MODE_0700 = 0x1C0;
        if ((stMode & S_IFMT) != S_IFDIR)
        {
            throw new UnauthorizedAccessException(
                $"Bridge path ancestor '{path}' is not a directory.");
        }

        if (ownerUid != selfUid)
        {
            throw new UnauthorizedAccessException(
                $"Bridge path ancestor '{path}' is not owned by the current user.");
        }

        if ((stMode & S_IRWXUGO) != MODE_0700)
        {
            throw new UnauthorizedAccessException(
                $"Bridge path ancestor '{path}' mode must be exactly 0700.");
        }
    }

    private static bool TryGetStat(string path, out uint stMode, out uint uid)
    {
        stMode = 0;
        uid = 0;
        var buf = new byte[512];
        if (OperatingSystem.IsMacOS())
        {
            // x86_64 Darwin has lstat$INODE64. arm64 Darwin has no such symbol;
            // its plain lstat already uses the 64-bit inode layout.
            var darwinResult = RuntimeInformation.ProcessArchitecture is Architecture.Arm64
                ? lstat(path, buf)
                : lstat_darwin_inode64(path, buf);
            if (darwinResult != 0)
                return false;
            stMode = BitConverter.ToUInt16(buf, DarwinStatModeOffset);
            uid = BitConverter.ToUInt32(buf, DarwinStatUidOffset);
        }
        else if (OperatingSystem.IsLinux())
        {
            if (lstat(path, buf) != 0)
                return false;
            var modeOffset = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? LinuxArm64StatModeOffset
                : LinuxLp64StatModeOffset;
            var uidOffset = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? LinuxArm64StatUidOffset
                : LinuxLp64StatUidOffset;
            stMode = BitConverter.ToUInt32(buf, modeOffset);
            uid = BitConverter.ToUInt32(buf, uidOffset);
        }
        else
        {
            return false;
        }

        try
        {
#pragma warning disable CA1416
            var perm = File.GetUnixFileMode(path);
#pragma warning restore CA1416
            stMode = (stMode & ~0x1FFu) | ((uint)perm & 0x1FFu);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return true;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    private static extern int lstat(string path, byte[] buf);

    [DllImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true)]
    private static extern int lstat_darwin_inode64(string path, byte[] buf);
}
