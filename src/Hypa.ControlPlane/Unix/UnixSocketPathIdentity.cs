using System.Runtime.InteropServices;

namespace Hypa.ControlPlane.Unix;

/// <summary>
/// Device and inode of a Unix socket path. Dispose deletes the path only when
/// this identity still matches. A missing path is not a replacement.
/// </summary>
internal readonly record struct UnixSocketFileIdentity(ulong Device, ulong Inode);

/// <summary>
/// Capture and compare Unix socket path identity. Windows has no inode.
/// </summary>
internal static class UnixSocketPathIdentity
{
    private const int StatBufferBytes = 512;
    private const int DarwinInodeOffset = 8;
    private const int LinuxDeviceOffset = 0;
    private const int LinuxInodeOffset = 8;

    internal static bool TryRead(string path, out UnixSocketFileIdentity identity)
    {
        identity = default;
        if (string.IsNullOrEmpty(path))
            return false;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return false;

        var buf = new byte[StatBufferBytes];
        if (Lstat(path, buf) != 0)
            return false;

        if (OperatingSystem.IsMacOS())
        {
            identity = new UnixSocketFileIdentity(
                BitConverter.ToUInt32(buf, 0),
                BitConverter.ToUInt64(buf, DarwinInodeOffset));
            return identity.Inode != 0 || identity.Device != 0;
        }

        identity = new UnixSocketFileIdentity(
            BitConverter.ToUInt64(buf, LinuxDeviceOffset),
            BitConverter.ToUInt64(buf, LinuxInodeOffset));
        return identity.Inode != 0 || identity.Device != 0;
    }

    /// <summary>
    /// Delete <paramref name="path"/> only when it still has
    /// <paramref name="owned"/>. A missing path is success. A different
    /// identity is left in place.
    /// </summary>
    internal static void RemoveIfOwned(string path, UnixSocketFileIdentity owned)
    {
        if (string.IsNullOrEmpty(path))
            return;
        if (!TryRead(path, out var current))
            return;
        if (current != owned)
            return;
        try
        {
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static int Lstat(string path, byte[] buf)
    {
        if (OperatingSystem.IsMacOS()
            && RuntimeInformation.ProcessArchitecture is not Architecture.Arm64 and not Architecture.Arm)
        {
            return lstat_darwin_inode64(path, buf);
        }

        return lstat(path, buf);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int lstat(string path, byte[] buf);

    [DllImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true)]
    private static extern int lstat_darwin_inode64(string path, byte[] buf);
}
