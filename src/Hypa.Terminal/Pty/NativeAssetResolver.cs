using System.Runtime.InteropServices;

namespace Hypa.Terminal.Pty;

/// <summary>
/// Resolves private native assets (hypa-pty-host, libghostty-vt) from pinned
/// locations only. Never walks cwd or parent directories.
/// </summary>
public static class NativeAssetResolver
{
    public const string PtyHostFileName = "hypa-pty-host";

    /// <summary>Beside-apphost channel marker written by the F2 packer only.</summary>
    public const string ChannelMarkerFileName = "hypa.channel";

    /// <summary>
    /// Portable RID for the current process (osx-arm64, linux-x64, …).
    /// </summary>
    public static string PortableRid()
    {
        if (OperatingSystem.IsMacOS())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "osx-arm64",
                Architecture.X64 => "osx-x64",
                _ => "osx-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            };
        }

        if (OperatingSystem.IsLinux())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "linux-arm64",
                Architecture.X64 => "linux-x64",
                Architecture.Arm => "linux-arm",
                _ => "linux-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            };
        }

        if (OperatingSystem.IsWindows())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "win-arm64",
                _ => "win-x64",
            };
        }

        return "unknown";
    }

    /// <summary>
    /// Accept an explicit or env override only when it is absolute.
    /// Relative values are rejected (do not resolve against cwd).
    /// </summary>
    public static string? RequireAbsoluteOverride(
        string? explicitPath,
        string? envValue,
        string envName)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            if (!Path.IsPathRooted(explicitPath))
            {
                throw new ArgumentException(
                    envName + " / explicit native path must be absolute.",
                    nameof(explicitPath));
            }

            return explicitPath;
        }

        if (!string.IsNullOrWhiteSpace(envValue))
        {
            if (!Path.IsPathRooted(envValue))
            {
                throw new ArgumentException(
                    envName + " must be an absolute path.",
                    envName);
            }

            return envValue;
        }

        return null;
    }

    /// <summary>
    /// Search roots: AppContext.BaseDirectory only. No cwd. No parent walk.
    /// </summary>
    public static IReadOnlyList<string> EnumerateSearchRoots()
    {
        var baseDir = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(baseDir))
            return [];

        return [Path.GetFullPath(baseDir)];
    }

    /// <summary>
    /// Candidate files under the binary directory and the pinned RID layout.
    /// </summary>
    public static IReadOnlyList<string> EnumerateSearchPaths(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        var list = new List<string>();
        var rid = PortableRid();
        foreach (var root in EnumerateSearchRoots())
        {
            AddUnique(list, Path.Combine(root, fileName));
            AddUnique(list, Path.Combine(root, "native", "runtimes", rid, "native", fileName));
            AddUnique(list, Path.Combine(root, "runtimes", rid, "native", fileName));
        }

        return list;
    }

    /// <summary>
    /// Resolve an existing file. An absolute override is returned even when missing
    /// so the caller can emit the requested path. Relative overrides throw.
    /// </summary>
    public static string? Resolve(
        string fileName,
        string? explicitPath = null,
        string? envVarName = null)
    {
        var envValue = envVarName is null
            ? null
            : Environment.GetEnvironmentVariable(envVarName);
        var overridePath = RequireAbsoluteOverride(
            explicitPath,
            envValue,
            envVarName ?? "native path");
        if (overridePath is not null)
            return overridePath;

        foreach (var candidate in EnumerateSearchPaths(fileName))
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Read a text file beside the apphost (<see cref="AppContext.BaseDirectory"/>).
    /// No cwd. No parent walk. Missing file returns null.
    /// </summary>
    public static string? ReadBesideBinaryText(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        var baseDir = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(baseDir))
            return null;

        var path = Path.Combine(Path.GetFullPath(baseDir), fileName);
        if (!File.Exists(path))
            return null;

        return File.ReadAllText(path).Trim();
    }

    /// <summary>Read <c>hypa.channel</c> beside the apphost. F1 packs omit this file.</summary>
    public static string? ReadChannelMarker()
        => ReadBesideBinaryText(ChannelMarkerFileName);

    private static void AddUnique(List<string> list, string path)
    {
        var full = Path.GetFullPath(path);
        if (!list.Contains(full, StringComparer.Ordinal))
            list.Add(full);
    }
}
