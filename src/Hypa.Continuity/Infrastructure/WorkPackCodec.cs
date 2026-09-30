using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hypa.Continuity.Application;
using Hypa.Continuity.Domain;

namespace Hypa.Continuity.Infrastructure;

/// <summary>
/// WorkPack tar+gzip codec (C-12). Max uncompressed 512 MiB.
/// A WorkPack is not safe to share. Transcript bodies may still hold secrets.
/// </summary>
public sealed class WorkPackCodec : IWorkPackCodec
{
    public const long DefaultMaxUncompressedBytes = 512L * 1024 * 1024;

    private readonly long _maxUncompressedBytes;

    public WorkPackCodec(long? maxUncompressedBytes = null)
    {
        _maxUncompressedBytes = maxUncompressedBytes ?? DefaultMaxUncompressedBytes;
        if (_maxUncompressedBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxUncompressedBytes));
    }

    /// <summary>Production ceiling (512 MiB). Tests may construct with a smaller limit.</summary>
    public long MaxUncompressedBytes => _maxUncompressedBytes;

    public ContinuityOutcome Write(
        string spoolDirectory,
        WorkPackManifestDto manifest,
        string harnessDir,
        string workspaceDir,
        out string? packPath)
    {
        packPath = null;
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(spoolDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDir);

        if (string.IsNullOrWhiteSpace(manifest.WorkId)
            || string.IsNullOrWhiteSpace(manifest.Harness.AdapterId)
            || string.IsNullOrWhiteSpace(manifest.Harness.ConversationId))
        {
            return ContinuityOutcome.Failure(ContinuityReasons.PackInvalid, "manifest missing required keys");
        }

        Directory.CreateDirectory(spoolDirectory);
        var fileName = manifest.WorkId + ".workpack";
        var finalPath = Path.Combine(spoolDirectory, fileName);
        var partialPath = finalPath + ".partial";
        var sidecarPath = finalPath + ".sha256";

        try
        {
            if (File.Exists(partialPath))
                File.Delete(partialPath);

            long uncompressed = 0;
            using (var partial = File.Create(partialPath))
            using (var gzip = new GZipStream(partial, CompressionLevel.Optimal, leaveOpen: false))
            using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
            {
                var manifestJson = JsonSerializer.Serialize(
                    manifest,
                    WorkPackManifestJsonContext.Default.WorkPackManifestDto);
                var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
                uncompressed += manifestBytes.LongLength;
                if (uncompressed > _maxUncompressedBytes)
                    return ContinuityOutcome.Failure(ContinuityReasons.PackTooLarge, "manifest alone exceeds limit");

                WriteBytesEntry(writer, "manifest.json", manifestBytes);

                var harnessOutcome = AddDirectory(writer, harnessDir, "harness", ref uncompressed);
                if (!harnessOutcome.Ok)
                    return harnessOutcome;

                var workspaceOutcome = AddDirectory(writer, workspaceDir, "workspace", ref uncompressed);
                if (!workspaceOutcome.Ok)
                    return workspaceOutcome;
            }

            var hash = Sha256HexFile(partialPath);
            File.WriteAllText(
                sidecarPath,
                hash + "  " + fileName + "\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(partialPath, finalPath, overwrite: true);
            packPath = finalPath;
            return ContinuityOutcome.Success();
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(partialPath))
                    File.Delete(partialPath);
            }
            catch (IOException)
            {
            }

            return ContinuityOutcome.Failure(ContinuityReasons.Internal, ex.Message);
        }
    }

    public string HashPackFile(string packPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packPath);
        return Sha256HexFile(packPath);
    }

    public ContinuityOutcome Read(
        string packPath,
        string extractRoot,
        out WorkPackManifestDto? manifest)
    {
        manifest = null;
        ArgumentException.ThrowIfNullOrWhiteSpace(packPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(extractRoot);

        var verify = VerifySidecar(packPath);
        if (!verify.Ok)
            return verify;

        try
        {
            if (Directory.Exists(extractRoot))
                Directory.Delete(extractRoot, recursive: true);
            Directory.CreateDirectory(extractRoot);
            long uncompressed = 0;
            using var fs = File.OpenRead(packPath);
            using var gzip = new GZipStream(fs, CompressionMode.Decompress);
            using var reader = new TarReader(gzip, leaveOpen: false);

            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) is not null)
            {
                var name = entry.Name.Replace('\\', '/');
                while (name.StartsWith("./", StringComparison.Ordinal))
                    name = name[2..];
                if (string.IsNullOrWhiteSpace(name)
                    || name.StartsWith('/')
                    || name.Contains("..", StringComparison.Ordinal))
                {
                    return ContinuityOutcome.Failure(ContinuityReasons.PackInvalid, "unsafe path: " + name);
                }

                if (entry.EntryType == TarEntryType.SymbolicLink
                    || entry.EntryType == TarEntryType.HardLink)
                {
                    return ContinuityOutcome.Failure(
                        ContinuityReasons.PackInvalid,
                        "symlinks not allowed in WorkPack");
                }

                if (entry.EntryType is TarEntryType.Directory)
                {
                    Directory.CreateDirectory(Path.Combine(extractRoot, name));
                    continue;
                }

                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                {
                    continue;
                }

                var size = entry.Length;
                uncompressed += size;
                if (uncompressed > _maxUncompressedBytes)
                    return ContinuityOutcome.Failure(ContinuityReasons.PackTooLarge, "uncompressed over limit");

                var target = Path.Combine(extractRoot, name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }

            var manifestPath = Path.Combine(extractRoot, "manifest.json");
            if (!File.Exists(manifestPath))
                return ContinuityOutcome.Failure(ContinuityReasons.PackInvalid, "manifest.json missing");

            var json = File.ReadAllText(manifestPath);
            manifest = JsonSerializer.Deserialize(
                json,
                WorkPackManifestJsonContext.Default.WorkPackManifestDto);
            if (manifest is null
                || string.IsNullOrWhiteSpace(manifest.WorkId)
                || string.IsNullOrWhiteSpace(manifest.Harness.AdapterId)
                || string.IsNullOrWhiteSpace(manifest.Harness.ConversationId)
                || string.IsNullOrWhiteSpace(manifest.Harness.HarnessVersion)
                || manifest.Generation < 1)
            {
                return ContinuityOutcome.Failure(ContinuityReasons.PackInvalid, "manifest missing required keys");
            }

            return ValidatePackIdentity(packPath, extractRoot, manifest);
        }
        catch (Exception ex)
        {
            return ContinuityOutcome.Failure(ContinuityReasons.PackInvalid, ex.Message);
        }
    }

    public ContinuityOutcome VerifySidecar(string packPath)
    {
        if (!File.Exists(packPath))
            return ContinuityOutcome.Failure(ContinuityReasons.PackInvalid, "workpack missing");

        var sidecar = packPath + ".sha256";
        if (!File.Exists(sidecar))
            return ContinuityOutcome.Failure(ContinuityReasons.PackInvalid, "sha256 sidecar missing");

        var line = File.ReadAllText(sidecar).TrimEnd('\n', '\r');
        var parts = line.Split(["  "], 2, StringSplitOptions.None);
        if (parts.Length != 2 || parts[0].Length != 64)
            return ContinuityOutcome.Failure(ContinuityReasons.PackInvalid, "sha256 sidecar malformed");

        var expected = parts[0].ToLowerInvariant();
        var actual = Sha256HexFile(packPath);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            return ContinuityOutcome.Failure(ContinuityReasons.PackInvalid, "sha256 mismatch");

        return ContinuityOutcome.Success();
    }

    /// <summary>
    /// Spec §1.3 / §1.4: work_id, adapter id, version envelope, and workspace hashes
    /// must match the packed files.
    /// </summary>
    private static ContinuityOutcome ValidatePackIdentity(
        string packPath,
        string extractRoot,
        WorkPackManifestDto manifest)
    {
        var fileName = Path.GetFileName(packPath);
        var expectedName = manifest.WorkId + ".workpack";
        if (!string.Equals(fileName, expectedName, StringComparison.Ordinal))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.PackInvalid,
                "work_id does not match pack file name");
        }

        var adapterPath = Path.Combine(extractRoot, "harness", "adapter_id");
        if (!File.Exists(adapterPath))
            return ContinuityOutcome.Failure(ContinuityReasons.PackInvalid, "harness/adapter_id missing");
        var packedAdapter = File.ReadAllText(adapterPath).Trim();
        if (!string.Equals(packedAdapter, manifest.Harness.AdapterId, StringComparison.Ordinal))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.PackInvalid,
                "harness.adapter_id does not match harness/adapter_id");
        }

        var conversationPath = Path.Combine(extractRoot, "harness", "conversation_id.txt");
        if (!File.Exists(conversationPath))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.PackInvalid,
                "harness/conversation_id.txt missing");
        }

        var packedConversation = File.ReadAllText(conversationPath).Trim();
        if (!string.Equals(packedConversation, manifest.Harness.ConversationId, StringComparison.Ordinal))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.PackInvalid,
                "harness.conversation_id does not match harness/conversation_id.txt");
        }

        var versionPath = Path.Combine(extractRoot, "harness", "version.txt");
        if (!File.Exists(versionPath))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.HarnessVersionUnreadable,
                "harness/version.txt missing");
        }

        var packedVersion = File.ReadAllText(versionPath).Trim();
        if (string.IsNullOrWhiteSpace(packedVersion))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.HarnessVersionUnreadable,
                "harness/version.txt empty");
        }

        if (!string.Equals(packedVersion, manifest.Harness.HarnessVersion, StringComparison.Ordinal))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.PackInvalid,
                "harness.harness_version does not match harness/version.txt");
        }

        var hashes = ValidateWorkspaceHashes(extractRoot, manifest);
        if (!hashes.Ok)
            return hashes;

        return ContinuityOutcome.Success();
    }

    private static ContinuityOutcome ValidateWorkspaceHashes(
        string extractRoot,
        WorkPackManifestDto manifest)
    {
        var bundlePath = Path.Combine(extractRoot, "workspace", "HEAD.bundle");
        if (!File.Exists(bundlePath))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.PackInvalid,
                "workspace/HEAD.bundle missing");
        }

        var diffPath = Path.Combine(extractRoot, "workspace", "HEAD.diff");
        if (!File.Exists(diffPath))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.PackInvalid,
                "workspace/HEAD.diff missing");
        }

        var tarPath = Path.Combine(extractRoot, "workspace", "untracked.tar");
        if (!File.Exists(tarPath))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.PackInvalid,
                "workspace/untracked.tar missing");
        }

        var innerPath = Path.Combine(extractRoot, "workspace", "workspace-manifest.json");
        if (!File.Exists(innerPath))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.PackInvalid,
                "workspace/workspace-manifest.json missing");
        }

        if (string.IsNullOrWhiteSpace(manifest.Workspace.Head)
            || string.IsNullOrWhiteSpace(manifest.Workspace.TrackedDiffSha256)
            || string.IsNullOrWhiteSpace(manifest.Workspace.UntrackedSha256))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.PackInvalid,
                "manifest workspace hashes missing");
        }

        var trackedActual = Sha256HexBytes(File.ReadAllBytes(diffPath));
        if (!string.Equals(trackedActual, manifest.Workspace.TrackedDiffSha256, StringComparison.OrdinalIgnoreCase))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.WorkspaceMismatch,
                "tracked_diff_sha256 mismatch");
        }

        var untrackedActual = Sha256HexBytes(File.ReadAllBytes(tarPath));
        if (!string.Equals(untrackedActual, manifest.Workspace.UntrackedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.WorkspaceMismatch,
                "untracked_sha256 mismatch");
        }

        var inner = JsonSerializer.Deserialize(
            File.ReadAllText(innerPath),
            WorkspaceManifestJsonContext.Default.WorkspaceManifestDto);
        if (inner is null)
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.PackInvalid,
                "workspace/workspace-manifest.json invalid");
        }

        if (string.IsNullOrWhiteSpace(inner.Head)
            || string.IsNullOrWhiteSpace(inner.TrackedDiffSha256)
            || string.IsNullOrWhiteSpace(inner.UntrackedSha256))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.PackInvalid,
                "workspace-manifest missing required hashes");
        }

        if (!string.Equals(inner.Head, manifest.Workspace.Head, StringComparison.OrdinalIgnoreCase))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.WorkspaceMismatch,
                "workspace head mismatch");
        }

        if (!string.Equals(
                inner.TrackedDiffSha256,
                manifest.Workspace.TrackedDiffSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.WorkspaceMismatch,
                "workspace-manifest tracked hash mismatch");
        }

        if (!string.Equals(
                inner.UntrackedSha256,
                manifest.Workspace.UntrackedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.WorkspaceMismatch,
                "workspace-manifest untracked hash mismatch");
        }

        return ContinuityOutcome.Success();
    }

    private ContinuityOutcome AddDirectory(
        TarWriter writer,
        string sourceDir,
        string prefix,
        ref long uncompressed)
    {
        if (!Directory.Exists(sourceDir))
            return ContinuityOutcome.Failure(ContinuityReasons.PackInvalid, prefix + " dir missing");

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
            if (rel.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
                return ContinuityOutcome.Failure(ContinuityReasons.PackInvalid, "bad relative path");

            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
                return ContinuityOutcome.Failure(ContinuityReasons.PackInvalid, "symlink escape refused");

            var bytes = File.ReadAllBytes(file);
            uncompressed += bytes.LongLength;
            if (uncompressed > _maxUncompressedBytes)
                return ContinuityOutcome.Failure(ContinuityReasons.PackTooLarge, "over uncompressed limit");

            WriteBytesEntry(writer, prefix + "/" + rel, bytes);
        }

        return ContinuityOutcome.Success();
    }

    private static void WriteBytesEntry(TarWriter writer, string name, byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = ms,
        };
        writer.WriteEntry(entry);
    }

    public static string Sha256HexFile(string path)
    {
        using var fs = File.OpenRead(path);
        var hash = SHA256.HashData(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Sha256HexBytes(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
