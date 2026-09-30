using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Hypa.AgentRuntime.Infrastructure.Logging;

/// <summary>
// / Owner-private log paths.
// / on a regular file only (<c>S_IFREG</c>).
/// latches disabled on open or rotation error; product writes must not throw
/// and must not block. Hypa adds the Unix 0600 rule from
/// <c>ScrollbackHistoryFile</c> and refuses a group/world-writable parent, a
/// symlink, and a FIFO, socket, device, or directory. <c>File.Exists</c> and
/// mode 0600 are not a regular-file proof. Sink errors latch disabled.
/// </summary>
public static class UnixLogPathSecurity
{
    public static bool IsUnix => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    public static UnixFileMode OwnerFileMode =>
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static UnixFileMode OwnerDirectoryMode =>
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static bool TryEnsurePrivateParent(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir))
            dir = ".";

        try
        {
            if (!IsUnix)
            {
                Directory.CreateDirectory(dir);
                return true;
            }

            if (Directory.Exists(dir))
                return IsSafeExistingDirectory(dir);

            Directory.CreateDirectory(dir);
#pragma warning disable CA1416
            File.SetUnixFileMode(dir, OwnerDirectoryMode);
#pragma warning restore CA1416
            return IsSafeExistingDirectory(dir);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryOpenAppend(string path, out FileStream? stream) =>
        TryOpen(path, append: true, out stream);

    public static bool TryOpenCreate(string path, out FileStream? stream) =>
        TryOpen(path, append: false, out stream);

    public static bool IsSymlink(string path)
    {
        if (!IsUnix || string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsOwnerOnlyFile(string path)
    {
        if (!IsUnix)
            return true;
        try
        {
#pragma warning disable CA1416
            var mode = File.GetUnixFileMode(path);
#pragma warning restore CA1416
            return (mode & ~OwnerFileMode) == 0 && (mode & OwnerFileMode) == OwnerFileMode;
        }
        catch
        {
            return false;
        }
    }

    public static bool HasSharedWrite(string path)
    {
        if (!IsUnix)
            return false;
        try
        {
#pragma warning disable CA1416
            var mode = File.GetUnixFileMode(path);
#pragma warning restore CA1416
            return (mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0;
        }
        catch
        {
            return true;
        }
    }

    public static bool HasOwnerWrite(string path)
    {
        if (!IsUnix)
            return true;
        try
        {
#pragma warning disable CA1416
            var mode = File.GetUnixFileMode(path);
#pragma warning restore CA1416
            return (mode & UnixFileMode.UserWrite) != 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsSafeExistingDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return false;
        if (IsSymlink(directory))
            return false;
        if (!HasOwnerWrite(directory))
            return false;
        return !HasSharedWrite(directory);
    }

    /// <summary>
    /// <c>lstat</c> <c>S_IFREG</c>. Missing is not a regular file.
    /// A FIFO, socket, device, or directory returns false without opening.
    /// </summary>
    public static bool IsRegularFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        if (!IsUnix)
        {
            try
            {
                return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.Directory) == 0;
            }
            catch
            {
                return false;
            }
        }

        return TryLstatMode(path, out var mode, out _) && IsRegularFileMode(mode);
    }

    /// <summary>
    /// Open a retained log for the JSON <c>event</c> sniff. Do not
    /// <c>FileStream</c>-open until <c>lstat</c> says <c>S_IFREG</c>. A FIFO
    /// returns false without blocking.
    /// </summary>
    public static bool TryOpenReadRegular(string path, out FileStream? stream)
    {
        stream = null;
        try
        {
            if (!IsUnix)
            {
                if (!File.Exists(path))
                    return false;
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return true;
            }

            if (!IsRegularFile(path))
                return false;

            var flags = O_Rdonly | GetONoFollow() | GetOCloexec() | GetONonblock();
            var fd = open(path, flags, 0);
            if (fd < 0)
                return false;

            try
            {
                if (!FdIsRegularFile(fd))
                {
                    _ = close(fd);
                    return false;
                }

                var handle = new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
                fd = -1;
                stream = new FileStream(handle, FileAccess.Read);
                return true;
            }
            finally
            {
                if (fd >= 0)
                    _ = close(fd);
            }
        }
        catch
        {
            stream?.Dispose();
            stream = null;
            return false;
        }
    }

    private static bool TryOpen(string path, bool append, out FileStream? stream)
    {
        stream = null;
        try
        {
            if (IsSymlink(path))
                return false;
            if (File.Exists(path) && IsUnix && !IsOwnerOnlyFile(path))
                return false;

            if (!IsUnix)
            {
                var options = new FileStreamOptions
                {
                    Mode = append ? FileMode.Append : FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.ReadWrite,
                };
                stream = new FileStream(path, options);
                return true;
            }

            // Missing is fine (O_CREAT). An existing FIFO/socket/device/dir
            // must not reach open — that can block under the sink gate.
            if (TryLstatMode(path, out var lmode, out var lerrno))
            {
                if (!IsRegularFileMode(lmode))
                    return false;
            }
            else if (lerrno != Enoent)
            {
                return false;
            }

            var flags = OpenWriteFlags(append);
            var fd = open(path, flags, 0x180);
            if (fd < 0)
                return false;

            try
            {
                _ = fchmod(fd, 0x180);
                if (IsSymlink(path) || !IsOwnerOnlyFile(path) || !FdIsRegularFile(fd))
                {
                    _ = close(fd);
                    return false;
                }

                var handle = new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
                fd = -1;
                stream = new FileStream(handle, FileAccess.Write);
                // Darwin FileStream issues pwrite; pwrite ignores O_APPEND.
                if (append)
                    stream.Seek(0, SeekOrigin.End);
                return true;
            }
            finally
            {
                if (fd >= 0)
                    _ = close(fd);
            }
        }
        catch
        {
            stream?.Dispose();
            stream = null;
            return false;
        }
    }

    private static int OpenWriteFlags(bool append)
    {
        // Linux: O_WRONLY|O_CREAT|O_NOFOLLOW|O_CLOEXEC|O_NONBLOCK + O_APPEND or O_TRUNC.
        // Darwin: same names, different bits.
        // O_NONBLOCK: a raced FIFO must fail open (ENXIO) instead of blocking.
        if (OperatingSystem.IsMacOS())
        {
            var flags = 0x0001 | 0x0200 | 0x0100 | 0x1000000 | GetONonblock();
            return flags | (append ? 0x0008 : 0x0400);
        }

        var linux = 0x0001 | 0x0040 | LinuxOpenFlags.NoFollow | 0x80000 | GetONonblock();
        return linux | (append ? 0x0400 : 0x0200);
    }

    private static bool FdIsRegularFile(int fd)
    {
        var statBuf = new byte[256];
        if (FstatForMode(fd, statBuf) != 0)
            return false;
        return IsRegularFileMode(ReadStatMode(statBuf));
    }

    private static bool TryLstatMode(string path, out int mode, out int errno)
    {
        mode = 0;
        errno = 0;
        var statBuf = new byte[256];
        if (LstatForMode(path, statBuf) != 0)
        {
            errno = Marshal.GetLastPInvokeError();
            return false;
        }

        mode = ReadStatMode(statBuf);
        return true;
    }

    private static int ReadStatMode(byte[] statBuf)
    {
        var off = StatModeOffset();
        return (ushort)(statBuf[off] | (statBuf[off + 1] << 8));
    }

    private static bool IsRegularFileMode(int mode) => (mode & S_Ifmt) == S_Ifreg;

    // Copied from NoFollowWorkspaceWalker: Darwin/FreeBSD 4, Linux arm64 16, else 24.
    // x86_64 Darwin uses lstat$INODE64 / fstat$INODE64; arm64 Darwin uses plain lstat/fstat.
    private static int StatModeOffset()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            return 4;
        if (OperatingSystem.IsLinux()
            && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return 16;
        return 24;
    }

    private static int LstatForMode(string path, byte[] statBuf)
    {
        if (OperatingSystem.IsMacOS()
            && RuntimeInformation.ProcessArchitecture is not Architecture.Arm64
                and not Architecture.Arm)
            return lstat_darwin_inode64(path, ref statBuf[0]);
        return lstat(path, ref statBuf[0]);
    }

    private static int FstatForMode(int fd, byte[] statBuf)
    {
        if (OperatingSystem.IsMacOS()
            && RuntimeInformation.ProcessArchitecture is not Architecture.Arm64
                and not Architecture.Arm)
            return fstat_darwin_inode64(fd, ref statBuf[0]);
        return fstat(fd, ref statBuf[0]);
    }

    private static int GetONoFollow()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            return 0x0100;
        return LinuxOpenFlags.NoFollow;
    }

    private static int GetOCloexec()
    {
        if (OperatingSystem.IsMacOS())
            return 0x1000000;
        if (OperatingSystem.IsFreeBSD())
            return 0x00100000;
        return 0x80000;
    }

    private static int GetONonblock()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            return 0x0004;
        return 0x800;
    }

    private const int O_Rdonly = 0;
    private const int Enoent = 2;
    private const int S_Ifmt = 0xF000;
    private const int S_Ifreg = 0x8000;

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string pathname, int flags, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int fchmod(int fd, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int fstat(int fd, ref byte buf);

    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int fstat_darwin_inode64(int fd, ref byte buf);

    [DllImport("libc", SetLastError = true)]
    private static extern int lstat(string pathname, ref byte buf);

    [DllImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true)]
    private static extern int lstat_darwin_inode64(string pathname, ref byte buf);
}
