using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Hypa.AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// Open + read regular files without following symlinks.
/// Closes the TOCTOU window where an attribute check sees a regular file and a later
/// open follows a swapped symlink into host paths outside project_root.
/// </summary>
internal static class NoFollowFileReader
{
    /// <summary>
    /// Result of a nofollow open attempt.
    /// </summary>
    public enum OpenStatus
    {
        Ok = 0,
        /// <summary>Path is a symlink/reparse point (or open failed with ELOOP).</summary>
        SymlinkOrReparse = 1,
        /// <summary>Missing, unreadable, or other IO error.</summary>
        Unavailable = 2,
        /// <summary>Regular file larger than the caller cap (not read into memory).</summary>
        Oversize = 3,
        /// <summary>FIFO, socket, device, or other non-regular node.</summary>
        NotRegular = 4,
        /// <summary>Opened inode does not match the classify-time identity.</summary>
        IdentityMismatch = 5,
    }

    /// <summary>
    /// Open <paramref name="path"/> without following a final-component symlink, then
    /// read all bytes via the open descriptor. On success returns Ok + bytes.
    /// </summary>
    public static OpenStatus TryReadAllBytes(string path, out byte[] bytes, out string? detail)
    {
        bytes = Array.Empty<byte>();
        detail = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            detail = "empty path";
            return OpenStatus.Unavailable;
        }

        if (OperatingSystem.IsWindows())
            return TryReadWindows(path, out bytes, out detail);

        return TryReadUnix(path, out bytes, out detail);
    }

    /// <summary>
    /// Async wrapper; still performs a single nofollow open then fd-based read.
    /// </summary>
    public static Task<(OpenStatus Status, byte[] Bytes, string? Detail)> TryReadAllBytesAsync(
        string path,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var status = TryReadAllBytes(path, out var bytes, out var detail);
        return Task.FromResult((status, bytes, detail));
    }

    private static OpenStatus TryReadUnix(string path, out byte[] bytes, out string? detail)
    {
        bytes = Array.Empty<byte>();
        detail = null;

        // Prefer lstat first so directory walk callers get a clear symlink classification
        // without relying solely on open errno (still re-open with O_NOFOLLOW).
        if (IsUnixSymlink(path))
        {
            detail = "symlink";
            return OpenStatus.SymlinkOrReparse;
        }

        var flags = O_RDONLY | GetONoFollow() | GetOCloexec();
        var fd = open(path, flags);
        if (fd < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            if (err == GetEloop() || err == GetEloopAlt())
            {
                detail = "symlink (O_NOFOLLOW)";
                return OpenStatus.SymlinkOrReparse;
            }

            detail = "open errno=" + err;
            return OpenStatus.Unavailable;
        }

        try
        {
            // Re-validate: if the path became a symlink after open of a hard path, the FD
            // still refers to the non-link inode we opened. Policy also rejects if path is
            // now a reparse/symlink name (skip rather than copy under a link name).
            if (IsUnixSymlink(path))
            {
                detail = "symlink after open";
                return OpenStatus.SymlinkOrReparse;
            }

            using var handle = new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
            fd = -1; // ownership transferred
            using var fs = new FileStream(handle, FileAccess.Read);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            bytes = ms.ToArray();
            return OpenStatus.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            detail = ex.Message;
            return OpenStatus.Unavailable;
        }
        finally
        {
            if (fd >= 0)
                _ = close(fd);
        }
    }

    private static OpenStatus TryReadWindows(string path, out byte[] bytes, out string? detail)
    {
        bytes = Array.Empty<byte>();
        detail = null;

        try
        {
            // lstat-equivalent: attributes without following the final reparse component
            // is not fully available from managed APIs; GetAttributes does not follow the
            // last reparse on some versions. Open with OPEN_REPARSE_POINT so we never follow.
            var handle = CreateFileW(
                path,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                var err = Marshal.GetLastPInvokeError();
                detail = "CreateFile err=" + err;
                return OpenStatus.Unavailable;
            }

            using (handle)
            {
                if (!GetFileInformationByHandle(handle, out var info))
                {
                    detail = "GetFileInformationByHandle failed";
                    return OpenStatus.Unavailable;
                }

                if ((info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
                {
                    detail = "reparse point";
                    return OpenStatus.SymlinkOrReparse;
                }

                if ((info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                {
                    detail = "directory";
                    return OpenStatus.Unavailable;
                }

                using var fs = new FileStream(handle, FileAccess.Read);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                bytes = ms.ToArray();
                return OpenStatus.Ok;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            detail = ex.Message;
            return OpenStatus.Unavailable;
        }
    }

    /// <summary>
    /// True when the final path component is a symlink (Unix lstat). Public for unit tests.
    /// </summary>
    internal static bool IsUnixSymlink(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD())
            return false;

        return NoFollowWorkspaceWalker.TryLstatIdentity(path, out _, out var isSymlink, out _)
            && isSymlink;
    }

    private static int GetONoFollow()
    {
        // Linux: 0400000; Darwin/BSD: 0x0100
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            return 0x0100;
        return LinuxOpenFlags.NoFollow;
    }

    private static int GetOCloexec()
    {
        // Linux: 02000000; Darwin: 0x1000000
        if (OperatingSystem.IsMacOS())
            return 0x1000000;
        if (OperatingSystem.IsFreeBSD())
            return 0x00100000;
        return 0x80000;
    }

    private static int GetEloop() =>
        OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD() ? 62 : 40;

    /// <summary>Some Linux configs surface ELOOP as 40; keep alt for safety (unused equal).</summary>
    private static int GetEloopAlt() => 40;

    private const int O_RDONLY = 0;

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    // ── Windows ──────────────────────────────────────────────────────────────

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint dwFileAttributes;
        public long ftCreationTime;
        public long ftLastAccessTime;
        public long ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }
}
