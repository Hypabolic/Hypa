using System.Runtime.InteropServices;
using Hypa.AgentRuntime.Application;
using Microsoft.Win32.SafeHandles;

namespace Hypa.AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// Durable file-then-directory sync for journal create/append.
/// <para>
/// Two-phase hole: create/append the file → file fsync → parent-directory fsync.
/// A crash after create/rename and before the journal directory is durable can
/// orphan or lose the new <c>*.hyjr</c> dent. This helper closes that hole on
/// the write path. Tests inject <see cref="IDurableNativeSync"/> to record
/// phases or fail the directory phase; production has no process-global hook.
/// </para>
/// <para>
/// File durability: macOS <c>F_FULLFSYNC</c> (fcntl 51). Other Unix <c>fsync</c>.
/// Windows <c>FlushFileBuffers</c>. Parent directory is opened and synced with
/// the same primitive. <see cref="SafeFileHandle.DangerousAddRef"/> /
/// <see cref="SafeFileHandle.DangerousRelease"/> wrap every
/// <see cref="SafeFileHandle.DangerousGetHandle"/> use.
/// </para>
/// </summary>
internal sealed class DurableFileSync : IDurableFileSync
{
    /// <summary>MacOS <c>F_FULLFSYNC</c> (fcntl command 51).</summary>
    public const int MacOsFFullFsync = 51;

    public static DurableFileSync Production { get; } = new(ProductionDurableNativeSync.Instance);

    private readonly IDurableNativeSync _native;

    public DurableFileSync(IDurableNativeSync native)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }

    /// <summary>Named durability primitive used for the open file and its parent directory.</summary>
    public static string FileSyncKind => ProductionDurableNativeSync.Instance.FileSyncKind;

    /// <summary>
    /// Fsync <paramref name="fileHandle"/>, then fsync its parent directory.
    /// </summary>
    public void SyncFileThenDirectory(SafeFileHandle fileHandle, string filePath)
    {
        ArgumentNullException.ThrowIfNull(fileHandle);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        _native.SyncHandle(fileHandle);

        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir))
            _native.SyncDirectory(dir);
    }
}

/// <summary>Production OS durability: Darwin F_FULLFSYNC, Unix fsync, Windows FlushFileBuffers.</summary>
internal sealed class ProductionDurableNativeSync : IDurableNativeSync
{
    public static ProductionDurableNativeSync Instance { get; } = new();

    public string FileSyncKind
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return "FlushFileBuffers";
            if (OperatingSystem.IsMacOS())
                return "F_FULLFSYNC";
            return "fsync";
        }
    }

    public void SyncHandle(SafeFileHandle handle)
    {
        var addRef = false;
        try
        {
            handle.DangerousAddRef(ref addRef);
            var fd = handle.DangerousGetHandle();
            SyncNative(fd);
        }
        finally
        {
            if (addRef)
                handle.DangerousRelease();
        }
    }

    public void SyncDirectory(string directoryPath)
    {
        if (OperatingSystem.IsWindows())
        {
            SyncDirectoryWindows(directoryPath);
            return;
        }

        var flags = O_RDONLY | GetODirectory() | GetOCloexec();
        var fd = open(directoryPath, flags);
        if (fd < 0)
            throw new IOException(
                "open journal directory for fsync failed errno=" + Marshal.GetLastPInvokeError());

        var dirHandle = new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
        try
        {
            SyncHandle(dirHandle);
        }
        finally
        {
            dirHandle.Dispose();
        }
    }

    private static void SyncDirectoryWindows(string directoryPath)
    {
        var handle = CreateFileW(
            directoryPath,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException(
                "CreateFile journal directory for FlushFileBuffers failed err="
                + Marshal.GetLastPInvokeError());
        }

        try
        {
            Instance.SyncHandle(handle);
        }
        finally
        {
            handle.Dispose();
        }
    }

    private static void SyncNative(IntPtr fd)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!FlushFileBuffers(fd))
            {
                throw new IOException(
                    "FlushFileBuffers failed err=" + Marshal.GetLastPInvokeError());
            }

            return;
        }

        var nativeFd = fd.ToInt32();
        if (nativeFd < 0)
            throw new IOException("invalid fd for durable sync");

        int rc;
        if (OperatingSystem.IsMacOS())
            rc = fcntl(nativeFd, DurableFileSync.MacOsFFullFsync);
        else
            rc = fsync(nativeFd);

        if (rc != 0)
        {
            throw new IOException(
                Instance.FileSyncKind + " failed errno=" + Marshal.GetLastPInvokeError());
        }
    }

    private static int GetODirectory()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            return 0x100000;
        return LinuxOpenFlags.Directory;
    }

    private static int GetOCloexec()
    {
        if (OperatingSystem.IsMacOS())
            return 0x1000000;
        if (OperatingSystem.IsFreeBSD())
            return 0x00100000;
        return 0x80000;
    }

    private const int O_RDONLY = 0;

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd);

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

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
    private static extern bool FlushFileBuffers(IntPtr hFile);
}
