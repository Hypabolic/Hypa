using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Hypa.Terminal.Pty;

/// <summary>
/// Private one-shot AF_UNIX path material for handoff / FD-pass.
/// Enforces same-user threat model: 0700 directory, 0600 socket, peer-UID check.
/// </summary>
internal static class UnixPrivateSocketPath
{
    private const int SOL_SOCKET_LINUX = 1;
    private const int SO_PEERCRED_LINUX = 17;

    /// <summary>
    /// Create a 0700 directory under XDG_RUNTIME_DIR (preferred) or temp, and a socket path inside it.
    /// Paths stay short for sockaddr_un limits (macOS 104 bytes). Throws when mode cannot be enforced.
    /// </summary>
    public static (string Directory, string SocketPath) Create(string socketNamePrefix)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Private UDS paths are Unix-only.");
        ArgumentException.ThrowIfNullOrEmpty(socketNamePrefix);

        // Short tokens: macOS sun_path is 104 bytes; long temp roots leave little room.
        var token = Guid.NewGuid().ToString("N");
        var root = ResolveRuntimeRoot();
        // Per-process private parent (reused) keeps sockets short: {root}/hp{pid}/s{token8}.sock
        var dir = Path.Combine(root, "hp" + Environment.ProcessId.ToString("x"));

