using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// Validates app-local MsQuic files before the probe treats them as present.
/// Install-directory write access can replace both the library and digest sidecar.
/// This spike does not verify an OS code-signing catalog.
/// </summary>
public static class MsQuicNativeIntegrity
{
    public static bool TryValidateAppLocal(string libraryPath, out string? failureDetail)
    {
        failureDetail = null;
        try
        {
            if (string.IsNullOrWhiteSpace(libraryPath))
            {
                failureDetail = "library path is required";
                return false;
            }

            var baseDir = Path.GetDirectoryName(libraryPath);
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                failureDetail = "application directory is required";
                return false;
            }

            if (!TryValidateInstallDirectory(baseDir, out failureDetail))
                return false;

            var info = new FileInfo(libraryPath);
            if (!TryValidateRegularFile(info, "library", out failureDetail))
                return false;

            if (!TryValidateFilePermissions(libraryPath, out failureDetail))
                return false;

            var sidecarPath = libraryPath + ".sha256";
            var sidecarInfo = new FileInfo(sidecarPath);
            if (!sidecarInfo.Exists)
            {
                failureDetail = "library digest sidecar is missing";
                return false;
            }

            if (!TryValidateRegularFile(sidecarInfo, "digest sidecar", out failureDetail))
                return false;

            if (!TryValidateFilePermissions(sidecarPath, out failureDetail))
                return false;

            var expectedDigest = File.ReadAllText(sidecarPath).Trim();
            if (expectedDigest.Length == 0)
            {
                failureDetail = "library digest sidecar is empty";
                return false;
            }

            byte[] libraryBytes;
            using (var stream = File.OpenRead(libraryPath))
                libraryBytes = SHA256.HashData(stream);

            var actualDigest = Convert.ToHexString(libraryBytes);
            if (!string.Equals(expectedDigest, actualDigest, StringComparison.OrdinalIgnoreCase))
            {
                failureDetail = "library digest does not match the publish sidecar";
                return false;
            }

            return true;
        }
        catch (IOException)
        {
            failureDetail = "native library integrity check failed";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            failureDetail = "native library integrity check failed";
            return false;
        }
        catch (CryptographicException)
        {
            failureDetail = "native library integrity check failed";
            return false;
        }
    }

    internal static bool TryValidateInstallDirectory(string baseDir, out string? failureDetail)
    {
        failureDetail = null;
        try
        {
            if (!Directory.Exists(baseDir))
            {
                failureDetail = "application directory is missing";
                return false;
            }

            foreach (var segment in EnumerateIntegrityDirectoryChain(baseDir))
            {
                var isInstallDirectory = string.Equals(
                    Path.GetFullPath(segment),
                    Path.GetFullPath(baseDir),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

                // Stop before a sticky world-writable ancestor such as /tmp.
                // On macOS /tmp is a symlink to /private/tmp; do not fail that link.
                if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                    && !isInstallDirectory
                    && IsUnixStickyWorldWritable(segment))
                    break;

                if (!TryValidateDirectoryNotLinked(segment, out failureDetail))
                    return false;

                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    // A root-owned system install is trusted like one owned
                    // by the current user. No other user may write to it.
                    if (!TryValidateUnixPathPermissions(
                            segment,
                            checkExtendedAcl: true,
                            out failureDetail))
                        return false;
                }
                else if (OperatingSystem.IsWindows())
                {
                    if (!TryValidateWindowsPathPermissions(
                            segment,
                            isDirectory: true,
                            requireTrustedOwner: true,
                            out failureDetail))
                        return false;
                }
            }

            return true;
        }
        catch (IOException)
        {
            failureDetail = "application directory access could not be verified";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            failureDetail = "application directory access could not be verified";
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            failureDetail = "application directory access could not be verified";
            return false;
        }
    }

    internal static bool TryValidateFilePermissions(string path, out string? failureDetail)
    {
        failureDetail = null;
        try
        {
            if (!File.Exists(path))
            {
                failureDetail = "file is missing";
                return false;
            }

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                return TryValidateUnixPathPermissions(
                    path,
                    checkExtendedAcl: true,
                    out failureDetail);

            if (OperatingSystem.IsWindows())
                return TryValidateWindowsPathPermissions(
                    path,
                    isDirectory: false,
                    requireTrustedOwner: true,
                    out failureDetail);

            return true;
        }
        catch (IOException)
        {
            failureDetail = "file access could not be verified";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            failureDetail = "file access could not be verified";
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            failureDetail = "file access could not be verified";
            return false;
        }
    }

    /// <summary>
    /// Yields logical directory segments to check for symlinks, reparse points, and
    /// ownership or ACL tamper rights. Always includes the install directory. When
    /// it lies under the publish root, also walks parents up to the volume root so
    /// a redirected ancestor cannot satisfy integrity checks on a swapped tree.
    /// </summary>
    private static IEnumerable<string> EnumerateIntegrityDirectoryChain(string baseDir)
    {
        var publishRoot = Path.GetFullPath(AppContext.BaseDirectory);
        var fullBase = Path.GetFullPath(baseDir);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        yield return baseDir;

        if (!IsSameOrDescendant(fullBase, publishRoot, comparison))
            yield break;

        var current = baseDir;
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        while (!string.IsNullOrEmpty(current))
        {
            if (IsVolumeRoot(current))
                yield break;

            var parentPath = GetParentDirectorySegment(current);
            if (string.IsNullOrEmpty(parentPath))
                yield break;

            if (!seen.Add(parentPath))
                yield break;

            yield return parentPath;
            current = parentPath;
        }
    }

    private static string? GetParentDirectorySegment(string path)
    {
        if (IsVolumeRoot(path))
            return null;

        var normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(normalized))
            return null;

        var parent = Directory.GetParent(normalized);
        return parent?.FullName;
    }

    private static bool IsVolumeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (!OperatingSystem.IsWindows() && path == "/")
            return true;

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!OperatingSystem.IsWindows() && trimmed == "/")
            return true;

        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var rest = trimmed.AsSpan(2);
            var slash = rest.IndexOf('\\');
            if (slash < 0)
                return false;

            var tail = rest[(slash + 1)..];
            return !tail.IsEmpty && !tail.Contains('\\');
        }

        if (OperatingSystem.IsWindows())
        {
            if (trimmed.Length == 2 && trimmed[1] == ':')
                return true;

            return trimmed.Length == 3 && trimmed[1] == ':' && trimmed[2] == '\\';
        }

        return trimmed == "/";
    }

    private static bool IsSameOrDescendant(string path, string ancestor, StringComparison comparison)
    {
        if (string.Equals(path, ancestor, comparison))
            return true;

        var prefix = ancestor.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison);
    }

    private static bool TryValidateDirectoryNotLinked(string path, out string? failureDetail)
    {
        failureDetail = null;
        var info = new DirectoryInfo(path);
        if (!info.Exists)
        {
            failureDetail = "application directory path is missing";
            return false;
        }

        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
        {
            failureDetail = "application directory path must not contain a symlink or reparse point";
            return false;
        }

        return true;
    }

    private static bool TryValidateRegularFile(FileInfo info, string label, out string? failureDetail)
    {
        failureDetail = null;
        if (!info.Exists)
        {
            failureDetail = $"{label} file is missing";
            return false;
        }

        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
        {
            failureDetail = $"{label} must be a regular file beside the app";
            return false;
        }

        return true;
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal static bool IsUnixStickyWorldWritable(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;

        var modePath = path;
        try
        {
            var info = new DirectoryInfo(path);
            if (info.LinkTarget is not null)
            {
                var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
                if (resolved is not null)
                    modePath = resolved.FullName;
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        var mode = File.GetUnixFileMode(modePath);
        return (mode & UnixFileMode.StickyBit) != 0
            && (mode & UnixFileMode.OtherWrite) != 0;
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static bool TryValidateUnixPathPermissions(
        string path,
        bool checkExtendedAcl,
        out string? failureDetail)
    {
        failureDetail = null;
        if (!TryValidateUnixTrustedOwner(path, out failureDetail))
            return false;

        if (checkExtendedAcl && !TryValidateUnixNoExtendedAcl(path, out failureDetail))
            return false;

        var mode = File.GetUnixFileMode(path);
        var groupWriteDenied = (mode & UnixFileMode.GroupWrite) != 0
            && !IsGroupPrivateToOwner(path);
        if ((mode & UnixFileMode.OtherWrite) != 0 || groupWriteDenied)
        {
            failureDetail = Directory.Exists(path)
                ? "application directory is writable by group or others"
                : "native library file is writable by group or others";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Group write is safe when the group holds only the owner (umask 002
    /// installs such as Linuxbrew). macOS groups live in Directory Services,
    /// so it keeps refusing group write there.
    /// </summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static bool IsGroupPrivateToOwner(string path) =>
        OperatingSystem.IsLinux()
        && TryGetUnixPathOwner(path, out var ownerUid, out var groupGid)
        && UnixPrivateGroup.IsPrivateTo(groupGid, ownerUid);

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static bool TryValidateUnixTrustedOwner(string path, out string? failureDetail)
    {
        failureDetail = null;
        if (!TryGetUnixPathOwner(path, out var ownerUid, out _))
        {
            failureDetail = Directory.Exists(path)
                ? "application directory owner could not be verified"
                : "native library file owner could not be verified";
            return false;
        }

        if (ownerUid != 0 && ownerUid != GetEffectiveUserId())
        {
            failureDetail = Directory.Exists(path)
                ? "application directory owner is not trusted"
                : "native library file owner is not trusted";
            return false;
        }

        return true;
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static bool TryValidateUnixNoExtendedAcl(string path, out string? failureDetail)
    {
        failureDetail = null;
        if (OperatingSystem.IsLinux())
            return TryValidateLinuxNoPosixAcl(path, out failureDetail);

        if (OperatingSystem.IsMacOS())
            return TryValidateMacNoExtendedAcl(path, out failureDetail);

        return true;
    }

    [SupportedOSPlatform("linux")]
    private static bool TryValidateLinuxNoPosixAcl(string path, out string? failureDetail)
    {
        failureDetail = null;
        if (!TryProbeLinuxExtendedAttribute(path, PosixAclAccessAttribute, out var accessProbe, out failureDetail))
            return false;

        if (accessProbe == LinuxExtendedAttributeProbe.Present)
        {
            failureDetail = Directory.Exists(path)
                ? "application directory has an extended ACL"
                : "native library file has an extended ACL";
            return false;
        }

        if (!Directory.Exists(path))
            return true;

        if (!TryProbeLinuxExtendedAttribute(path, PosixAclDefaultAttribute, out var defaultProbe, out failureDetail))
            return false;

        if (defaultProbe == LinuxExtendedAttributeProbe.Present)
        {
            failureDetail = "application directory has an extended ACL";
            return false;
        }

        return true;
    }

    private enum LinuxExtendedAttributeProbe
    {
        Absent,
        Present,
    }

    [SupportedOSPlatform("linux")]
    private static bool TryProbeLinuxExtendedAttribute(
        string path,
        string attributeName,
        out LinuxExtendedAttributeProbe probe,
        out string? failureDetail)
    {
        probe = LinuxExtendedAttributeProbe.Absent;
        failureDetail = null;

        var length = getxattr(path, attributeName, Array.Empty<byte>(), 0);
        if (length >= 0)
        {
            probe = LinuxExtendedAttributeProbe.Present;
            return true;
        }

        var errno = GetLinuxErrno();
        if (IsLinuxAbsentExtendedAttributeErrno(errno))
            return true;

        failureDetail = Directory.Exists(path)
            ? "application directory extended ACL could not be verified"
            : "native library file extended ACL could not be verified";
        return false;
    }

    [SupportedOSPlatform("macos")]
    private static bool TryValidateMacNoExtendedAcl(string path, out string? failureDetail)
    {
        failureDetail = null;
        var acl = acl_get_file(path, AclTypeExtended);
        if (acl == IntPtr.Zero)
        {
            var errno = GetMacErrno();
            if (IsMacAbsentExtendedAclErrno(errno))
                return true;

            failureDetail = Directory.Exists(path)
                ? "application directory extended ACL could not be verified"
                : "native library file extended ACL could not be verified";
            return false;
        }

        try
        {
            if (MacAclGrantsExtendedWrite(acl))
            {
                failureDetail = Directory.Exists(path)
                    ? "application directory has an extended ACL"
                    : "native library file has an extended ACL";
                return false;
            }

            return true;
        }
        finally
        {
            acl_free(acl);
        }
    }

    [SupportedOSPlatform("macos")]
    private static bool MacAclGrantsExtendedWrite(IntPtr acl)
    {
        IntPtr entry = IntPtr.Zero;
        var entryId = AclFirstEntry;
        while (acl_get_entry(acl, entryId, ref entry) == 0)
        {
            entryId = AclNextEntry;
            if (acl_get_tag_type(entry, out var tag) != 0 || tag != AclExtendedAllowTag)
                continue;

            if (acl_get_permset(entry, out var permset) != 0)
                continue;

            if (MacPermsetGrantsWrite(permset))
                return true;
        }

        return false;
    }

    [SupportedOSPlatform("macos")]
    private static bool MacPermsetGrantsWrite(IntPtr permset)
    {
        ReadOnlySpan<int> writePerms =
        [
            AclPermWriteData,
            AclPermAppendData,
            AclPermAddSubdirectory,
            AclPermDelete,
            AclPermDeleteChild,
            AclPermWriteAttributes,
            AclPermWriteExtattributes,
            AclPermWriteSecurity,
            AclPermChangeOwner,
        ];

        foreach (var perm in writePerms)
        {
            if (acl_get_perm_np(permset, perm) == 1)
                return true;
        }

        return false;
    }

    [SupportedOSPlatform("macos")]
    private static int GetMacErrno() => Marshal.ReadInt32(__error());

    [SupportedOSPlatform("linux")]
    private static int GetLinuxErrno() => Marshal.ReadInt32(__errno_location());

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static bool TryGetUnixPathOwner(string path, out uint ownerUid, out uint groupGid)
    {
        ownerUid = 0;
        groupGid = 0;
        var buffer = new byte[512];

        var rc = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm
            ? lstat(path, buffer)
            : OperatingSystem.IsMacOS()
                ? lstat_darwin_inode64(path, buffer)
                : lstat(path, buffer);
        if (rc != 0)
            return false;

        var uidOffset = OperatingSystem.IsMacOS()
            ? DarwinStatUidOffset
            : RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? LinuxArm64StatUidOffset
                : LinuxLp64StatUidOffset;
        ownerUid = BitConverter.ToUInt32(buffer, uidOffset);
        // st_gid follows st_uid in every supported stat layout.
        groupGid = BitConverter.ToUInt32(buffer, uidOffset + sizeof(uint));
        return true;
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static uint GetEffectiveUserId() => geteuid();

    private const int DarwinStatUidOffset = 16;
    private const int LinuxArm64StatUidOffset = 24;
    private const int LinuxLp64StatUidOffset = 28;
    private const int AclTypeExtended = 0x00000100;
    private const int AclFirstEntry = 0;
    private const int AclNextEntry = -1;
    private const int AclExtendedAllowTag = 1;
    private const int AclPermWriteData = 1 << 2;
    private const int AclPermAppendData = 1 << 3;
    private const int AclPermDelete = 1 << 4;
    private const int AclPermDeleteChild = 1 << 6;
    private const int AclPermWriteAttributes = 1 << 8;
    private const int AclPermWriteExtattributes = 1 << 10;
    private const int AclPermWriteSecurity = 1 << 12;
    private const int AclPermChangeOwner = 1 << 13;
    private const int AclPermAddSubdirectory = AclPermAppendData;
    private const int ErrNoEnt = 2;
    private const int ErrNoData = 61;
    private const string PosixAclAccessAttribute = "system.posix_acl_access";

    internal static bool IsLinuxAbsentExtendedAttributeErrno(int errno) => errno == ErrNoData;

    internal static bool IsMacAbsentExtendedAclErrno(int errno) => errno == 0 || errno == ErrNoEnt;
    private const string PosixAclDefaultAttribute = "system.posix_acl_default";

    [SupportedOSPlatform("macos")]
    [DllImport("libc")]
    private static extern IntPtr __error();

    [SupportedOSPlatform("linux")]
    [DllImport("libc")]
    private static extern IntPtr __errno_location();

    [SupportedOSPlatform("linux")]
    [DllImport("libc", SetLastError = true)]
    private static extern nint getxattr(string path, string name, byte[] value, nuint size);

    [SupportedOSPlatform("macos")]
    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr acl_get_file(string path, int aclType);

    [SupportedOSPlatform("macos")]
    [DllImport("libc", SetLastError = true)]
    private static extern int acl_free(IntPtr acl);

    [SupportedOSPlatform("macos")]
    [DllImport("libc", SetLastError = true)]
    private static extern int acl_get_entry(IntPtr acl, int entryId, ref IntPtr entry);

    [SupportedOSPlatform("macos")]
    [DllImport("libc", SetLastError = true)]
    private static extern int acl_get_tag_type(IntPtr entry, out int tagType);

    [SupportedOSPlatform("macos")]
    [DllImport("libc", SetLastError = true)]
    private static extern int acl_get_permset(IntPtr entry, out IntPtr permset);

    [SupportedOSPlatform("macos")]
    [DllImport("libc", SetLastError = true)]
    private static extern int acl_get_perm_np(IntPtr permset, int perm);

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [DllImport("libc", SetLastError = true)]
    private static extern int lstat(string path, byte[] buf);

    [SupportedOSPlatform("macos")]
    [DllImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true)]
    private static extern int lstat_darwin_inode64(string path, byte[] buf);

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    [SupportedOSPlatform("windows")]
    private static bool TryValidateWindowsPathPermissions(
        string path,
        bool isDirectory,
        bool requireTrustedOwner,
        out string? failureDetail)
    {
        failureDetail = null;
        FileSystemSecurity security = isDirectory
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();

        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner)
        {
            failureDetail = isDirectory
                ? "application directory owner could not be verified"
                : "native library file owner could not be verified";
            return false;
        }

        if (requireTrustedOwner && !IsTrustedOwner(owner))
        {
            failureDetail = isDirectory
                ? "application directory owner is not trusted"
                : "native library file owner is not trusted";
            return false;
        }

        const FileSystemRights dangerousRightsMask =
            FileSystemRights.FullControl
            | FileSystemRights.Modify
            | FileSystemRights.Write
            | FileSystemRights.Delete
            | FileSystemRights.WriteData
            | FileSystemRights.AppendData
            | FileSystemRights.ChangePermissions
            | FileSystemRights.TakeOwnership
            | FileSystemRights.WriteAttributes
            | FileSystemRights.WriteExtendedAttributes;

        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow)
                continue;

            if ((rule.FileSystemRights & dangerousRightsMask) == 0)
                continue;

            if (rule.IdentityReference is not SecurityIdentifier sid)
            {
                failureDetail = isDirectory
                    ? "application directory has an unresolved access rule"
                    : "native library file has an unresolved access rule";
                return false;
            }

            if (IsBroadWritePrincipal(sid))
            {
                failureDetail = isDirectory
                    ? "application directory is writable by users other than the owner"
                    : "native library file is writable by users other than the owner";
                return false;
            }

            if (!sid.Equals(owner) && !IsTrustedSystemPrincipal(sid))
            {
                failureDetail = isDirectory
                    ? "application directory is writable by a non-owner principal"
                    : "native library file is writable by a non-owner principal";
                return false;
            }
        }

        return true;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsTrustedOwner(SecurityIdentifier owner)
    {
        if (IsTrustedSystemPrincipal(owner))
            return true;

        using var identity = WindowsIdentity.GetCurrent();
        return identity.User is not null && identity.User.Equals(owner);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsTrustedSystemPrincipal(SecurityIdentifier sid) =>
        sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
        || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);

    [SupportedOSPlatform("windows")]
    private static bool IsBroadWritePrincipal(SecurityIdentifier sid)
    {
        ReadOnlySpan<WellKnownSidType> broad =
        [
            WellKnownSidType.WorldSid,
            WellKnownSidType.BuiltinUsersSid,
            WellKnownSidType.AuthenticatedUserSid,
            WellKnownSidType.InteractiveSid,
            WellKnownSidType.AnonymousSid,
            WellKnownSidType.BatchSid,
            WellKnownSidType.ServiceSid,
            WellKnownSidType.NetworkSid,
            WellKnownSidType.RestrictedCodeSid,
            WellKnownSidType.BuiltinGuestsSid,
            WellKnownSidType.BuiltinPowerUsersSid,
            WellKnownSidType.BuiltinRemoteDesktopUsersSid,
            WellKnownSidType.LocalSid,
            WellKnownSidType.LocalServiceSid,
            WellKnownSidType.NetworkServiceSid,
        ];

        foreach (var type in broad)
        {
            if (sid.IsWellKnown(type))
                return true;
        }

        return false;
    }
}
