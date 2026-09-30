using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Hypa.AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// Fd-relative (Unix openat / Windows reparse-safe) workspace directory walk.
/// Closes intermediate-symlink TOCTOU where a real directory is pushed on a path stack,
/// then swapped to a symlink before path-based Enumerate/open follows host content
/// .
/// </summary>
internal static class NoFollowWorkspaceWalker
{
    internal enum EntryKind
    {
        Directory,
        RegularFile,
        SymlinkOrReparse,
        OtherOrUnavailable,
    }

    /// <summary>
    /// Result of walking one directory level: child names with kinds (symlink-nofollow).
    /// </summary>
    internal sealed record LevelEntry(string Name, EntryKind Kind);

    /// <summary>Device/inode identity for a project_root pin.</summary>
    internal readonly record struct FileIdentity(ulong Device, ulong Inode);

    /// <summary>
    /// Open <paramref name="path"/> as a real directory without following a final-component
    /// symlink/reparse. Caller owns the returned fd/handle and must dispose it.
    /// </summary>
    internal static bool TryOpenDirectory(string path, out SafeFileHandle handle, out string? detail)
    {
        // Never own fd 0 (IntPtr.Zero): SafeFileHandle may treat 0 as valid and
        // close stdin on Dispose/GC. Testhost then dies with EBADF / ResumeThread.
        handle = new SafeFileHandle(new IntPtr(-1), ownsHandle: true);
        detail = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            detail = "empty path";
            return false;
        }

        if (OperatingSystem.IsWindows())
            return TryOpenDirectoryWindows(path, out handle, out detail);

