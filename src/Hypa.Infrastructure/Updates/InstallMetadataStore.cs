using System.Runtime.InteropServices;
using System.Text.Json;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Updates;

namespace Hypa.Infrastructure.Updates;

public sealed class InstallMetadataStore(IConfigLoader config, IRuntimeIdentifierProvider rid) : IInstallMetadataStore
{
    public async Task<InstallMetadata> GetAsync(CancellationToken ct)
    {
        // A package manager owns the files it installed. A stale install.json from an
        // earlier script install must not redirect `hypa update` at a different copy.
        var detected = DetectSource(Environment.ProcessPath ?? string.Empty);
        if (PackageManagerUpdateStrategy.IsPackageManagerSource(detected))
            return Infer(detected);

        try
        {
            var path = await GetMetadataPathAsync(ct);
            if (File.Exists(path))
            {
                await using var stream = File.OpenRead(path);
                var metadata = await JsonSerializer.DeserializeAsync(
                    stream, UpdatesJsonContext.Default.InstallMetadata, ct);
                if (metadata is not null)
                    return metadata;
            }
        }
        catch { }

        return Infer(detected);
    }

    public async Task SaveAsync(InstallMetadata metadata, CancellationToken ct)
    {
        try
        {
            var path = await GetMetadataPathAsync(ct);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(
                stream, metadata, UpdatesJsonContext.Default.InstallMetadata, ct);
        }
        catch { }
    }

    private async Task<string> GetMetadataPathAsync(CancellationToken ct)
    {
        try
        {
            var configResult = await config.LoadAsync(ct);
            if (configResult.IsOk)
                return Path.Combine(configResult.Value.StoragePath, "install.json");
        }
        catch { }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".hypa", "install.json");
    }

    private InstallMetadata Infer(string source)
    {
        string? installDir = null;
        string? binLink = null;
        string? execPath = null;

        if (source == "script")
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                installDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Hypa", "bin");
                execPath = Path.Combine(installDir, "hypa.exe");
            }
            else
            {
                installDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share", "hypa");
                binLink = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "bin", "hypa");
                execPath = Path.Combine(installDir, "hypa");
            }
        }

        return new InstallMetadata(
            Source: source,
            RuntimeIdentifier: rid.RuntimeIdentifier,
            InstallDirectory: installDir,
            BinLinkPath: binLink,
            ExecutablePath: execPath,
            InstalledVersion: null,
            InstalledAt: null);
    }

    // Check the path hypa was started as and, when that is a symlink, where it points.
    // A package manager's bin link (for example /usr/local/bin/hypa) resolves into its
    // own tree, which is what identifies the owner.
    private static string DetectSource(string processPath)
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Func<string, string?> tryResolveSymlink = path =>
        {
            try { return new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: false)?.FullName; }
            catch { return null; }
        };

        var source = DetectSource(processPath, isWindows, home, localAppData, tryResolveSymlink);
        if (source != "unknown" || string.IsNullOrEmpty(processPath))
            return source;

        try
        {
            var resolved = File.ResolveLinkTarget(processPath, returnFinalTarget: true)?.FullName;
            if (resolved is not null)
                return DetectSource(resolved, isWindows, home, localAppData, tryResolveSymlink);
        }
        catch { }

        return source;
    }

    internal static string DetectSource(
        string processPath,
        bool isWindows,
        string home,
        string localAppData,
        Func<string, string?> tryResolveSymlink)
    {
        if (string.IsNullOrEmpty(processPath))
            return "unknown";

        // Normalize to forward slashes so comparisons work regardless of which OS the
        // code is running on (tests may pass Unix-style paths on Windows or vice-versa).
        var normalizedPath = processPath.Replace('\\', '/');
        var normalizedHome = home.Replace('\\', '/').TrimEnd('/');
        var normalizedLocalAppData = localAppData.Replace('\\', '/').TrimEnd('/');

        var packageManager = DetectPackageManager(normalizedPath);
        if (packageManager is not null)
            return packageManager;

        if (isWindows)
        {
            var winScriptDirWithSep = normalizedLocalAppData + "/Hypa/bin/";
            if (normalizedPath.StartsWith(winScriptDirWithSep, StringComparison.OrdinalIgnoreCase))
                return "script";
        }
        else
        {
            var stableDir = normalizedHome + "/.local/share/hypa";
            var stableDirWithSep = stableDir + "/";
            if (normalizedPath.StartsWith(stableDirWithSep, StringComparison.Ordinal))
                return "script";
            // For versioned installs the stable dir is a symlink to the real versioned dir.
            // Resolve it so we only accept paths inside the actual symlink target, not any
            // directory that happens to share the "hypa-" prefix.
            var resolvedTarget = tryResolveSymlink(stableDir);
            if (resolvedTarget is not null)
            {
                var resolvedWithSep = resolvedTarget.Replace('\\', '/').TrimEnd('/') + "/";
                if (normalizedPath.StartsWith(resolvedWithSep, StringComparison.Ordinal))
                    return "script";
            }
        }

        return "unknown";
    }

    private static string? DetectPackageManager(string path)
    {
        // Homebrew keeps every keg under <prefix>/Cellar/<formula>/<version>. This also
        // covers Intel macOS (/usr/local) and Linuxbrew (/home/linuxbrew/.linuxbrew).
        if (path.Contains("/Cellar/hypa/", StringComparison.Ordinal) ||
            path.Contains("/opt/homebrew/bin/hypa", StringComparison.Ordinal))
            return "homebrew";

        if (path.Contains("scoop/apps/hypa", StringComparison.OrdinalIgnoreCase))
            return "scoop";

        // The npm wrapper resolves the binary inside a platform package, for example
        // node_modules/@hypabolic/hypa-darwin-arm64/bin/hypa. pnpm nests that under .pnpm.
        if (path.Contains("/node_modules/@hypabolic/hypa-", StringComparison.Ordinal))
            return path.Contains("/node_modules/.pnpm/", StringComparison.Ordinal) ? "pnpm" : "npm";

        // The PyPI wheel unpacks the binary next to the Python package:
        // <site-packages>/hypa/bin/hypa (dist-packages on Debian-patched Python).
        // pipx and uv tool use their own venvs, and each one upgrades with its own command.
        if (path.Contains("/site-packages/hypa/bin/", StringComparison.Ordinal) ||
            path.Contains("/dist-packages/hypa/bin/", StringComparison.Ordinal))
        {
            if (path.Contains("/pipx/venvs/hypa/", StringComparison.Ordinal))
                return "pipx";
            if (path.Contains("/uv/tools/hypa/", StringComparison.Ordinal))
                return "uv";
            return "pip";
        }

        return null;
    }
}