        // Concurrent tests may race TryDeleteDirectory between CreateDirectory and chmod.
        // Retry a few times rather than fail closed on a transient empty-dir cleanup.
        Exception? last = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Directory.CreateDirectory(dir);
                EnforceDirectoryMode0700(dir);
                last = null;
                break;
            }
            catch (Exception ex) when (
                ex is DirectoryNotFoundException
                or FileNotFoundException
                or IOException)
            {
                last = ex;
            }
        }

        if (last is not null)
            throw last;

        // Prefix contributes a 1-char tag so handoff vs fdpass are distinguishable in debug.
        var tag = socketNamePrefix.Length > 0 ? socketNamePrefix[0] : 's';
        var sockName = string.Concat(tag.ToString(), token.AsSpan(0, 10), ".sock");
        var socketPath = Path.Combine(dir, sockName);
        if (socketPath.Length > 100)
        {
            throw new IOException(
                $"Private UDS path length {socketPath.Length} exceeds sockaddr_un budget: {socketPath}");
        }

        return (dir, socketPath);
    }

    /// <summary>Set socket inode to 0600 and verify owner is this euid. Throws on failure.</summary>
    public static void HardenSocketFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var mode = File.GetUnixFileMode(path);
            var allowed = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if ((mode & ~allowed) != 0)
            {
                throw new IOException(
                    $"Handoff socket '{path}' mode is {mode:G}; required 0600 (user rw only).");
            }

            if (!TryGetOwnerUid(path, out var ownerUid) || ownerUid != geteuid())
            {
                throw new IOException(
                    $"Handoff socket '{path}' is not owned by the current euid (same-user model).");
            }

            return;
        }

        throw new PlatformNotSupportedException("Unix socket harden is Unix-only.");
    }

    /// <summary>
    /// Connect-side path validation (mirrors native <c>path_is_private_socket</c>).
    /// Absolute path, socket inode owned by euid with exact mode 0600, parent directory
    /// owned by euid with exact mode 0700. Call before <c>Connect</c>.
    /// </summary>
    public static void ValidateConnectPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Private UDS paths are Unix-only.");

        if (!Path.IsPathRooted(path))
        {
            throw new UnauthorizedAccessException(
                $"Handoff connect path must be absolute (same-user private UDS): '{path}'.");
        }

        // Socket inode: must exist, be S_IFSOCK, owned by euid, exact mode 0600.
        if (!TryGetStat(path, out var stMode, out var ownerUid))
        {
            throw new UnauthorizedAccessException(
                $"Handoff connect path '{path}' is not a private socket inode.");
        }

        // S_IFMT=0170000, S_IFSOCK=0140000; permission mask 0777.
        const uint S_IFMT = 0xF000;
        const uint S_IFSOCK = 0xC000;
        const uint S_IRWXUGO = 0x1FF; // 0777
        const uint MODE_0600 = 0x180; // 0600 user rw only
        const uint MODE_0700 = 0x1C0; // 0700 user rwx only
        if ((stMode & S_IFMT) != S_IFSOCK)
        {
            throw new UnauthorizedAccessException(
                $"Handoff connect path '{path}' is not a socket.");
        }

        if (ownerUid != geteuid())
        {
            throw new UnauthorizedAccessException(
                $"Handoff connect path '{path}' is not owned by the current euid.");
        }

        // Exact 0600 — reject group/other bits and owner-mode deviations (0000, 0400, 0700, …).
        if ((stMode & S_IRWXUGO) != MODE_0600)
        {
            throw new UnauthorizedAccessException(
                $"Handoff connect path '{path}' mode must be exactly 0600.");
        }

        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent))
        {
            throw new UnauthorizedAccessException(
                $"Handoff connect path '{path}' has no parent directory.");
        }

        if (!TryGetStat(parent, out var parentMode, out var parentUid)
            || parentUid != geteuid())
        {
            throw new UnauthorizedAccessException(
                $"Handoff connect parent '{parent}' is not owned by the current euid.");
        }

        // Parent must be a directory with exact private mode 0700.
        const uint S_IFDIR = 0x4000;
        if ((parentMode & S_IFMT) != S_IFDIR)
        {
            throw new UnauthorizedAccessException(
                $"Handoff connect parent '{parent}' is not a directory.");
        }

        if ((parentMode & S_IRWXUGO) != MODE_0700)
        {
            throw new UnauthorizedAccessException(
                $"Handoff connect parent '{parent}' mode must be exactly 0700.");
        }
    }

    // Same table as NoFollowWorkspaceWalker.StatModeOffset.
    // Darwin INODE64: st_mode@4 (ushort) st_uid@16.
    // glibc linux-arm64: st_mode@16 st_uid@24. Other Linux LP64: st_mode@24 st_uid@28.
    private const int DarwinStatModeOffset = 4;
    private const int DarwinStatUidOffset = 16;
    private const int LinuxArm64StatModeOffset = 16;
    private const int LinuxArm64StatUidOffset = 24;
    private const int LinuxLp64StatModeOffset = 24;
    private const int LinuxLp64StatUidOffset = 28;

    /// <summary>
    /// lstat-based mode + uid. Returns false when path is missing or lstat fails.
    /// st_mode is the full mode word (file type + permission bits).
    /// Permission bits are overlaid from <see cref="File.GetUnixFileMode"/> so
    /// 0600/0700 checks do not depend on glibc stat padding.
    /// </summary>
    private static unsafe bool TryGetStat(string path, out uint stMode, out uint uid)
    {
        stMode = 0;
        uid = 0;
        const int n = 512;
        byte* buf = stackalloc byte[n];
        for (var i = 0; i < n; i++)
            buf[i] = 0;

        if (OperatingSystem.IsMacOS())
        {
            // x86_64 Darwin: lstat$INODE64. arm64/arm Darwin lstat is already INODE64.
            var rc = RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm
                ? lstat(path, buf)
                : lstat_darwin_inode64(path, buf);
            if (rc != 0)
                return false;
            stMode = *(ushort*)(buf + DarwinStatModeOffset);
            uid = *(uint*)(buf + DarwinStatUidOffset);
            OverlayUnixPermissionBits(path, ref stMode);
            return true;
        }

        if (OperatingSystem.IsLinux())
        {
            if (lstat(path, buf) != 0)
                return false;
            var (modeOff, uidOff) = LinuxStatOffsets();
            stMode = *(uint*)(buf + modeOff);
            uid = *(uint*)(buf + uidOff);
            OverlayUnixPermissionBits(path, ref stMode);
            return true;
        }

        return false;
    }

    private static (int ModeOffset, int UidOffset) LinuxStatOffsets()
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return (LinuxArm64StatModeOffset, LinuxArm64StatUidOffset);
        return (LinuxLp64StatModeOffset, LinuxLp64StatUidOffset);
    }

    private static void OverlayUnixPermissionBits(string path, ref uint stMode)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;
        try
        {
            // BCL permission bits are ABI-independent. Keep S_IFMT from lstat.
            var perms = (uint)File.GetUnixFileMode(path) & 0xFFF;
            stMode = (stMode & 0xFFFFF000u) | perms;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Keep lstat permission bits when the BCL overlay is unavailable.
        }
    }

    /// <summary>
    /// After Accept/Connect, require peer effective UID == our euid.
    /// Prevents local peer-steal of a mis-permissioned path.
    /// </summary>
    public static void ValidatePeerIsSelf(Socket connected)
    {
        ArgumentNullException.ThrowIfNull(connected);
        var fd = connected.SafeHandle.DangerousGetHandle().ToInt32();
        ValidatePeerIsSelf(fd);
    }

    public static void ValidatePeerIsSelf(int connectedSocketFd)
    {
        if (connectedSocketFd < 0)
            throw new ArgumentOutOfRangeException(nameof(connectedSocketFd));

        var self = geteuid();
        uint peerUid;
        if (OperatingSystem.IsLinux())
            peerUid = GetPeerUidLinux(connectedSocketFd);
        else if (OperatingSystem.IsMacOS())
            peerUid = GetPeerUidMac(connectedSocketFd);
        else
            throw new PlatformNotSupportedException("Peer credential check requires Linux or macOS.");

        if (peerUid != self)
        {
            throw new UnauthorizedAccessException(
                $"AF_UNIX peer uid {peerUid} does not match local euid {self} (H-08 same-user model).");
        }
    }

    /// <summary>
    /// Best-effort remove of a socket's private directory when empty.
    /// Per-process dirs are shared across sockets; non-empty dirs are left in place.
    /// </summary>
    public static void TryDeleteDirectory(string? dir)
    {
        if (string.IsNullOrEmpty(dir))
            return;
        try
        {
            if (!Directory.Exists(dir))
                return;
            // Only remove if no remaining entries (other live sockets may share the dir).
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir, recursive: false);
        }
        catch
        {
            // cleanup best-effort
        }
    }

    public static void TryUnlink(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // cleanup best-effort
        }
    }

    private static string ResolveRuntimeRoot()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(xdg) && Directory.Exists(xdg))
        {
            try
            {
                var probe = Path.Combine(xdg, $".hypa-probe-{Guid.NewGuid():N}");
                Directory.CreateDirectory(probe);
                Directory.Delete(probe);
                return xdg;
            }
            catch
            {
                // fall through to temp
            }
        }

        return Path.GetTempPath();
    }

    private static void EnforceDirectoryMode0700(string dir)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var want = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(dir, want);
            var mode = File.GetUnixFileMode(dir);
            if ((mode & ~want) != 0 || (mode & want) != want)
            {
                throw new IOException(
                    $"Private handoff directory '{dir}' mode is {mode:G}; required 0700.");
            }

            if (!TryGetOwnerUid(dir, out var ownerUid) || ownerUid != geteuid())
            {
                throw new IOException(
                    $"Private handoff directory '{dir}' is not owned by the current euid.");
            }

            return;
        }

        throw new PlatformNotSupportedException("Unix directory mode is Unix-only.");
    }

    private static bool TryGetOwnerUid(string path, out uint uid)
        => TryGetStat(path, out _, out uid);

    private static uint GetPeerUidLinux(int fd)
    {
        // struct ucred { pid_t pid; uid_t uid; gid_t gid; }
        Span<int> ucred = stackalloc int[3];
        var len = ucred.Length * sizeof(int);
        if (getsockopt(fd, SOL_SOCKET_LINUX, SO_PEERCRED_LINUX, ref ucred[0], ref len) != 0)
        {
            var err = Marshal.GetLastPInvokeError();
            throw new Win32Exception(err, $"getsockopt(SO_PEERCRED) failed errno={err}");
        }

        return unchecked((uint)ucred[1]);
    }

    private static uint GetPeerUidMac(int fd)
    {
        uint euid = 0;
        uint egid = 0;
        if (getpeereid(fd, ref euid, ref egid) != 0)
        {
            var err = Marshal.GetLastPInvokeError();
            throw new Win32Exception(err, $"getpeereid failed errno={err}");
        }

        return euid;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int lstat(string path, byte* buf);

    [DllImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true)]
    private static extern unsafe int lstat_darwin_inode64(string path, byte* buf);

    [DllImport("libc", SetLastError = true)]
    private static extern int getsockopt(int sockfd, int level, int optname, ref int optval, ref int optlen);

    [DllImport("libc", SetLastError = true)]
    private static extern int getpeereid(int s, ref uint euid, ref uint egid);
}