        return TryOpenDirectoryUnix(path, out handle, out detail);
    }

    /// <summary>
    /// Open child directory <paramref name="name"/> relative to an open parent directory fd
    /// without following symlinks (single path component — no intermediate follow).
    /// </summary>
    internal static bool TryOpenSubdirectory(
        SafeFileHandle parentDir,
        string name,
        out SafeFileHandle handle,
        out string? detail)
    {
        handle = new SafeFileHandle(new IntPtr(-1), ownsHandle: true);
        detail = null;
        if (!IsSafeSingleName(name))
        {
            detail = "unsafe name";
            return false;
        }

        if (OperatingSystem.IsWindows())
            return TryOpenSubdirectoryWindows(parentDir, name, out handle, out detail);

        return TryOpenSubdirectoryUnix(parentDir, name, out handle, out detail);
    }

    /// <summary>
    /// List single-component entry names under an open directory.
    /// Unix listing uses fdopendir on a dup of the held fd. Windows listing uses
    /// the handle path. Failures throw so the caller can mark transfer_incomplete.
    /// </summary>
    internal static IReadOnlyList<string> ListEntryNames(
        SafeFileHandle dirHandle,
        string pathForNames,
        Application.IWorkspaceWalkHooks? hooks = null)
    {
        if (hooks?.ThrowOnList == true)
            throw new IOException("test-forced list failure");

        if (OperatingSystem.IsWindows())
            return ListEntryNamesWindows(dirHandle, pathForNames);

        return ListEntryNamesUnix(dirHandle, pathForNames);
    }

    /// <summary>
    /// Classify a single-component child of an open directory without following links.
    /// </summary>
    internal static EntryKind ClassifyEntry(SafeFileHandle parentDir, string name, out string? detail) =>
        ClassifyEntry(parentDir, name, out _, out detail);

    /// <summary>
    /// Classify a single-component child and capture device/inode identity.
    /// Directory and regular-file results fail closed when identity is unreadable.
    /// </summary>
    internal static EntryKind ClassifyEntry(
        SafeFileHandle parentDir, string name, out FileIdentity identity, out string? detail)
    {
        identity = default;
        detail = null;
        if (!IsSafeSingleName(name))
        {
            detail = "unsafe name";
            return EntryKind.OtherOrUnavailable;
        }

        if (OperatingSystem.IsWindows())
            return ClassifyEntryWindows(parentDir, name, out identity, out detail);

        return ClassifyEntryUnix(parentDir, name, out identity, out detail);
    }

    /// <summary>
    /// Hold a <see cref="SafeFileHandle"/> across native fd use (EuidPeerAuthenticator pattern).
    /// </summary>
    internal static T WithUnixFd<T>(SafeFileHandle handle, Func<int, T> action)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(action);
        var addRef = false;
        try
        {
            handle.DangerousAddRef(ref addRef);
            var fd = handle.DangerousGetHandle().ToInt32();
            return action(fd);
        }
        finally
        {
            if (addRef)
                handle.DangerousRelease();
        }
    }

    /// <summary>
    /// Read a regular file as a single-component child of an open directory (O_NOFOLLOW).
    /// </summary>
    internal static NoFollowFileReader.OpenStatus TryReadFile(
        SafeFileHandle parentDir,
        string name,
        out byte[] bytes,
        out string? detail) =>
        TryReadFile(parentDir, name, long.MaxValue, CancellationToken.None, out bytes, out _, out detail);

    /// <summary>
    /// Read a regular file relative to <paramref name="parentDir"/> without following
    /// links. Stats the inode first; skips non-regular nodes and files larger than
    /// <paramref name="maxBytes"/> without materializing them. Honors <paramref name="ct"/>.
    /// </summary>
    internal static NoFollowFileReader.OpenStatus TryReadFile(
        SafeFileHandle parentDir,
        string name,
        long maxBytes,
        CancellationToken ct,
        out byte[] bytes,
        out long inodeSize,
        out string? detail) =>
        TryReadFile(
            parentDir, name, maxBytes, ct, expectedIdentity: null,
            out bytes, out inodeSize, out _, out detail);

    /// <summary>
    /// Read a regular file relative to <paramref name="parentDir"/> without following
    /// links. When <paramref name="expectedIdentity"/> is set, the opened inode must
    /// match or the read is refused without copying bytes.
    /// </summary>
    internal static NoFollowFileReader.OpenStatus TryReadFile(
        SafeFileHandle parentDir,
        string name,
        long maxBytes,
        CancellationToken ct,
        FileIdentity? expectedIdentity,
        out byte[] bytes,
        out long inodeSize,
        out FileIdentity identity,
        out string? detail)
    {
        bytes = Array.Empty<byte>();
        inodeSize = 0;
        identity = default;
        detail = null;
        if (!IsSafeSingleName(name))
        {
            detail = "unsafe name";
            return NoFollowFileReader.OpenStatus.Unavailable;
        }

        if (OperatingSystem.IsWindows())
        {
            return TryReadFileWindows(
                parentDir, name, maxBytes, ct, expectedIdentity,
                out bytes, out inodeSize, out identity, out detail);
        }

        return TryReadFileUnix(
            parentDir, name, maxBytes, ct, expectedIdentity,
            out bytes, out inodeSize, out identity, out detail);
    }

    /// <summary>
    /// Reject multi-component, empty, or dot-segment names so openat never receives
    /// intermediate path components that the kernel would follow.
    /// </summary>
    internal static bool IsSafeSingleName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        if (name is "." or "..")
            return false;
        if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
            return false;
        if (name.Contains('\0', StringComparison.Ordinal))
            return false;
        return true;
    }

    // ── Unix ────────────────────────────────────────────────────────────────

    private static bool TryOpenDirectoryUnix(string path, out SafeFileHandle handle, out string? detail)
    {
        handle = new SafeFileHandle(new IntPtr(-1), ownsHandle: true);
        detail = null;
        var flags = O_RDONLY | GetODirectory() | GetONoFollow() | GetOCloexec();
        var fd = open(path, flags);
        if (fd < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            if (err == GetEloop() || err == GetEloopAlt())
            {
                detail = "symlink (O_NOFOLLOW|O_DIRECTORY)";
                return false;
            }

            // Root may legitimately be opened without O_NOFOLLOW if the configured
            // project_root itself is a symlink the operator chose — retry without NOFOLLOW
            // only when the path is the walk root (caller decides). Here we stay strict.
            detail = "open dir errno=" + err;
            return false;
        }

        handle = new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
        return true;
    }

    /// <summary>
    /// lstat the configured root (no follow). Returns false when the path is missing
    /// or is a non-directory, non-symlink node.
    /// </summary>
    internal static bool TryClassifyWalkRoot(string path, out bool isSymlink, out string? detail)
    {
        isSymlink = false;
        detail = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            detail = "empty path";
            return false;
        }

        if (!TryLstatClassify(path, out isSymlink, out var isDirectory, out detail))
            return false;

        if (!isSymlink && !isDirectory)
        {
            detail = "not a directory";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Open walk root after classify. Non-symlink roots never follow (ELOOP/ENOTDIR
    /// skip). Operator-chosen symlink roots resolve once, then pin that directory fd.
    /// Descendants always use NOFOLLOW via openat.
    /// </summary>
    internal static bool TryOpenWalkRootAfterClassify(
        string path,
        bool isSymlinkAtClassify,
        out SafeFileHandle handle,
        out string? detail)
    {
        if (isSymlinkAtClassify)
            return TryOpenWalkRootFollowOnce(path, out handle, out detail);

        return TryOpenDirectory(path, out handle, out detail);
    }

    /// <summary>
    /// Open walk root: lstat first. Never follow a root that was a real directory
    /// (closes the O_NOFOLLOW-fail then open-without-NOFOLLOW TOCTOU). An operator
    /// project_root that is already a symlink is resolved once.
    /// </summary>
    internal static bool TryOpenWalkRoot(
        string path,
        out SafeFileHandle handle,
        out string? detail,
        Application.IWorkspaceWalkHooks? hooks = null) =>
        TryOpenWalkRoot(path, (ProjectRootPin?)null, out handle, out detail, hooks);

    /// <summary>
    /// Open walk root. A prepare pin that recorded a real directory never follows,
    /// even when the path is now a symlink. After open, the fd inode must match.
    /// </summary>
    internal static bool TryOpenWalkRoot(
        string path,
        ProjectRootPin? expected,
        out SafeFileHandle handle,
        out string? detail,
        Application.IWorkspaceWalkHooks? hooks = null)
    {
        handle = new SafeFileHandle(new IntPtr(-1), ownsHandle: true);
        if (!TryClassifyWalkRoot(path, out var isSymlink, out detail))
            return false;

        hooks?.AfterWalkRootLstat(path);

        // A root that was a real directory at prepare is never an operator symlink.
        if (expected is { WasSymlink: false } && isSymlink)
        {
            detail = "project_root was a real directory at prepare; now a symlink";
            return false;
        }

        // Never follow when unpinned. A later-created symlink is not an operator root.
        if (expected is null && isSymlink)
        {
            detail = "unpinned project_root is a symlink; refuse follow";
            return false;
        }

        var followOnce = expected?.WasSymlink == true;
        if (!TryOpenWalkRootAfterClassify(path, followOnce, out handle, out detail))
            return false;

        if (expected is { } pin)
        {
            if (!TryFstatIdentity(handle, out var now, out detail))
            {
                handle.Dispose();
                handle = new SafeFileHandle(new IntPtr(-1), ownsHandle: true);
                return false;
            }

            if (now.Device != pin.Identity.Device || now.Inode != pin.Identity.Inode)
            {
                detail = "project_root inode changed after prepare";
                handle.Dispose();
                handle = new SafeFileHandle(new IntPtr(-1), ownsHandle: true);
                return false;
            }
        }

        return true;
    }

    /// <summary>Prepare-time pin of project_root (target inode when the path is a symlink).</summary>
    internal static bool TryPinWalkRoot(
        string path,
        out ulong device,
        out ulong inode,
        out bool wasSymlink,
        out string? detail,
        Application.IWorkspaceWalkHooks? hooks = null)
    {
        device = 0;
        inode = 0;
        wasSymlink = false;
        if (!TryClassifyWalkRoot(path, out wasSymlink, out detail))
            return false;

        hooks?.AfterPinClassify(path);

        if (wasSymlink)
        {
            if (!TryResolveFinalDirectory(path, out var resolved, out detail))
                return false;
            if (!TryLstatIdentity(resolved, out var target, out var targetLink, out detail))
                return false;
            if (targetLink)
            {
                detail = "resolved project_root is still a symlink";
                return false;
            }

            device = target.Device;
            inode = target.Inode;
            return true;
        }

        if (!TryLstatIdentity(path, out var real, out var nowLink, out detail))
            return false;
        if (nowLink)
        {
            detail = "project_root became a symlink";
            return false;
        }

        device = real.Device;
        inode = real.Inode;
        return true;
    }

    internal readonly record struct ProjectRootPin(FileIdentity Identity, bool WasSymlink);

    /// <summary>
    /// lstat classify without following. Used by walk-root and nofollow file classify.
    /// </summary>
    internal static bool TryLstatClassify(
        string path, out bool isSymlink, out bool isDirectory, out string? detail)
    {
        isSymlink = false;
        isDirectory = false;
        detail = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            detail = "empty path";
            return false;
        }

        if (OperatingSystem.IsWindows())
            return TryLstatClassifyWindows(path, out isSymlink, out isDirectory, out detail);

        var statBuf = new byte[256];
        if (LstatForIdentity(path, statBuf) != 0)
        {
            detail = "lstat errno=" + Marshal.GetLastPInvokeError();
            return false;
        }

        var off = StatModeOffset();
        var mode = (ushort)(statBuf[off] | (statBuf[off + 1] << 8));
        isSymlink = (mode & S_Ifmt) == S_Iflnk;
        isDirectory = IsDirectoryMode(mode);
        return true;
    }

    internal static bool TryLstatIdentity(
        string path, out FileIdentity id, out bool isSymlink, out string? detail)
    {
        id = default;
        isSymlink = false;
        detail = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            detail = "empty path";
            return false;
        }

        if (OperatingSystem.IsWindows())
            return TryLstatIdentityWindows(path, out id, out isSymlink, out detail);

        return TryLstatIdentityUnix(path, out id, out isSymlink, out detail);
    }

    internal static bool TryFstatIdentity(SafeFileHandle handle, out FileIdentity id, out string? detail)
    {
        id = default;
        detail = null;
        if (OperatingSystem.IsWindows())
            return TryFstatIdentityWindows(handle, out id, out detail);

        return TryFstatIdentityUnix(handle, out id, out detail);
    }

    /// <summary>
    /// Kernel fd path for a held directory handle. Linux <c>/proc/self/fd/N</c> and
    /// macOS <c>/dev/fd/N</c> name the open inode, not a followable project_root string.
    /// </summary>
    internal static bool TryFormatKernelFdDirectoryPath(
        SafeFileHandle handle, out string path, out string? detail)
    {
        path = "";
        detail = null;
        var fd = WithUnixFd(handle, static h => h);
        if (fd < 0)
        {
            detail = "invalid fd";
            return false;
        }

        if (OperatingSystem.IsLinux())
        {
            path = "/proc/self/fd/" + fd;
            return true;
        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            path = "/dev/fd/" + fd;
            return true;
        }

        detail = "no kernel fd path on this OS";
        return false;
    }

    internal static bool IsKernelFdDirectoryPath(string path) =>
        TryParseKernelFdDirectoryPath(path, out _);

    /// <summary>
    /// Parse Linux <c>/proc/self/fd/N</c> or macOS/BSD <c>/dev/fd/N</c>.
    /// Rejects extra path components so the name cannot walk off the fd.
    /// </summary>
    internal static bool TryParseKernelFdDirectoryPath(string path, out int fd)
    {
        fd = -1;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string rest;
        if (path.StartsWith("/proc/self/fd/", StringComparison.Ordinal))
            rest = path["/proc/self/fd/".Length..];
        else if (path.StartsWith("/dev/fd/", StringComparison.Ordinal))
            rest = path["/dev/fd/".Length..];
        else
            return false;

        if (rest.Length == 0)
            return false;
        if (rest.Contains('/', StringComparison.Ordinal) || rest.Contains('\\', StringComparison.Ordinal))
            return false;
        if (!int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out fd))
            return false;
        return fd >= 0;
    }

    /// <summary>
    /// fstat the open descriptor named by a kernel fd path. Does not re-open
    /// <c>project_root</c> and does not follow a swapped vnode string.
    /// </summary>
    internal static bool TryFstatKernelFdDirectoryPath(
        string path, out FileIdentity id, out string? detail)
    {
        id = default;
        detail = null;
        if (!TryParseKernelFdDirectoryPath(path, out var fd))
        {
            detail = "not a kernel fd path";
            return false;
        }

        return TryFstatIdentityFromFd(fd, out id, out detail);
    }

    /// <summary>
    /// Resolve a real directory path for the open handle. The path must still be
    /// a non-symlink directory whose identity matches the handle (and pin).
    /// </summary>
    internal static bool TryGetVerifiedDirectoryPath(
        SafeFileHandle handle,
        string fallbackPath,
        ProjectRootPin? expected,
        out string path,
        out string? detail)
    {
        path = "";
        if (!TryFstatIdentity(handle, out var openedId, out detail))
            return false;

        if (expected is { } pin
            && (openedId.Device != pin.Identity.Device || openedId.Inode != pin.Identity.Inode))
        {
            detail = "open fd is not the prepared project_root inode";
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            if (!TryGetPathFromHandle(handle, out path, out detail))
                return false;
        }
        else if (WithUnixFd(handle, h => TryGetUnixFdPath(h, fallbackPath, out var vnodePath) ? vnodePath : null) is { } vnodePath)
        {
            path = vnodePath;
        }
        else if (!string.IsNullOrWhiteSpace(fallbackPath))
        {
            path = fallbackPath;
        }
        else
        {
            detail = "directory path unavailable";
            return false;
        }

        if (!TryLstatIdentity(path, out var now, out var isLink, out detail))
            return false;
        if (isLink)
        {
            detail = "directory path is a symlink";
            path = "";
            return false;
        }

        if (now.Device != openedId.Device || now.Inode != openedId.Inode)
        {
            detail = "directory path inode does not match open fd";
            path = "";
            return false;
        }

        return true;
    }

    private static bool TryOpenWalkRootFollowOnce(string path, out SafeFileHandle handle, out string? detail)
    {
        handle = new SafeFileHandle(new IntPtr(-1), ownsHandle: true);
        if (!TryResolveFinalDirectory(path, out var resolved, out detail))
            return false;

        // Open the resolved path with NOFOLLOW so a swap of that inode cannot follow again.
        return TryOpenDirectory(resolved, out handle, out detail);
    }

    internal static bool TryResolveFinalDirectory(string path, out string resolved, out string? detail)
    {
        resolved = "";
        detail = null;
        try
        {
            var target = Directory.ResolveLinkTarget(path, returnFinalTarget: true)
                ?? File.ResolveLinkTarget(path, returnFinalTarget: true);
            if (target is null)
            {
                detail = "symlink resolve failed";
                return false;
            }

            resolved = Path.GetFullPath(target.FullName);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            detail = "symlink resolve: " + ex.Message;
            return false;
        }
    }

    private static bool TryOpenSubdirectoryUnix(
        SafeFileHandle parentDir,
        string name,
        out SafeFileHandle handle,
        out string? detail)
    {
        handle = new SafeFileHandle(new IntPtr(-1), ownsHandle: true);
        detail = null;
        string? localDetail = null;
        var fd = WithUnixFd(parentDir, parentFd =>
        {
            if (parentFd < 0)
            {
                localDetail = "invalid parent fd";
                return -1;
            }

            var flags = O_RDONLY | GetODirectory() | GetONoFollow() | GetOCloexec();
            var opened = openat(parentFd, name, flags);
            if (opened < 0)
            {
                var err = Marshal.GetLastPInvokeError();
                if (err == GetEloop() || err == GetEloopAlt())
                    localDetail = "symlink (openat O_NOFOLLOW|O_DIRECTORY)";
                else
                    localDetail = "openat dir errno=" + err;
            }

            return opened;
        });
        if (fd < 0)
        {
            detail = localDetail ?? "invalid parent fd";
            return false;
        }

        handle = new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
        return true;
    }

    private static IReadOnlyList<string> ListEntryNamesUnix(SafeFileHandle dirHandle, string pathForNames)
    {
        return WithUnixFd(dirHandle, fd => ListEntryNamesUnixCore(dirHandle, fd, pathForNames));
    }

    private static IReadOnlyList<string> ListEntryNamesUnixCore(
        SafeFileHandle dirHandle, int fd, string pathForNames)
    {
        if (fd < 0)
            throw new IOException("invalid directory fd");

        if (!TryFstatIdentityUnix(dirHandle, out var openedId, out var statDetail))
            throw new IOException("fstat directory fd failed: " + (statDetail ?? "unknown"));

        // Prefer a vnode path that still names this fd's inode. A swapped walk
        // path string is rejected (symlink or inode mismatch) instead of followed.
        if (TryGetUnixFdPath(fd, pathForNames, out var vnodePath)
            && NoFollowWorkspaceWalker.TryLstatIdentity(vnodePath, out var now, out var isLink, out _)
            && !isLink
            && now.Device == openedId.Device
            && now.Inode == openedId.Inode)
        {
            try
            {
                return EnumerateSafeNames(vnodePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException("enumerate directory failed: " + ex.Message, ex);
            }
        }

        var fdPath = OperatingSystem.IsLinux()
            ? "/proc/self/fd/" + fd
            : "/dev/fd/" + fd;
        try
        {
            return EnumerateSafeNames(fdPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ListEntryNamesUnixFdopendir(fd, ex);
        }
    }

    private static bool TryGetUnixFdPath(int fd, string fallbackPath, out string path)
    {
        path = "";
        if (OperatingSystem.IsMacOS())
        {
            var buf = new byte[4096];
            if (FcntlGetPath(fd, buf) == 0)
            {
                var n = 0;
                while (n < buf.Length && buf[n] != 0)
                    n++;
                if (n > 0)
                {
                    path = System.Text.Encoding.UTF8.GetString(buf, 0, n);
                    return true;
                }
            }
        }

        if (OperatingSystem.IsLinux())
        {
            var proc = "/proc/self/fd/" + fd;
            if (TryReadLink(proc, out var linked) && !string.IsNullOrWhiteSpace(linked))
            {
                path = linked;
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(fallbackPath)
            && NoFollowWorkspaceWalker.TryLstatIdentity(fallbackPath, out _, out var link, out _)
            && !link)
        {
            path = fallbackPath;
            return true;
        }

        return false;
    }

    private static bool TryReadLink(string path, out string target)
    {
        target = "";
        var buf = new byte[4096];
        var n = readlink(path, buf, buf.Length);
        if (n <= 0)
            return false;
        target = System.Text.Encoding.UTF8.GetString(buf, 0, n);
        return true;
    }

    private static IReadOnlyList<string> EnumerateSafeNames(string directoryPath)
    {
        var names = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            var name = Path.GetFileName(entry);
            if (string.IsNullOrEmpty(name) || name is "." or "..")
                continue;
            if (!IsSafeSingleName(name))
                continue;
            names.Add(name);
        }

        return names;
    }

    private static IReadOnlyList<string> ListEntryNamesUnixFdopendir(int fd, Exception primary)
    {
        var dupFd = dup(fd);
        if (dupFd < 0)
        {
            throw new IOException(
                "enumerate directory fd failed: " + primary.Message
                + "; dup errno=" + Marshal.GetLastPInvokeError(),
                primary);
        }

        var dir = fdopendir(dupFd);
        if (dir == IntPtr.Zero)
        {
            var err = Marshal.GetLastPInvokeError();
            _ = close(dupFd);
            throw new IOException(
                "enumerate directory fd failed: " + primary.Message
                + "; fdopendir errno=" + err,
                primary);
        }

        try
        {
            var names = new List<string>();
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                var entry = readdir(dir);
                if (entry == IntPtr.Zero)
                {
                    var err = Marshal.GetLastPInvokeError();
                    if (err != 0)
                    {
                        throw new IOException(
                            "readdir failed errno=" + err + " after " + primary.Message,
                            primary);
                    }

                    break;
                }

                var name = ReadDirentName(entry);
                if (string.IsNullOrEmpty(name) || name is "." or "..")
                    continue;
                if (!IsSafeSingleName(name))
                    continue;
                names.Add(name);
            }

            return names;
        }
        finally
        {
            _ = closedir(dir);
        }
    }

    private static string? ReadDirentName(IntPtr entry)
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            var nameLen = Marshal.ReadInt16(entry, 18) & 0xFFFF;
            if (nameLen < 0 || nameLen > 1024)
                return Marshal.PtrToStringUTF8(IntPtr.Add(entry, 21));
            return Marshal.PtrToStringUTF8(IntPtr.Add(entry, 21), nameLen);
        }

        // Linux dirent64: d_name at offset 19, NUL-terminated.
        return Marshal.PtrToStringUTF8(IntPtr.Add(entry, 19));
    }

    private static bool TryLstatIdentityUnix(
        string path, out FileIdentity id, out bool isSymlink, out string? detail)
    {
        id = default;
        isSymlink = false;
        detail = null;
        var statBuf = new byte[256];
        if (LstatForIdentity(path, statBuf) != 0)
        {
            detail = "lstat errno=" + Marshal.GetLastPInvokeError();
            return false;
        }

        if (!TryReadIdentity(statBuf, out id))
        {
            detail = "stat identity unreadable";
            return false;
        }

        var off = StatModeOffset();
        var mode = (ushort)(statBuf[off] | (statBuf[off + 1] << 8));
        isSymlink = (mode & S_Ifmt) == S_Iflnk;
        return true;
    }

    private static bool TryFstatIdentityUnix(SafeFileHandle handle, out FileIdentity id, out string? detail)
    {
        id = default;
        detail = null;
        FileIdentity localId = default;
        string? localDetail = null;
        var ok = WithUnixFd(handle, fd => TryFstatIdentityFromFd(fd, out localId, out localDetail));
        id = localId;
        detail = localDetail;
        return ok;
    }

    private static bool TryFstatIdentityFromFd(int fd, out FileIdentity id, out string? detail)
    {
        id = default;
        detail = null;
        if (fd < 0)
        {
            detail = "invalid fd";
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            detail = "no kernel fd fstat on this OS";
            return false;
        }

        var statBuf = new byte[256];
        if (FstatForMode(fd, statBuf) != 0)
        {
            detail = "fstat errno=" + Marshal.GetLastPInvokeError();
            return false;
        }

        if (!TryReadIdentity(statBuf, out id))
        {
            detail = "stat identity unreadable";
            return false;
        }

        return true;
    }

    private static int LstatForIdentity(string path, byte[] statBuf)
    {
        if (OperatingSystem.IsMacOS()
            && RuntimeInformation.ProcessArchitecture is not Architecture.Arm64
                and not Architecture.Arm)
            return lstat_darwin_inode64(path, ref statBuf[0]);
        return lstat(path, ref statBuf[0]);
    }

    private static bool TryReadIdentity(byte[] statBuf, out FileIdentity id)
    {
        var devOff = StatDevOffset();
        var inoOff = StatInoOffset();
        ulong dev;
        if (StatDevSize() == 4)
        {
            dev = (uint)(statBuf[devOff]
                | (statBuf[devOff + 1] << 8)
                | (statBuf[devOff + 2] << 16)
                | (statBuf[devOff + 3] << 24));
        }
        else
        {
            dev = BitConverter.ToUInt64(statBuf, devOff);
        }

        var ino = BitConverter.ToUInt64(statBuf, inoOff);
        id = new FileIdentity(dev, ino);
        return true;
    }

    private static int StatDevOffset() => 0;

    private static int StatDevSize()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            return 4;
        return 8;
    }

    private static int StatInoOffset() => 8;

    private static EntryKind ClassifyEntryUnix(
        SafeFileHandle parentDir, string name, out FileIdentity identity, out string? detail)
    {
        identity = default;
        detail = null;
        FileIdentity localIdentity = default;
        string? localDetail = null;
        var kind = WithUnixFd(parentDir, parentFd =>
            ClassifyEntryUnixFromFd(parentFd, name, out localIdentity, out localDetail));
        identity = localIdentity;
        detail = localDetail;
        return kind;
    }

    private static EntryKind ClassifyEntryUnixFromFd(
        int parentFd, string name, out FileIdentity identity, out string? detail)
    {
        identity = default;
        detail = null;
        if (parentFd < 0)
        {
            detail = "invalid parent fd";
            return EntryKind.OtherOrUnavailable;
        }

        // Prefer directory open with O_NOFOLLOW|O_DIRECTORY (single component — no intermediate follow).
        var dflags = O_RDONLY | GetODirectory() | GetONoFollow() | GetOCloexec();
        var dfd = openat(parentFd, name, dflags);
        if (dfd >= 0)
        {
            try
            {
                if (!TryFstatIdentityFromFd(dfd, out identity, out detail))
                    return EntryKind.OtherOrUnavailable;
                return EntryKind.Directory;
            }
            finally
            {
                _ = close(dfd);
            }
        }

        var derr = Marshal.GetLastPInvokeError();
        if (derr == GetEloop() || derr == GetEloopAlt())
        {
            detail = "symlink";
            return EntryKind.SymlinkOrReparse;
        }

        // ENOTDIR (or equivalent) → not a directory; try nofollow + O_NONBLOCK open, then fstat.
        // Do not use path-based attributes (TOCTOU / intermediate follow).
        var fflags = O_RDONLY | GetONoFollow() | GetOCloexec() | GetONonblock();
        var ffd = openat(parentFd, name, fflags);
        if (ffd >= 0)
        {
            try
            {
                if (!TryGetFdModeAndIdentity(ffd, out var mode, out identity))
                {
                    detail = "fstat failed";
                    identity = default;
                    return EntryKind.OtherOrUnavailable;
                }

                if (IsRegularFileMode(mode))
                    return EntryKind.RegularFile;

                detail = "not a regular file";
                identity = default;
                return EntryKind.OtherOrUnavailable;
            }
            finally
            {
                _ = close(ffd);
            }
        }

        var ferr = Marshal.GetLastPInvokeError();
        if (ferr == GetEloop() || ferr == GetEloopAlt())
        {
            detail = "symlink";
            return EntryKind.SymlinkOrReparse;
        }

        detail = "openat errno=" + ferr;
        return EntryKind.OtherOrUnavailable;
    }

    private static NoFollowFileReader.OpenStatus TryReadFileUnix(
        SafeFileHandle parentDir,
        string name,
        long maxBytes,
        CancellationToken ct,
        FileIdentity? expectedIdentity,
        out byte[] bytes,
        out long inodeSize,
        out FileIdentity identity,
        out string? detail)
    {
        bytes = Array.Empty<byte>();
        inodeSize = 0;
        identity = default;
        detail = null;
        string? localDetail = null;
        var fd = WithUnixFd(parentDir, parentFd =>
        {
            if (parentFd < 0)
            {
                localDetail = "invalid parent fd";
                return -1;
            }

            var flags = O_RDONLY | GetONoFollow() | GetOCloexec() | GetONonblock();
            var opened = openat(parentFd, name, flags);
            if (opened < 0)
            {
                var err = Marshal.GetLastPInvokeError();
                if (err == GetEloop() || err == GetEloopAlt())
                    localDetail = "symlink (openat O_NOFOLLOW)";
                else
                    localDetail = "openat errno=" + err;
            }

            return opened;
        });
        if (fd < 0)
        {
            detail = localDetail ?? "invalid parent fd";
            if (localDetail is not null && localDetail.StartsWith("symlink", StringComparison.Ordinal))
                return NoFollowFileReader.OpenStatus.SymlinkOrReparse;
            return NoFollowFileReader.OpenStatus.Unavailable;
        }

        try
        {
            if (!TryGetFdModeAndIdentity(fd, out var mode, out identity))
            {
                detail = "fstat failed";
                identity = default;
                return NoFollowFileReader.OpenStatus.Unavailable;
            }

            if (IsDirectoryMode(mode))
            {
                detail = "directory";
                identity = default;
                return NoFollowFileReader.OpenStatus.Unavailable;
            }

            if (!IsRegularFileMode(mode))
            {
                detail = "not a regular file";
                identity = default;
                return NoFollowFileReader.OpenStatus.NotRegular;
            }

            if (expectedIdentity is FileIdentity expected && expected != identity)
            {
                detail = "inode changed after classify";
                return NoFollowFileReader.OpenStatus.IdentityMismatch;
            }

            using var handle = new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
            fd = -1;
            using var fs = new FileStream(handle, FileAccess.Read);
            try
            {
                inodeSize = fs.Length;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException)
            {
                detail = ex.Message;
                return NoFollowFileReader.OpenStatus.Unavailable;
            }

            return ReadCappedFromStream(fs, maxBytes, inodeSize, ct, out bytes, out detail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            detail = ex.Message;
            return NoFollowFileReader.OpenStatus.Unavailable;
        }
        finally
        {
            if (fd >= 0)
                _ = close(fd);
        }
    }

    private static NoFollowFileReader.OpenStatus ReadCappedFromStream(
        FileStream fs,
        long maxBytes,
        long inodeSize,
        CancellationToken ct,
        out byte[] bytes,
        out string? detail)
    {
        bytes = Array.Empty<byte>();
        detail = null;
        if (inodeSize < 0)
        {
            detail = "negative size";
            return NoFollowFileReader.OpenStatus.Unavailable;
        }

        if (inodeSize > maxBytes || inodeSize > int.MaxValue)
        {
            detail = "oversize " + inodeSize;
            return NoFollowFileReader.OpenStatus.Oversize;
        }

        if (inodeSize == 0)
            return NoFollowFileReader.OpenStatus.Ok;

        var cap = (int)Math.Min(maxBytes, int.MaxValue);
        using var ms = new MemoryStream((int)inodeSize);
        var buffer = new byte[Math.Min(64 * 1024, cap)];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int n;
            try
            {
                n = fs.Read(buffer, 0, buffer.Length);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                detail = ex.Message;
                bytes = Array.Empty<byte>();
                return NoFollowFileReader.OpenStatus.Unavailable;
            }

            if (n == 0)
                break;

            if (ms.Length + n > cap)
            {
                detail = "grew beyond cap";
                bytes = Array.Empty<byte>();
                return NoFollowFileReader.OpenStatus.Oversize;
            }

            ms.Write(buffer, 0, n);
        }

        bytes = ms.ToArray();
        return NoFollowFileReader.OpenStatus.Ok;
    }

    private static bool TryGetFdMode(int fd, out int mode) =>
        TryGetFdModeAndIdentity(fd, out mode, out _);

    private static bool TryGetFdModeAndIdentity(int fd, out int mode, out FileIdentity identity)
    {
        // Intel macOS fstat() is the 32-bit-inode struct (st_mode at 8). Reading
        // offset 4 there is st_ino low bits — (ino & S_IFMT)==S_IFDIR falsely
        // skips regular files and sets transfer_incomplete.
        // Prefer fstat$INODE64 (st_mode at 4), same as UnixPrivateSocketPath lstat.
        mode = 0;
        identity = default;
        var statBuf = new byte[256];
        if (FstatForMode(fd, statBuf) != 0)
            return false;

        var off = StatModeOffset();
        mode = (ushort)(statBuf[off] | (statBuf[off + 1] << 8));
        return TryReadIdentity(statBuf, out identity);
    }

    private static bool IsDirectoryMode(int mode) => (mode & S_Ifmt) == S_Ifdir;

    private static bool IsRegularFileMode(int mode) => (mode & S_Ifmt) == S_Ifreg;

    private static int FstatForMode(int fd, byte[] statBuf)
    {
        // x86_64 Darwin: fstat$INODE64. arm64 Darwin fstat is already INODE64.
        if (OperatingSystem.IsMacOS()
            && RuntimeInformation.ProcessArchitecture is not Architecture.Arm64
                and not Architecture.Arm)
            return fstat_darwin_inode64(fd, ref statBuf[0]);
        return fstat(fd, ref statBuf[0]);
    }

    private static int StatModeOffset()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            return 4;
        if (OperatingSystem.IsLinux()
            && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return 16;
        return 24;
    }

    private static int GetONoFollow()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            return 0x0100;
        return LinuxOpenFlags.NoFollow;
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

    private static int GetONonblock()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            return 0x0004;
        return 0x800; // Linux
    }

    private static int GetEloop() =>
        OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD() ? 62 : 40;

    private static int GetEloopAlt() => 40;

    private const int O_RDONLY = 0;
    private const int S_Ifmt = 0xF000;
    private const int S_Ifdir = 0x4000;
    private const int S_Ifreg = 0x8000;
    private const int S_Iflnk = 0xA000;

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int openat(int dirfd, string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int readlink(string pathname, byte[] buf, int bufsiz);

    [DllImport("libc", SetLastError = true)]
    private static extern int fstat(int fd, ref byte buf);

    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int fstat_darwin_inode64(int fd, ref byte buf);

    [DllImport("libc", SetLastError = true)]
    private static extern int lstat(string pathname, ref byte buf);

    [DllImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true)]
    private static extern int lstat_darwin_inode64(string pathname, ref byte buf);

    [DllImport("libc", SetLastError = true)]
    private static extern int dup(int oldfd);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr fdopendir(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr readdir(IntPtr dirp);

    [DllImport("libc", SetLastError = true)]
    private static extern int closedir(IntPtr dirp);

    private const int F_GetPath = 50;

    // Darwin arm64: fcntl is variadic; Apple passes variadic args on the stack.
    // Six pads exhaust x2-x7 so the path buffer pointer lands at [sp+0].
    // Same sequence as UnixRawTerminal.Fcntl.
    private static bool DarwinStackVariadic =>
        OperatingSystem.IsMacOS()
        && RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm;

    private static int FcntlGetPath(int fd, byte[] buf)
    {
        if (DarwinStackVariadic)
        {
            var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                return fcntl_darwin_variadic(
                    fd, F_GetPath, 0, 0, 0, 0, 0, 0, handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }

        return fcntl_getpath(fd, F_GetPath, buf);
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "fcntl")]
    private static extern int fcntl_getpath(int fd, int cmd, byte[] buf);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int fcntl_darwin_variadic(
        int fd,
        int cmd,
        nint pad2, nint pad3, nint pad4, nint pad5, nint pad6, nint pad7,
        nint arg);

    // ── Windows ─────────────────────────────────────────────────────────────

    private static bool TryOpenDirectoryWindows(string path, out SafeFileHandle handle, out string? detail)
    {
        handle = new SafeFileHandle(new IntPtr(-1), ownsHandle: true);
        detail = null;
        try
        {
            var h = CreateFileW(
                path,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero);

            if (h.IsInvalid)
            {
                detail = "CreateFile dir err=" + Marshal.GetLastPInvokeError();
                h.Dispose();
                return false;
            }

            if (!GetFileInformationByHandle(h, out var info))
            {
                detail = "GetFileInformationByHandle failed";
                h.Dispose();
                return false;
            }

            if ((info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
            {
                detail = "reparse point";
                h.Dispose();
                return false;
            }

            if ((info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
            {
                detail = "not a directory";
                h.Dispose();
                return false;
            }

            handle = h;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            detail = ex.Message;
            return false;
        }
    }

    private static bool TryOpenSubdirectoryWindows(
        SafeFileHandle parentDir,
        string name,
        out SafeFileHandle handle,
        out string? detail)
    {
        // Win32 lacks openat; re-open via GetFinalPathNameByHandle + child name with
        // OPEN_REPARSE_POINT so a swapped reparse final component is rejected.
        handle = new SafeFileHandle(new IntPtr(-1), ownsHandle: true);
        detail = null;
        if (!TryGetPathFromHandle(parentDir, out var parentPath, out detail))
            return false;

        var childPath = Path.Combine(parentPath, name);
        return TryOpenDirectoryWindows(childPath, out handle, out detail);
    }

    private static IReadOnlyList<string> ListEntryNamesWindows(SafeFileHandle dirHandle, string pathForNames)
    {
        if (!TryGetPathFromHandle(dirHandle, out var path, out var detail))
        {
            if (!string.IsNullOrWhiteSpace(pathForNames))
                path = pathForNames;
            else
                throw new IOException(detail ?? "directory handle path unavailable");
        }

        try
        {
            var names = new List<string>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                var name = Path.GetFileName(entry);
                if (string.IsNullOrEmpty(name) || name is "." or "..")
                    continue;
                if (!IsSafeSingleName(name))
                    continue;
                names.Add(name);
            }

            return names;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("enumerate directory failed: " + ex.Message, ex);
        }
    }

    private static bool TryLstatClassifyWindows(
        string path, out bool isSymlink, out bool isDirectory, out string? detail)
    {
        isSymlink = false;
        isDirectory = false;
        if (!TryLstatIdentityWindows(path, out _, out isSymlink, out detail))
            return false;

        try
        {
            var h = CreateFileW(
                path,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero);
            if (h.IsInvalid)
            {
                detail = "CreateFile classify err=" + Marshal.GetLastPInvokeError();
                h.Dispose();
                return false;
            }

            using (h)
            {
                if (!GetFileInformationByHandle(h, out var info))
                {
                    detail = "GetFileInformationByHandle failed";
                    return false;
                }

                isDirectory = (info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            detail = ex.Message;
            return false;
        }
    }

    private static bool TryLstatIdentityWindows(
        string path, out FileIdentity id, out bool isSymlink, out string? detail)
    {
        id = default;
        isSymlink = false;
        detail = null;
        try
        {
            var h = CreateFileW(
                path,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero);
            if (h.IsInvalid)
            {
                detail = "CreateFile lstat err=" + Marshal.GetLastPInvokeError();
                h.Dispose();
                return false;
            }

            using (h)
            {
                if (!GetFileInformationByHandle(h, out var info))
                {
                    detail = "GetFileInformationByHandle failed";
                    return false;
                }

                isSymlink = (info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
                id = ToIdentity(info);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            detail = ex.Message;
            return false;
        }
    }

    private static bool TryFstatIdentityWindows(SafeFileHandle handle, out FileIdentity id, out string? detail)
    {
        id = default;
        detail = null;
        if (!GetFileInformationByHandle(handle, out var info))
        {
            detail = "GetFileInformationByHandle failed";
            return false;
        }

        id = ToIdentity(info);
        return true;
    }

    private static FileIdentity ToIdentity(ByHandleFileInformation info) =>
        new(
            info.dwVolumeSerialNumber,
            ((ulong)info.nFileIndexHigh << 32) | info.nFileIndexLow);

    private static EntryKind ClassifyEntryWindows(
        SafeFileHandle parentDir, string name, out FileIdentity identity, out string? detail)
    {
        identity = default;
        detail = null;
        if (!TryGetPathFromHandle(parentDir, out var parentPath, out detail))
            return EntryKind.OtherOrUnavailable;

        var childPath = Path.Combine(parentPath, name);
        try
        {
            // OPEN_REPARSE_POINT open classifies without following final reparse.
            var h = CreateFileW(
                childPath,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero);

            if (h.IsInvalid)
            {
                detail = "CreateFile err=" + Marshal.GetLastPInvokeError();
                h.Dispose();
                return EntryKind.OtherOrUnavailable;
            }

            using (h)
            {
                if (!GetFileInformationByHandle(h, out var info))
                {
                    detail = "GetFileInformationByHandle failed";
                    return EntryKind.OtherOrUnavailable;
                }

                if ((info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
                {
                    detail = "reparse point";
                    return EntryKind.SymlinkOrReparse;
                }

                identity = ToIdentity(info);
                if ((info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                    return EntryKind.Directory;

                if (GetFileType(h) != FILE_TYPE_DISK)
                {
                    detail = "not a regular file";
                    identity = default;
                    return EntryKind.OtherOrUnavailable;
                }

                return EntryKind.RegularFile;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            detail = ex.Message;
            return EntryKind.OtherOrUnavailable;
        }
    }

    private static NoFollowFileReader.OpenStatus TryReadFileWindows(
        SafeFileHandle parentDir,
        string name,
        long maxBytes,
        CancellationToken ct,
        FileIdentity? expectedIdentity,
        out byte[] bytes,
        out long inodeSize,
        out FileIdentity identity,
        out string? detail)
    {
        bytes = Array.Empty<byte>();
        inodeSize = 0;
        identity = default;
        detail = null;
        if (!TryGetPathFromHandle(parentDir, out var parentPath, out detail))
            return NoFollowFileReader.OpenStatus.Unavailable;

        var childPath = Path.Combine(parentPath, name);
        try
        {
            var h = CreateFileW(
                childPath,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero);

            if (h.IsInvalid)
            {
                detail = "CreateFile err=" + Marshal.GetLastPInvokeError();
                h.Dispose();
                return NoFollowFileReader.OpenStatus.Unavailable;
            }

            using (h)
            {
                if (!GetFileInformationByHandle(h, out var info))
                {
                    detail = "GetFileInformationByHandle failed";
                    return NoFollowFileReader.OpenStatus.Unavailable;
                }

                if ((info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
                {
                    detail = "reparse point";
                    return NoFollowFileReader.OpenStatus.SymlinkOrReparse;
                }

                if ((info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                {
                    detail = "directory";
                    return NoFollowFileReader.OpenStatus.Unavailable;
                }

                if (GetFileType(h) != FILE_TYPE_DISK)
                {
                    detail = "not a regular file";
                    return NoFollowFileReader.OpenStatus.NotRegular;
                }

                identity = ToIdentity(info);
                if (expectedIdentity is FileIdentity expected && expected != identity)
                {
                    detail = "inode changed after classify";
                    return NoFollowFileReader.OpenStatus.IdentityMismatch;
                }

                inodeSize = ((long)info.nFileSizeHigh << 32) | info.nFileSizeLow;
                using var fs = new FileStream(h, FileAccess.Read);
                return ReadCappedFromStream(fs, maxBytes, inodeSize, ct, out bytes, out detail);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            detail = ex.Message;
            return NoFollowFileReader.OpenStatus.Unavailable;
        }
    }

    private static bool TryGetPathFromHandle(SafeFileHandle handle, out string path, out string? detail)
    {
        path = "";
        detail = null;
        var buf = new char[1024];
        var len = GetFinalPathNameByHandleW(handle, buf, (uint)buf.Length, FILE_NAME_NORMALIZED);
        if (len == 0 || len >= buf.Length)
        {
            detail = "GetFinalPathNameByHandle err=" + Marshal.GetLastPInvokeError();
            return false;
        }

        path = new string(buf, 0, (int)len);
        // Strip \\?\ prefix for managed Path APIs.
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path[4..];
        return true;
    }

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_NAME_NORMALIZED = 0;
    private const uint FILE_TYPE_DISK = 1;

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle hFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile,
        [Out] char[] lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

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
