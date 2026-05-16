using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Updates;

namespace Hypa.Infrastructure.Updates;

public sealed class ScriptInstallUpdateStrategy : IUpdateStrategy
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IInstallMetadataStore _metadataStore;
    private readonly Action<string, string> _renameLink;

    public ScriptInstallUpdateStrategy(IHttpClientFactory httpClientFactory, IInstallMetadataStore metadataStore)
        : this(httpClientFactory, metadataStore, Directory.Move) { }

    internal ScriptInstallUpdateStrategy(IHttpClientFactory httpClientFactory, IInstallMetadataStore metadataStore, Action<string, string> renameLink)
    {
        _httpClientFactory = httpClientFactory;
        _metadataStore = metadataStore;
        _renameLink = renameLink;
    }

    public string Name => "script";

    public bool CanHandle(InstallMetadata metadata) =>
        metadata.Source == "script";

    public Task<Result<UpdatePlan, Error>> PlanAsync(UpdateInfo update, InstallMetadata metadata, CancellationToken ct)
    {
        if (!ValidatePreconditions(update, metadata, out var err))
            return Task.FromResult(Result<UpdatePlan, Error>.Fail(err));

        var plan = new UpdatePlan(
            Strategy: Name,
            CanAutoUpdate: !RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            Summary: $"Update from {update.CurrentVersion} to {update.LatestVersion} via script install",
            Command: "hypa update",
            Detail: RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "Windows self-update is not yet supported. Re-run install.ps1 to upgrade."
                : null);

        return Task.FromResult(Result<UpdatePlan, Error>.Ok(plan));
    }

    public async Task<Result<Unit, Error>> ApplyAsync(UpdateInfo update, InstallMetadata metadata, CancellationToken ct)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Result<Unit, Error>.Fail(new Error(
                "Update.WindowsNotSupported",
                "Windows self-update is not yet supported. Re-run install.ps1 to upgrade."));

        if (!ValidatePreconditions(update, metadata, out var err))
            return Result<Unit, Error>.Fail(err);

        // Canonicalize both paths so traversal components like ../other are resolved
        // before any prefix comparison.
        var installDir = Path.GetFullPath(metadata.InstallDirectory!);
        var execPath = Path.GetFullPath(metadata.ExecutablePath!);
        var assetName = update.AssetName;

        var installDirPrefix = installDir.TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!execPath.StartsWith(installDirPrefix, StringComparison.Ordinal))
            return Result<Unit, Error>.Fail(new Error(
                "Update.PathMismatch",
                $"Executable '{execPath}' is not inside install directory '{installDir}'."));

        var tempDir = Path.Combine(Path.GetTempPath(), $"hypa-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var archivePath = Path.Combine(tempDir, assetName);
            var checksumsPath = Path.Combine(tempDir, "SHA256SUMS");

            var downloadResult = await DownloadFileAsync(update.DownloadUrl!, archivePath, ct);
            if (!downloadResult.IsOk) return Result<Unit, Error>.Fail(downloadResult.Error);

            var csResult = await DownloadFileAsync(update.ChecksumsUrl!, checksumsPath, ct);
            if (!csResult.IsOk) return Result<Unit, Error>.Fail(csResult.Error);

            var verifyResult = VerifyChecksum(archivePath, assetName, checksumsPath);
            if (!verifyResult.IsOk) return Result<Unit, Error>.Fail(verifyResult.Error);

            var extractDir = Path.Combine(tempDir, "extracted");
            Directory.CreateDirectory(extractDir);

            var extractResult = ExtractArchive(archivePath, extractDir);
            if (!extractResult.IsOk) return Result<Unit, Error>.Fail(extractResult.Error);

            var extractedBinary = FindBinary(extractDir);
            if (extractedBinary is null)
                return Result<Unit, Error>.Fail(new Error("Update.BinaryNotFound", "Could not locate hypa binary in archive."));

            var packageDir = Path.GetDirectoryName(extractedBinary)!;
            var binaryRelPath = Path.GetRelativePath(packageDir, extractedBinary);

            // Stage the entire package beside the install dir (same filesystem) then
            // swap the whole directory atomically.  This means:
            //   • the live install dir is never partially updated,
            //   • stale files removed from a new release disappear automatically,
            //   • rollback to the old dir is possible if the promotion rename fails.
            var stagingDir = installDir + $".new-{Guid.NewGuid():N}";
            Directory.CreateDirectory(stagingDir);
            var stagingDirPromoted = false;  // true when installDir symlink is pointing at stagingDir
            try
            {
                foreach (var src in Directory.GetFiles(packageDir, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(packageDir, src);
                    var dest = Path.Combine(stagingDir, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(src, dest, overwrite: true);
                }

                var stagedBinary = Path.Combine(stagingDir, binaryRelPath);
                File.SetUnixFileMode(stagedBinary,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

                // If installDir is a symlink (set up by the installer), swap it to point at
                // stagingDir.  We use File.GetAttributes (which calls lstat, not stat) to
                // reliably detect the symlink regardless of what the target type is.
                //
                // If installDir is a real directory (legacy install), use the two-rename
                // approach: move the old dir aside, then move staging into place.
                var installDirIsSymlink = Directory.Exists(installDir)
                    && File.GetAttributes(installDir).HasFlag(FileAttributes.ReparsePoint);

                if (installDirIsSymlink)
                {
                    // Resolve old versioned dir for cleanup and rollback.
                    var oldTarget = File.ResolveLinkTarget(installDir, returnFinalTarget: false);
                    var oldVersionedDir = oldTarget?.FullName;

                    // Two-step swap: unlink old symlink then rename temp symlink into its place.
                    // There is a brief window between the two steps where installDir is absent.
                    // On failure after the unlink, recreate the old symlink before rethrowing
                    // so the user's install path is not left broken.
                    var tempLink = installDir + $".new-{Guid.NewGuid():N}";
                    Directory.CreateSymbolicLink(tempLink, stagingDir);
                    var unlinkedInstallDir = false;
                    try
                    {
                        File.Delete(installDir);
                        unlinkedInstallDir = true;
                        _renameLink(tempLink, installDir);
                        // stagingDir is now the live versioned dir; the finally must not delete it.
                        stagingDirPromoted = true;
                    }
                    catch
                    {
                        try { File.Delete(tempLink); } catch { }
                        if (unlinkedInstallDir && !stagingDirPromoted && oldVersionedDir is not null)
                            try { Directory.CreateSymbolicLink(installDir, oldVersionedDir); } catch { }
                        throw;
                    }
                    if (oldVersionedDir is not null)
                        try { Directory.Delete(oldVersionedDir, recursive: true); } catch { }
                }
                else
                {
                    var oldDir = installDir + $".old-{Guid.NewGuid():N}";
                    if (Directory.Exists(installDir))
                    {
                        Directory.Move(installDir, oldDir);
                        try
                        {
                            Directory.Move(stagingDir, installDir);
                        }
                        catch
                        {
                            try { Directory.Move(oldDir, installDir); } catch { }
                            throw;
                        }
                        try { Directory.Delete(oldDir, recursive: true); } catch { }
                    }
                    else
                    {
                        Directory.Move(stagingDir, installDir);
                    }
                }
            }
            finally
            {
                if (!stagingDirPromoted)
                    try { Directory.Delete(stagingDir, recursive: true); } catch { }
            }

            await _metadataStore.SaveAsync(metadata with
            {
                InstallDirectory = installDir,
                ExecutablePath = execPath,
                InstalledVersion = update.LatestVersion,
                InstalledAt = DateTimeOffset.UtcNow,
            }, ct);

            return Result<Unit, Error>.Ok(Unit.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Result<Unit, Error>.Fail(new Error("Update.PromotionFailed", ex.Message));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private async Task<Result<Unit, Error>> DownloadFileAsync(string url, string destPath, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("hypa-update");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
                return Result<Unit, Error>.Fail(new Error("Update.DownloadError", $"HTTP {(int)response.StatusCode} for {url}"));

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(destPath);
            await stream.CopyToAsync(file, ct);
            return Result<Unit, Error>.Ok(Unit.Value);
        }
        catch (Exception ex)
        {
            return Result<Unit, Error>.Fail(new Error("Update.DownloadError", ex.Message));
        }
    }

    internal static Result<Unit, Error> ExtractArchive(string archivePath, string extractDir)
    {
        try
        {
            if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
                archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            {
                // Pre-compute the canonical prefix once so every entry pays only one GetFullPath call.
                var extractDirPrefix = Path.GetFullPath(extractDir)
                    .TrimEnd(Path.DirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;

                using var fileStream = File.OpenRead(archivePath);
                using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
                using var tar = new TarReader(gzip);

                TarEntry? entry;
                while ((entry = tar.GetNextEntry()) is not null)
                {
                    if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                    {
                        var relativeName = entry.Name
                            .Replace('/', Path.DirectorySeparatorChar)
                            .TrimStart(Path.DirectorySeparatorChar);

                        var destPath = Path.GetFullPath(Path.Combine(extractDir, relativeName));

                        if (!destPath.StartsWith(extractDirPrefix, StringComparison.Ordinal))
                            return Result<Unit, Error>.Fail(new Error(
                                "Update.PathTraversal",
                                $"Archive entry '{entry.Name}' resolves outside the extraction directory."));

                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                        entry.ExtractToFile(destPath, overwrite: true);
                    }
                }
            }
            else
            {
                ZipFile.ExtractToDirectory(archivePath, extractDir, overwriteFiles: true);
            }

            return Result<Unit, Error>.Ok(Unit.Value);
        }
        catch (Exception ex)
        {
            return Result<Unit, Error>.Fail(new Error("Update.ExtractError", ex.Message));
        }
    }

    private static Result<Unit, Error> VerifyChecksum(string archivePath, string assetName, string checksumsPath)
    {
        try
        {
            var lines = File.ReadAllLines(checksumsPath);
            string? expected = null;
            foreach (var line in lines)
            {
                var parts = line.Split("  ", 2, StringSplitOptions.None);
                if (parts.Length == 2 && parts[1].Trim() == assetName)
                {
                    expected = parts[0].Trim();
                    break;
                }
            }

            if (expected is null)
                return Result<Unit, Error>.Fail(new Error("Update.ChecksumMissing", $"No checksum entry for '{assetName}'."));

            using var sha = SHA256.Create();
            using var file = File.OpenRead(archivePath);
            var hashBytes = sha.ComputeHash(file);
            var actual = Convert.ToHexString(hashBytes).ToLowerInvariant();

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                return Result<Unit, Error>.Fail(new Error("Update.ChecksumMismatch",
                    $"Checksum mismatch: expected {expected}, got {actual}."));

            return Result<Unit, Error>.Ok(Unit.Value);
        }
        catch (Exception ex)
        {
            return Result<Unit, Error>.Fail(new Error("Update.ChecksumError", ex.Message));
        }
    }

    private static string? FindBinary(string dir)
    {
        var name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "hypa.exe" : "hypa";
        return Directory.GetFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
    }

    private static bool ValidatePreconditions(UpdateInfo update, InstallMetadata metadata, out Error error)
    {
        if (string.IsNullOrEmpty(update.DownloadUrl))
        {
            error = new Error("Update.NoDownloadUrl", "No download URL found for this platform.");
            return false;
        }

        if (string.IsNullOrEmpty(update.ChecksumsUrl))
        {
            error = new Error("Update.NoChecksums", "SHA256SUMS asset not found in this release; checksum verification is required for script install updates.");
            return false;
        }

        if (string.IsNullOrEmpty(metadata.InstallDirectory) || string.IsNullOrEmpty(metadata.ExecutablePath))
        {
            error = new Error("Update.NoInstallDir", "Install directory or executable path is unknown.");
            return false;
        }

        error = default;
        return true;
    }
}
