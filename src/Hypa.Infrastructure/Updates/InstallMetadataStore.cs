using System.Runtime.InteropServices;
using System.Text.Json;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Updates;

namespace Hypa.Infrastructure.Updates;

public sealed class InstallMetadataStore(IConfigLoader config, IRuntimeIdentifierProvider rid) : IInstallMetadataStore
{
    public async Task<InstallMetadata> GetAsync(CancellationToken ct)
    {
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

        return Infer();
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

    private InstallMetadata Infer()
    {
        var processPath = Environment.ProcessPath ?? string.Empty;
        var source = DetectSource(processPath);

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

    private static string DetectSource(string processPath) =>
        DetectSource(processPath,
            isWindows: RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            home: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            localAppData: Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            tryResolveSymlink: path =>
            {
                try { return new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: false)?.FullName; }
                catch { return null; }
            });

    internal static string DetectSource(
        string processPath,
        bool isWindows,
        string home,
        string localAppData,
        Func<string, string?> tryResolveSymlink)
    {
        if (string.IsNullOrEmpty(processPath))
            return "unknown";

        if (processPath.Contains("/Cellar/hypa/", StringComparison.Ordinal) ||
            processPath.Contains("/opt/homebrew/bin/hypa", StringComparison.Ordinal))
            return "homebrew";

        if (processPath.Contains("scoop/apps/hypa", StringComparison.OrdinalIgnoreCase))
            return "scoop";

        // Normalize to forward slashes so comparisons work regardless of which OS the
        // code is running on (tests may pass Unix-style paths on Windows or vice-versa).
        var normalizedPath = processPath.Replace('\\', '/');
        var normalizedHome = home.Replace('\\', '/').TrimEnd('/');
        var normalizedLocalAppData = localAppData.Replace('\\', '/').TrimEnd('/');

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
}
