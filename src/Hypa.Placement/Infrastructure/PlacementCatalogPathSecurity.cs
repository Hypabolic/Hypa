using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Hypa.Placement.Infrastructure;

/// <summary>
/// Owner-private catalog directory and file modes. Fail closed when not private.
/// </summary>
internal static class PlacementCatalogPathSecurity
{
    internal static void EnsurePrivateDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            UnixCatalogPathSecurity.EnsurePrivateDirectory(path);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            WindowsCatalogPathSecurity.EnsurePrivateDirectory(path);
            return;
        }

        throw new UnauthorizedAccessException("placement catalog requires a private owner-only directory");
    }

    internal static void EnsurePrivateFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            UnixCatalogPathSecurity.EnsurePrivateFile(path);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            WindowsCatalogPathSecurity.EnsurePrivateFile(path);
            return;
        }

        throw new UnauthorizedAccessException("placement catalog requires a private owner-only file");
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static class UnixCatalogPathSecurity
    {
        internal static void EnsurePrivateDirectory(string path)
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var want = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            if (File.GetUnixFileMode(path) != want)
            {
                throw new UnauthorizedAccessException(
                    $"placement catalog directory '{path}' must be owner-private (0700)");
            }
        }

        internal static void EnsurePrivateFile(string path)
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var want = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if (File.GetUnixFileMode(path) != want)
            {
                throw new UnauthorizedAccessException(
                    $"placement catalog file '{path}' must be owner-private (0600)");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static class WindowsCatalogPathSecurity
    {
        internal static void EnsurePrivateDirectory(string path)
        {
            var owner = RequireCurrentUser().User!;
            var dir = new DirectoryInfo(path);
            var acl = dir.GetAccessControl();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            RemoveAllowRules(acl);
            acl.AddAccessRule(
                new FileSystemAccessRule(
                    owner,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            dir.SetAccessControl(acl);
            VerifyDirectory(path, owner);
        }

        internal static void EnsurePrivateFile(string path)
        {
            var owner = RequireCurrentUser().User!;
            var file = new FileInfo(path);
            var acl = file.GetAccessControl();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            RemoveAllowRules(acl);
            acl.AddAccessRule(
                new FileSystemAccessRule(
                    owner,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));
            file.SetAccessControl(acl);
            VerifyFile(path, owner);
        }

        private static WindowsIdentity RequireCurrentUser()
        {
            var identity = WindowsIdentity.GetCurrent()
                ?? throw new UnauthorizedAccessException("placement catalog requires a local owner identity");
            if (identity.User is null)
            {
                throw new UnauthorizedAccessException("placement catalog requires a local owner identity");
            }

            return identity;
        }

        private static void RemoveAllowRules(FileSystemSecurity acl)
        {
            var rules = acl.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules)
                acl.RemoveAccessRule(rule);
        }

        private static void VerifyDirectory(string path, SecurityIdentifier owner)
        {
            var acl = new DirectoryInfo(path).GetAccessControl();
            if (!acl.AreAccessRulesProtected || HasForeignAllowRules(acl, owner))
            {
                throw new UnauthorizedAccessException(
                    $"placement catalog directory '{path}' must be owner-private");
            }
        }

        private static void VerifyFile(string path, SecurityIdentifier owner)
        {
            var acl = new FileInfo(path).GetAccessControl();
            if (!acl.AreAccessRulesProtected || HasForeignAllowRules(acl, owner))
            {
                throw new UnauthorizedAccessException(
                    $"placement catalog file '{path}' must be owner-private");
            }
        }

        private static bool HasForeignAllowRules(FileSystemSecurity acl, SecurityIdentifier owner)
        {
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(
                         includeExplicit: true,
                         includeInherited: false,
                         typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow)
                    continue;
                if (rule.IdentityReference is not SecurityIdentifier sid)
                    continue;
                if (!sid.Equals(owner))
                    return true;
            }

            return false;
        }
    }
}
