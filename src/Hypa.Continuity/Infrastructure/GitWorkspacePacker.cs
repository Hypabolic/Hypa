using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hypa.Continuity.Application;
using Hypa.Continuity.Domain;

namespace Hypa.Continuity.Infrastructure;

/// <summary>Git workspace capture / apply per cubes-v0-spec §3.</summary>
public sealed class GitWorkspacePacker : IWorkspacePacker
{
    private static readonly string[] PackArtifactNames =
    [
        "HEAD.diff",
        "HEAD.bundle",
        "untracked.tar",
        "workspace-manifest.json",
    ];

    private readonly string _gitFileName;
    private readonly IReadOnlyDictionary<string, string>? _gitEnvironment;
    private readonly Action<string>? _onPromotedArtifact;
    private readonly Action<string>? _onRestoredArtifact;
    private readonly Action<string>? _onCompleteRestoredArtifact;
    private readonly Action<string>? _onRecoveredArtifact;
    private readonly Action<string>? _onRecoveredDestArtifact;
    private readonly Action<string>? _onFallbackCopiedArtifact;
    private readonly Action<string>? _onFallbackDestArtifact;
    private readonly Action<string>? _onLastCopyDestArtifact;
    private readonly Action<string>? _onRollbackDestArtifact;
    private readonly Action<string>? _onRetryDestArtifact;

    public GitWorkspacePacker()
        : this(
            "git",
            gitEnvironment: null,
            onPromotedArtifact: null,
            onRestoredArtifact: null,
            onCompleteRestoredArtifact: null,
            onRecoveredArtifact: null,
            onRecoveredDestArtifact: null,
            onFallbackCopiedArtifact: null,
            onFallbackDestArtifact: null,
            onLastCopyDestArtifact: null,
            onRollbackDestArtifact: null,
            onRetryDestArtifact: null)
    {
    }

    internal GitWorkspacePacker(string gitFileName, IReadOnlyDictionary<string, string>? gitEnvironment)
        : this(
            gitFileName,
            gitEnvironment,
            onPromotedArtifact: null,
            onRestoredArtifact: null,
            onCompleteRestoredArtifact: null,
            onRecoveredArtifact: null,
            onRecoveredDestArtifact: null,
            onFallbackCopiedArtifact: null,
            onFallbackDestArtifact: null,
            onLastCopyDestArtifact: null,
            onRollbackDestArtifact: null,
            onRetryDestArtifact: null)
    {
    }

    internal GitWorkspacePacker(
        string gitFileName,
        IReadOnlyDictionary<string, string>? gitEnvironment,
        Action<string>? onPromotedArtifact,
        Action<string>? onRestoredArtifact = null,
        Action<string>? onCompleteRestoredArtifact = null,
        Action<string>? onRecoveredArtifact = null,
        Action<string>? onRecoveredDestArtifact = null,
        Action<string>? onFallbackCopiedArtifact = null,
        Action<string>? onFallbackDestArtifact = null,
        Action<string>? onLastCopyDestArtifact = null,
        Action<string>? onRollbackDestArtifact = null,
        Action<string>? onRetryDestArtifact = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gitFileName);
        _gitFileName = gitFileName;
        _gitEnvironment = gitEnvironment;
        _onPromotedArtifact = onPromotedArtifact;
        _onRestoredArtifact = onRestoredArtifact;
        _onCompleteRestoredArtifact = onCompleteRestoredArtifact;
        _onRecoveredArtifact = onRecoveredArtifact;
        _onRecoveredDestArtifact = onRecoveredDestArtifact;
        _onFallbackCopiedArtifact = onFallbackCopiedArtifact;
        _onFallbackDestArtifact = onFallbackDestArtifact;
        _onLastCopyDestArtifact = onLastCopyDestArtifact;
        _onRollbackDestArtifact = onRollbackDestArtifact;
        _onRetryDestArtifact = onRetryDestArtifact;
    }

    public ContinuityOutcome Capture(string workspacePath, string destWorkspaceDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destWorkspaceDir);

        var ws = Path.GetFullPath(workspacePath);
        if (!Directory.Exists(ws))
            return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceUnsupported, "workspace missing");

        var pre = CheckPreconditions(ws);
        if (!pre.Ok)
            return pre;

        var dest = Path.GetFullPath(destWorkspaceDir);
        var staging = dest + ".partial";
        var backup = Path.Combine(staging, ".orig");
        var destExisted = false;
        var createdPackDirectory = false;
        var promotionStarted = false;
        ContinuityOutcome Fail(string detail)
        {
            if (promotionStarted && destExisted)
            {
                try
                {
                    RestorePackArtifacts(backup, dest);
                }
                catch (Exception restoreEx)
                {
                    detail = string.IsNullOrEmpty(detail)
                        ? restoreEx.Message
                        : detail + "; restore failed: " + restoreEx.Message;
                }
            }

            TryDeletePackDirectory(staging);
            if (createdPackDirectory)
                TryDeletePackDirectory(dest);
            return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceUnsupported, detail);
        }

        try
        {
            TryDeletePackDirectory(staging);
            Directory.CreateDirectory(staging);

            var head = RunGit(ws, ["rev-parse", "HEAD"]);
            if (!head.Ok)
                return Fail(head.Detail!);
            var headSha = head.Stdout!.Trim();

            var branch = RunGit(ws, ["branch", "--show-current"]);
            if (!branch.Ok)
                return Fail(branch.Detail!);
            var branchName = branch.Stdout!.Trim();

            var gitVersion = RunGit(ws, ["--version"]);
            if (!gitVersion.Ok)
                return Fail(gitVersion.Detail!);

            var status = RunGit(ws, ["status", "--porcelain=v2", "--branch", "--untracked-files=all"]);
            if (!status.Ok)
                return Fail(status.Detail!);

            var diffBytes = RunGitBytes(ws, ["diff", "--binary", "HEAD"]);
            if (!diffBytes.Ok)
                return Fail(diffBytes.Detail!);

            var diffPath = Path.Combine(staging, "HEAD.diff");
            File.WriteAllBytes(diffPath, diffBytes.Bytes!);

            var bundlePath = Path.Combine(staging, "HEAD.bundle");
            var bundle = RunGit(ws, ["bundle", "create", bundlePath, "HEAD"]);
            if (!bundle.Ok)
                return Fail(bundle.Detail!);

            var untracked = RunGit(ws, ["ls-files", "-o", "--exclude-standard", "-z"]);
            if (!untracked.Ok)
                return Fail(untracked.Detail!);

            var listed = SplitNul(untracked.Stdout ?? "");
            var packed = new List<string>();
            var skipped = new List<string>();
            foreach (var rel in listed)
            {
                var normalized = rel.Replace('\\', '/');
                if (IsDeniedUntracked(ws, normalized) || IsOutsideWorkspaceSymlink(ws, normalized))
                    skipped.Add(normalized);
                else
                    packed.Add(normalized);
            }

            var untrackedTar = Path.Combine(staging, "untracked.tar");
            WriteUntrackedTar(ws, packed, untrackedTar);

            var trackedHash = Sha256Hex(File.ReadAllBytes(diffPath));
            var untrackedHash = Sha256Hex(File.ReadAllBytes(untrackedTar));

            var manifest = new WorkspaceManifestDto
            {
                Schema = 1,
                Head = headSha,
                Branch = branchName,
                Detached = string.IsNullOrEmpty(branchName),
                GitVersion = gitVersion.Stdout!.Trim(),
                StatusPorcelainV2 = (status.Stdout ?? "")
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList(),
                TrackedDiffSha256 = trackedHash,
                UntrackedSha256 = untrackedHash,
                UntrackedPaths = packed.OrderBy(p => p, StringComparer.Ordinal).ToList(),
                SkippedPaths = skipped.OrderBy(p => p, StringComparer.Ordinal).ToList(),
            };

            var manifestPath = Path.Combine(staging, "workspace-manifest.json");
            var json = JsonSerializer.Serialize(manifest, WorkspaceManifestJsonContext.Default.WorkspaceManifestDto);
            File.WriteAllText(manifestPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (!Directory.Exists(dest))
            {
                Directory.CreateDirectory(dest);
                createdPackDirectory = true;
            }
            else
            {
                destExisted = true;
                BackupExistingPackArtifacts(dest, backup);
            }

            promotionStarted = true;
            PromotePackArtifacts(staging, dest);
            TryDeletePackDirectory(staging);
            return ContinuityOutcome.Success();
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    public ContinuityOutcome Apply(string packWorkspaceDir, string destWorkspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packWorkspaceDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(destWorkspacePath);

        var pack = Path.GetFullPath(packWorkspaceDir);
        var dest = Path.GetFullPath(destWorkspacePath);
        var manifest = LoadManifest(pack);
        if (manifest is null)
            return ContinuityOutcome.Failure(ContinuityReasons.PackInvalid, "workspace-manifest.json missing");

        try
        {
            Directory.CreateDirectory(dest);
            var init = RunGit(dest, ["init"]);
            if (!init.Ok)
                return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceApplyFailed, init.Detail!);

            var bundle = Path.Combine(pack, "HEAD.bundle");
            var fetch = RunGit(dest, ["fetch", bundle, "HEAD"]);
            if (!fetch.Ok)
                return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceApplyFailed, fetch.Detail!);

            var checkout = RunGit(dest, ["checkout", "--force", "FETCH_HEAD"]);
            if (!checkout.Ok)
                return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceApplyFailed, checkout.Detail!);

            var head = RunGit(dest, ["rev-parse", "HEAD"]);
            if (!head.Ok || !string.Equals(head.Stdout!.Trim(), manifest.Head, StringComparison.OrdinalIgnoreCase))
            {
                return ContinuityOutcome.Failure(
                    ContinuityReasons.WorkspaceApplyFailed,
                    $"HEAD after checkout {head.Stdout?.Trim()} != {manifest.Head}");
            }

            var diffPath = Path.Combine(pack, "HEAD.diff");
            if (!File.Exists(diffPath))
                return ContinuityOutcome.Failure(ContinuityReasons.PackInvalid, "HEAD.diff missing");

            var diffLen = new FileInfo(diffPath).Length;
            if (diffLen > 0)
            {
                var apply = RunGit(dest, ["apply", "--binary", "--index", diffPath]);
                if (!apply.Ok)
                    return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceApplyFailed, apply.Detail!);
            }

            var untrackedTar = Path.Combine(pack, "untracked.tar");
            if (!File.Exists(untrackedTar))
                return ContinuityOutcome.Failure(ContinuityReasons.PackInvalid, "untracked.tar missing");

            ExtractUntrackedTar(untrackedTar, dest);

            if (!string.IsNullOrEmpty(manifest.Branch))
            {
                // Branch is a hint only; ignore failure.
                _ = RunGit(dest, ["checkout", "-B", manifest.Branch]);
            }

            return ContinuityOutcome.Success();
        }
        catch (Exception ex)
        {
            return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceApplyFailed, ex.Message);
        }
    }

    public ContinuityOutcome CheckEquivalence(string packWorkspaceDir, string destWorkspacePath)
    {
        var pack = Path.GetFullPath(packWorkspaceDir);
        var dest = Path.GetFullPath(destWorkspacePath);
        var manifest = LoadManifest(pack);
        if (manifest is null)
            return ContinuityOutcome.Failure(ContinuityReasons.PackInvalid, "workspace-manifest.json missing");

        var head = RunGit(dest, ["rev-parse", "HEAD"]);
        if (!head.Ok)
            return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceMismatch, head.Detail!);
        if (!string.Equals(head.Stdout!.Trim(), manifest.Head, StringComparison.OrdinalIgnoreCase))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.WorkspaceMismatch,
                $"HEAD {head.Stdout.Trim()} != {manifest.Head}");
        }

        var diffBytes = RunGitBytes(dest, ["diff", "--binary", "HEAD"]);
        if (!diffBytes.Ok)
            return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceMismatch, diffBytes.Detail!);
        var trackedHash = Sha256Hex(diffBytes.Bytes!);
        if (!string.Equals(trackedHash, manifest.TrackedDiffSha256, StringComparison.OrdinalIgnoreCase))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.WorkspaceMismatch,
                "tracked diff sha256 mismatch");
        }

        var untracked = RunGit(dest, ["ls-files", "-o", "--exclude-standard", "-z"]);
        if (!untracked.Ok)
            return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceMismatch, untracked.Detail!);
        var paths = SplitNul(untracked.Stdout ?? "")
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        var expected = manifest.UntrackedPaths.OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (!paths.SequenceEqual(expected, StringComparer.Ordinal))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.WorkspaceMismatch,
                "untracked path set mismatch");
        }

        var tmpTar = Path.Combine(Path.GetTempPath(), "hypa-untracked-" + Guid.NewGuid().ToString("N") + ".tar");
        try
        {
            WriteUntrackedTar(dest, paths, tmpTar);
            var untrackedHash = Sha256Hex(File.ReadAllBytes(tmpTar));
            if (!string.Equals(untrackedHash, manifest.UntrackedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return ContinuityOutcome.Failure(
                    ContinuityReasons.WorkspaceMismatch,
                    "untracked.tar sha256 mismatch");
            }
        }
        finally
        {
            if (File.Exists(tmpTar))
                File.Delete(tmpTar);
        }

        return ContinuityOutcome.Success();
    }

    private ContinuityOutcome CheckPreconditions(string ws)
    {
        var layout = TryResolveWorktreeLayout(ws, out var worktreeGitDir, out _);
        if (!layout.Ok)
            return layout;

        var inside = RunGit(ws, ["rev-parse", "--is-inside-work-tree"]);
        if (!inside.Ok || !string.Equals(inside.Stdout!.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceUnsupported, "not a git work tree");

        if (File.Exists(Path.Combine(worktreeGitDir, "MERGE_HEAD"))
            || Directory.Exists(Path.Combine(worktreeGitDir, "rebase-merge"))
            || Directory.Exists(Path.Combine(worktreeGitDir, "rebase-apply"))
            || File.Exists(Path.Combine(worktreeGitDir, "CHERRY_PICK_HEAD"))
            || File.Exists(Path.Combine(worktreeGitDir, "REVERT_HEAD")))
        {
            return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceUnsupported, "merge/rebase in progress");
        }

        var unmerged = RunGit(ws, ["diff", "--name-only", "--diff-filter=U"]);
        if (!unmerged.Ok)
            return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceUnsupported, unmerged.Detail!);
        if (!string.IsNullOrWhiteSpace(unmerged.Stdout))
            return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceUnsupported, "unmerged paths present");

        var sub = RunGit(ws, ["submodule", "status"]);
        if (!sub.Ok)
            return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceUnsupported, sub.Detail!);
        if (!string.IsNullOrWhiteSpace(sub.Stdout))
            return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceUnsupported, "submodules present");

        if (HasGitLfs())
        {
            var lfs = RunGit(ws, ["lfs", "ls-files"]);
            if (!lfs.Ok)
                return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceUnsupported, lfs.Detail!);
            if (!string.IsNullOrWhiteSpace(lfs.Stdout))
                return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceUnsupported, "git lfs files present");
        }

        return ContinuityOutcome.Success();
    }

    /// <summary>
    /// Resolve the worktree git dir and common dir. A linked worktree stores
    /// <c>gitdir:</c> in a <c>.git</c> file. Merge state lives in the worktree
    /// git dir. Objects live in the common dir. Capture does not pack either dir.
    /// </summary>
    internal static ContinuityOutcome TryResolveWorktreeLayout(
        string workspacePath,
        out string worktreeGitDir,
        out string commonDir)
    {
        worktreeGitDir = "";
        commonDir = "";
        var gitEntry = Path.Combine(workspacePath, ".git");
        if (Directory.Exists(gitEntry))
        {
            worktreeGitDir = gitEntry;
            return TryResolveCommonDir(worktreeGitDir, out commonDir);
        }

        if (!File.Exists(gitEntry))
            return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceUnsupported, "not a git work tree");

        string text;
        try
        {
            text = File.ReadAllText(gitEntry);
        }
        catch (IOException ex)
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.WorkspaceUnsupported,
                "invalid gitdir pointer: " + ex.Message);
        }

        if (!TryParseGitDirPointer(text, out var pointer))
            return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceUnsupported, "invalid gitdir pointer");

        string resolvedPointer;
        try
        {
            resolvedPointer = ResolveAgainst(workspacePath, pointer);
        }
        catch (ArgumentException)
        {
            return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceUnsupported, "invalid gitdir pointer");
        }
        catch (NotSupportedException)
        {
            return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceUnsupported, "invalid gitdir pointer");
        }

        if (!Directory.Exists(resolvedPointer))
            return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceUnsupported, "gitdir pointer missing");

        worktreeGitDir = resolvedPointer;
        return TryResolveCommonDir(worktreeGitDir, out commonDir);
    }

    private static ContinuityOutcome TryResolveCommonDir(string worktreeGitDir, out string commonDir)
    {
        commonDir = "";
        var commondirFile = Path.Combine(worktreeGitDir, "commondir");
        if (!File.Exists(commondirFile))
        {
            commonDir = worktreeGitDir;
            return ContinuityOutcome.Success();
        }

        string commonRaw;
        try
        {
            commonRaw = File.ReadAllText(commondirFile).Trim();
        }
        catch (IOException ex)
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.WorkspaceUnsupported,
                "git common dir missing: " + ex.Message);
        }

        if (string.IsNullOrWhiteSpace(commonRaw))
            return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceUnsupported, "git common dir missing");

        string resolvedCommon;
        try
        {
            resolvedCommon = ResolveAgainst(worktreeGitDir, commonRaw);
        }
        catch (ArgumentException)
        {
            return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceUnsupported, "git common dir missing");
        }
        catch (NotSupportedException)
        {
            return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceUnsupported, "git common dir missing");
        }

        if (!Directory.Exists(resolvedCommon))
            return ContinuityOutcome.Failure(ContinuityReasons.WorkspaceUnsupported, "git common dir missing");

        commonDir = resolvedCommon;
        return ContinuityOutcome.Success();
    }

    /// <summary>
    /// Parse a gitfile. The first non-empty line must start with <c>gitdir:</c>.
    /// The remainder is the pointer. Relative pointers resolve from the worktree root.
    /// </summary>
    internal static bool TryParseGitDirPointer(string gitFileText, out string pointer)
    {
        pointer = "";
        if (string.IsNullOrWhiteSpace(gitFileText))
            return false;

        var text = gitFileText.TrimStart('\uFEFF');
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
                continue;
            const string prefix = "gitdir:";
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
                return false;
            var rest = line[prefix.Length..].Trim();
            if (rest.Length == 0)
                return false;
            pointer = rest;
            return true;
        }

        return false;
    }

    private static string ResolveAgainst(string baseDir, string path)
    {
        var trimmed = path.Trim();
        if (Path.IsPathRooted(trimmed))
            return Path.GetFullPath(trimmed);
        return Path.GetFullPath(Path.Combine(baseDir, trimmed));
    }

    private bool HasGitLfs()
    {
        try
        {
            var psi = CreateGitStartInfo(cwd: null, ["lfs", "version"]);
            using var p = Process.Start(psi);
            if (p is null)
                return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static WorkspaceManifestDto? LoadManifest(string packWorkspaceDir)
    {
        var path = Path.Combine(packWorkspaceDir, "workspace-manifest.json");
        if (!File.Exists(path))
            return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, WorkspaceManifestJsonContext.Default.WorkspaceManifestDto);
    }

    private static void WriteUntrackedTar(string workspaceRoot, IReadOnlyList<string> relativePaths, string tarPath)
    {
        using var fs = File.Create(tarPath);
        using var writer = new TarWriter(fs, TarEntryFormat.Ustar, leaveOpen: false);
        foreach (var rel in relativePaths.OrderBy(p => p, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(rel) || rel.Contains("..", StringComparison.Ordinal))
                continue;
            if (IsDeniedUntracked(workspaceRoot, rel) || IsOutsideWorkspaceSymlink(workspaceRoot, rel))
                continue;
            var full = Path.Combine(workspaceRoot, rel);
            if (IsSymbolicLink(full))
            {
                var rawTarget = new FileInfo(full).LinkTarget ?? "";
                if (!TryWorkspaceRelativeLinkTarget(workspaceRoot, full, rawTarget, out var linkName))
                    continue;
                var linkEntry = new UstarTarEntry(TarEntryType.SymbolicLink, rel.Replace('\\', '/'))
                {
                    LinkName = linkName,
                    ModificationTime = DateTimeOffset.UnixEpoch,
                };
                writer.WriteEntry(linkEntry);
                continue;
            }

            if (!File.Exists(full) && !Directory.Exists(full))
                continue;
            if (Directory.Exists(full) && !File.Exists(full))
            {
                // Skip empty directories; only files/symlinks.
                continue;
            }

            if (File.Exists(full))
            {
                using var stream = File.OpenRead(full);
                var entry = new UstarTarEntry(TarEntryType.RegularFile, rel.Replace('\\', '/'))
                {
                    DataStream = stream,
                    ModificationTime = DateTimeOffset.UnixEpoch,
                };
                if (!OperatingSystem.IsWindows())
                {
                    try
                    {
                        entry.Mode = File.GetUnixFileMode(full);
                    }
                    catch (PlatformNotSupportedException)
                    {
                    }
                }

                writer.WriteEntry(entry);
            }
        }
    }

    private static void ExtractUntrackedTar(string tarPath, string destRoot)
    {
        var dest = Path.GetFullPath(destRoot);
        using var fs = File.OpenRead(tarPath);
        using var reader = new TarReader(fs, leaveOpen: false);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile
                or TarEntryType.SymbolicLink))
            {
                continue;
            }

            var name = entry.Name.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains("..", StringComparison.Ordinal))
                throw new InvalidOperationException("unsafe tar path: " + name);
            if (IsDeniedUntrackedPath(name))
                continue;

            var target = Path.Combine(dest, name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (entry.EntryType == TarEntryType.SymbolicLink)
            {
                var linkName = entry.LinkName ?? "";
                if (!IsSafeInTreeLinkTarget(dest, target, linkName))
                    throw new InvalidOperationException("unsafe symlink target: " + linkName);
                if (File.Exists(target) || Directory.Exists(target))
                    continue;
                File.CreateSymbolicLink(target, linkName);
                continue;
            }

            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>
    /// Pack an in-tree symlink as a relative target. Absolute targets that resolve
    /// inside the work tree become relative. Outside or rooted escapes are refused.
    /// </summary>
    private static bool TryWorkspaceRelativeLinkTarget(
        string workspaceRoot,
        string linkPath,
        string rawTarget,
        out string relativeTarget)
    {
        relativeTarget = "";
        if (string.IsNullOrWhiteSpace(rawTarget))
            return false;

        var root = Path.GetFullPath(workspaceRoot);
        var linkDir = Path.GetDirectoryName(linkPath) ?? root;
        string resolved;
        try
        {
            resolved = Path.IsPathRooted(rawTarget)
                ? Path.GetFullPath(rawTarget)
                : Path.GetFullPath(Path.Combine(linkDir, rawTarget));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }

        if (IsPathOutside(root, resolved))
            return false;

        relativeTarget = Path.GetRelativePath(linkDir, resolved).Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(relativeTarget) || IsAbsoluteLinkTarget(relativeTarget))
            return false;
        return true;
    }

    /// <summary>
    /// Inner-tar symlink targets must be relative and resolve inside dest.
    /// Absolute targets fail closed even when they point at dest.
    /// </summary>
    private static bool IsSafeInTreeLinkTarget(string destRoot, string linkPath, string linkName)
    {
        if (string.IsNullOrWhiteSpace(linkName) || IsAbsoluteLinkTarget(linkName))
            return false;

        var linkDir = Path.GetDirectoryName(linkPath) ?? destRoot;
        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(linkDir, linkName));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }

        return !IsPathOutside(destRoot, resolved);
    }

    private static bool IsAbsoluteLinkTarget(string linkName)
    {
        if (string.IsNullOrWhiteSpace(linkName))
            return false;
        var normalized = linkName.Replace('\\', '/');
        return Path.IsPathRooted(linkName)
            || Path.IsPathRooted(normalized)
            || normalized.StartsWith('/');
    }

    /// <summary>
    /// Untracked secret deny-list: <c>.env</c>, <c>*.pem</c>, <c>credentials*</c>.
    /// Match the last path segment. Also refuse a symlink whose target uses that name.
    /// </summary>
    internal static bool IsDeniedUntracked(string workspaceRoot, string relativePath)
    {
        if (IsDeniedUntrackedPath(relativePath))
            return true;
        return IsDeniedUntrackedSymlinkTarget(workspaceRoot, relativePath);
    }

    /// <summary>
    /// Untracked secret deny-list: <c>.env</c>, <c>*.pem</c>, <c>credentials*</c>.
    /// Match the last path segment.
    /// </summary>
    internal static bool IsDeniedUntrackedPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;
        var name = Path.GetFileName(relativePath.Replace('\\', '/'));
        if (string.Equals(name, ".env", StringComparison.Ordinal))
            return true;
        if (name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.StartsWith("credentials", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    internal static bool IsDeniedUntrackedSymlinkTarget(string workspaceRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(relativePath))
            return false;
        if (relativePath.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
            return false;

        var full = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath));
        if (!IsSymbolicLink(full))
            return false;

        var raw = new FileInfo(full).LinkTarget;
        if (!string.IsNullOrEmpty(raw) && IsDeniedUntrackedPath(raw.Replace('\\', '/')))
            return true;

        FileSystemInfo? resolved;
        try
        {
            resolved = File.ResolveLinkTarget(full, returnFinalTarget: true);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        if (resolved is null)
            return false;
        return IsDeniedUntrackedPath(resolved.Name) || IsDeniedUntrackedPath(resolved.FullName);
    }

    /// <summary>
    /// Skip an untracked symlink whose resolved target is outside the work tree.
    /// Do not copy secret bytes from an outside file that is not deny-listed by name.
    /// </summary>
    internal static bool IsOutsideWorkspaceSymlink(string workspaceRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(relativePath))
            return false;
        if (relativePath.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
            return false;

        var full = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath));
        if (!IsSymbolicLink(full))
            return false;

        var root = Path.GetFullPath(workspaceRoot);
        var raw = new FileInfo(full).LinkTarget;
        if (!string.IsNullOrEmpty(raw))
        {
            var rawFull = Path.IsPathRooted(raw)
                ? Path.GetFullPath(raw)
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(full) ?? root, raw));
            if (IsPathOutside(root, rawFull))
                return true;
        }

        FileSystemInfo? resolved;
        try
        {
            resolved = File.ResolveLinkTarget(full, returnFinalTarget: true);
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }

        if (resolved is null)
            return true;
        return IsPathOutside(root, Path.GetFullPath(resolved.FullName));
    }

    private static bool IsPathOutside(string workspaceRoot, string candidate)
    {
        var relative = Path.GetRelativePath(workspaceRoot, candidate).Replace('\\', '/');
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative);
    }

    private static bool IsSymbolicLink(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static List<string> SplitNul(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return [];
        return raw.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Replace('\\', '/'))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
    }

    private static string Sha256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void PromotePackArtifacts(string staging, string dest)
    {
        foreach (var name in PackArtifactNames)
        {
            var from = Path.Combine(staging, name);
            if (!File.Exists(from))
                continue;
            File.Move(from, Path.Combine(dest, name), overwrite: true);
            _onPromotedArtifact?.Invoke(name);
        }
    }

    private static void BackupExistingPackArtifacts(string dest, string backup)
    {
        foreach (var name in PackArtifactNames)
        {
            var source = Path.Combine(dest, name);
            if (!File.Exists(source))
                continue;
            Directory.CreateDirectory(backup);
            File.Copy(source, Path.Combine(backup, name), overwrite: true);
        }
    }

    private void RestorePackArtifacts(string backup, string dest)
    {
        if (!Directory.Exists(dest))
            return;

        try
        {
            TryRestorePackArtifacts(backup, dest);
        }
        catch (Exception)
        {
            try
            {
                CompleteRestoreFromBackup(backup, dest);
            }
            catch (Exception completeEx)
            {
                throw new IOException("restore complete failed: " + completeEx.Message, completeEx);
            }

            throw;
        }
    }

    private void TryRestorePackArtifacts(string backup, string dest)
    {
        var staged = new List<(string TempPath, string DestPath, string Name)>();
        try
        {
            foreach (var name in PackArtifactNames)
            {
                var backupPath = Path.Combine(backup, name);
                if (!File.Exists(backupPath))
                    continue;
                var destPath = Path.Combine(dest, name);
                var tempPath = destPath + ".restore";
                File.Copy(backupPath, tempPath, overwrite: true);
                staged.Add((tempPath, destPath, name));
            }

            foreach (var (tempPath, destPath, name) in staged)
            {
                File.Move(tempPath, destPath, overwrite: true);
                _onRestoredArtifact?.Invoke(name);
            }

            foreach (var name in PackArtifactNames)
            {
                if (File.Exists(Path.Combine(backup, name)))
                    continue;
                RemovePackArtifact(Path.Combine(dest, name));
            }
        }
        finally
        {
            foreach (var (tempPath, _, _) in staged)
                TryDeleteFile(tempPath);
        }
    }

    private void CompleteRestoreFromBackup(string backup, string dest)
    {
        if (!Directory.Exists(dest))
            return;

        var staged = new List<string>();
        try
        {
            foreach (var name in PackArtifactNames)
            {
                var destPath = Path.Combine(dest, name);
                TryDeleteFile(destPath + ".restore");
                var completePath = destPath + ".complete";
                TryDeleteFile(completePath);

                var backupPath = Path.Combine(backup, name);
                if (!File.Exists(backupPath))
                {
                    RemovePackArtifact(destPath);
                    continue;
                }

                File.Copy(backupPath, completePath, overwrite: true);
                staged.Add(completePath);
                if (Directory.Exists(destPath))
                    RemovePackArtifact(destPath);
                File.Move(completePath, destPath, overwrite: true);
                _onCompleteRestoredArtifact?.Invoke(name);
            }
        }
        catch (Exception ex)
        {
            // A later complete copy can fail after one dest write. Stage backup
            // copies first so dest does not keep mixed promoted bytes.
            try
            {
                CopyBackupArtifactsOntoDest(backup, dest);
            }
            catch (Exception recoveryEx)
            {
                throw new IOException(ex.Message + "; recovery failed: " + recoveryEx.Message, recoveryEx);
            }

            throw;
        }
        finally
        {
            foreach (var path in staged)
                TryDeleteCompleteTemp(path);
            foreach (var name in PackArtifactNames)
            {
                var destPath = Path.Combine(dest, name);
                TryDeleteFile(destPath + ".restore");
                TryDeleteCompleteTemp(destPath + ".complete");
                TryDeleteCompleteTemp(destPath + ".recovery");
                TryDeleteCompleteTemp(destPath + ".frombackup");
                TryDeleteCompleteTemp(destPath + ".fallback");
                TryDeleteCompleteTemp(destPath + ".lastcopy");
                TryDeleteCompleteTemp(destPath + ".rollback");
                TryDeleteCompleteTemp(destPath + ".retry");
                TryDeleteCompleteTemp(destPath + ".recoverretry");
                TryDeleteCompleteTemp(destPath + ".finalretry");
            }
        }
    }

    private void CopyBackupArtifactsOntoDest(string backup, string dest)
    {
        var staged = new List<(string TempPath, string DestPath, string Name)>();
        var missing = new List<string>();
        try
        {
            foreach (var name in PackArtifactNames)
            {
                var destPath = Path.Combine(dest, name);
                var recoveryPath = destPath + ".recovery";
                TryDeleteFile(recoveryPath);

                var backupPath = Path.Combine(backup, name);
                if (!File.Exists(backupPath))
                {
                    missing.Add(destPath);
                    continue;
                }

                File.Copy(backupPath, recoveryPath, overwrite: true);
                staged.Add((recoveryPath, destPath, name));
                _onRecoveredArtifact?.Invoke(name);
            }

            foreach (var (tempPath, destPath, name) in staged)
            {
                if (Directory.Exists(destPath))
                    RemovePackArtifact(destPath);
                File.Move(tempPath, destPath, overwrite: true);
                _onRecoveredDestArtifact?.Invoke(name);
            }

            foreach (var destPath in missing)
                RemovePackArtifact(destPath);
        }
        catch (Exception destEx)
        {
            try
            {
                RestoreDestFromBackup(backup, dest);
            }
            catch (Exception restoreEx)
            {
                throw new IOException(destEx.Message + "; dest restore failed: " + restoreEx.Message, restoreEx);
            }

            throw;
        }
        finally
        {
            foreach (var (tempPath, _, _) in staged)
                TryDeleteCompleteTemp(tempPath);
            foreach (var name in PackArtifactNames)
            {
                var destPath = Path.Combine(dest, name);
                TryDeleteCompleteTemp(destPath + ".recovery");
                TryDeleteCompleteTemp(destPath + ".frombackup");
                TryDeleteCompleteTemp(destPath + ".fallback");
                TryDeleteCompleteTemp(destPath + ".lastcopy");
                TryDeleteCompleteTemp(destPath + ".rollback");
                TryDeleteCompleteTemp(destPath + ".retry");
                TryDeleteCompleteTemp(destPath + ".recoverretry");
                TryDeleteCompleteTemp(destPath + ".finalretry");
            }
        }
    }

    private void RestoreDestFromBackup(string backup, string dest)
    {
        var staged = new List<(string TempPath, string DestPath)>();
        var missing = new List<string>();
        try
        {
            foreach (var name in PackArtifactNames)
            {
                var destPath = Path.Combine(dest, name);
                var backupPath = Path.Combine(backup, name);
                var sidecar = destPath + ".recovery";
                var tempPath = destPath + ".frombackup";
                TryDeleteFile(tempPath);

                var source = File.Exists(sidecar)
                    ? sidecar
                    : File.Exists(backupPath) ? backupPath : null;
                if (source is null)
                {
                    missing.Add(destPath);
                    continue;
                }

                File.Copy(source, tempPath, overwrite: true);
                staged.Add((tempPath, destPath));
            }

            foreach (var (tempPath, destPath) in staged)
            {
                if (Directory.Exists(destPath))
                    RemovePackArtifact(destPath);
                File.Move(tempPath, destPath, overwrite: true);
            }

            foreach (var destPath in missing)
                RemovePackArtifact(destPath);
        }
        catch (Exception ex)
        {
            try
            {
                WriteBackupArtifactsOntoDest(backup, dest);
            }
            catch (Exception fallbackEx)
            {
                throw new IOException(ex.Message + "; dest restore fallback failed: " + fallbackEx.Message, fallbackEx);
            }

            throw;
        }
        finally
        {
            foreach (var (tempPath, _) in staged)
                TryDeleteCompleteTemp(tempPath);
            foreach (var name in PackArtifactNames)
            {
                var destPath = Path.Combine(dest, name);
                TryDeleteCompleteTemp(destPath + ".frombackup");
                TryDeleteCompleteTemp(destPath + ".fallback");
                TryDeleteCompleteTemp(destPath + ".lastcopy");
                TryDeleteCompleteTemp(destPath + ".rollback");
                TryDeleteCompleteTemp(destPath + ".retry");
                TryDeleteCompleteTemp(destPath + ".recoverretry");
                TryDeleteCompleteTemp(destPath + ".finalretry");
            }
        }
    }

    private void WriteBackupArtifactsOntoDest(string backup, string dest)
    {
        var staged = new List<(string TempPath, string DestPath, string Name)>();
        var missing = new List<string>();
        try
        {
            // Stage every fallback copy before any dest write.
            StageBackupSources(
                backup,
                dest,
                ".fallback",
                removeTempDirectory: false,
                staged,
                missing,
                _onFallbackCopiedArtifact);
            MoveStagedBackupSourcesOntoDest(staged, _onFallbackDestArtifact);
            foreach (var destPath in missing)
                RemovePackArtifact(destPath);
        }
        catch (Exception ex)
        {
            try
            {
                WriteBackupSourcesFromLastCopy(backup, dest);
            }
            catch (Exception lastEx)
            {
                throw new IOException(ex.Message + "; dest restore last copy failed: " + lastEx.Message, lastEx);
            }

            throw;
        }
        finally
        {
            foreach (var (tempPath, _, _) in staged)
                TryDeleteCompleteTemp(tempPath);
            foreach (var name in PackArtifactNames)
            {
                var destPath = Path.Combine(dest, name);
                TryDeleteCompleteTemp(destPath + ".fallback");
                TryDeleteCompleteTemp(destPath + ".lastcopy");
                TryDeleteCompleteTemp(destPath + ".rollback");
                TryDeleteCompleteTemp(destPath + ".retry");
                TryDeleteCompleteTemp(destPath + ".recoverretry");
                TryDeleteCompleteTemp(destPath + ".finalretry");
            }
        }
    }

    private void WriteBackupSourcesFromLastCopy(string backup, string dest)
    {
        var staged = new List<(string TempPath, string DestPath, string Name)>();
        var missing = new List<string>();
        try
        {
            StageBackupSources(
                backup,
                dest,
                ".lastcopy",
                removeTempDirectory: true,
                staged,
                missing,
                onCopied: null);
            MoveStagedBackupSourcesOntoDest(staged, _onLastCopyDestArtifact);
            foreach (var destPath in missing)
                RemovePackArtifact(destPath);
        }
        catch (Exception ex)
        {
            // A later last-copy dest write can fail after one dest write.
            // Restore dest from backup so dest does not keep mixed bytes.
            try
            {
                RollbackLastCopyDestFromBackup(backup, dest);
            }
            catch (Exception rollbackEx)
            {
                throw new IOException(ex.Message + "; dest restore last copy dest write failed: " + rollbackEx.Message, rollbackEx);
            }

            throw;
        }
        finally
        {
            foreach (var (tempPath, _, _) in staged)
                TryDeleteCompleteTemp(tempPath);
            foreach (var name in PackArtifactNames)
            {
                var destPath = Path.Combine(dest, name);
                TryDeleteCompleteTemp(destPath + ".lastcopy");
                TryDeleteCompleteTemp(destPath + ".rollback");
                TryDeleteCompleteTemp(destPath + ".retry");
                TryDeleteCompleteTemp(destPath + ".recoverretry");
                TryDeleteCompleteTemp(destPath + ".finalretry");
            }
        }
    }

    private void RollbackLastCopyDestFromBackup(string backup, string dest)
    {
        var staged = new List<(string TempPath, string DestPath, string Name)>();
        var missing = new List<string>();
        try
        {
            // Stage every rollback copy before any dest write.
            StageBackupSources(
                backup,
                dest,
                ".rollback",
                removeTempDirectory: true,
                staged,
                missing,
                onCopied: null);
            MoveStagedBackupSourcesOntoDest(staged, _onRollbackDestArtifact);
            foreach (var destPath in missing)
                RemovePackArtifact(destPath);
        }
        catch (Exception ex)
        {
            // A later rollback dest write can fail after one dest write.
            // Retry dest from backup so dest does not keep mixed bytes.
            try
            {
                RetryRollbackDestFromBackup(backup, dest);
            }
            catch (Exception retryEx)
            {
                throw new IOException(ex.Message + "; dest restore rollback dest write failed: " + retryEx.Message, retryEx);
            }

            throw;
        }
        finally
        {
            foreach (var (tempPath, _, _) in staged)
                TryDeleteCompleteTemp(tempPath);
            foreach (var name in PackArtifactNames)
            {
                var destPath = Path.Combine(dest, name);
                TryDeleteCompleteTemp(destPath + ".rollback");
                TryDeleteCompleteTemp(destPath + ".retry");
                TryDeleteCompleteTemp(destPath + ".recoverretry");
                TryDeleteCompleteTemp(destPath + ".finalretry");
            }
        }
    }

    private void RetryRollbackDestFromBackup(string backup, string dest)
    {
        var staged = new List<(string TempPath, string DestPath, string Name)>();
        var missing = new List<string>();
        try
        {
            StageBackupSources(
                backup,
                dest,
                ".retry",
                removeTempDirectory: true,
                staged,
                missing,
                onCopied: null);
            MoveStagedBackupSourcesOntoDest(staged, _onRetryDestArtifact);
            foreach (var destPath in missing)
                RemovePackArtifact(destPath);
        }
        catch (Exception ex)
        {
            // A later retry dest write can fail after one dest write.
            // Stage backup copies first so dest does not keep mixed bytes.
            try
            {
                RecoverRetryDestFromBackup(backup, dest);
            }
            catch (Exception recoverEx)
            {
                throw new IOException(ex.Message + "; dest restore retry dest write failed: " + recoverEx.Message, recoverEx);
            }

            throw;
        }
        finally
        {
            foreach (var (tempPath, _, _) in staged)
                TryDeleteCompleteTemp(tempPath);
            foreach (var name in PackArtifactNames)
            {
                var destPath = Path.Combine(dest, name);
                TryDeleteCompleteTemp(destPath + ".retry");
                TryDeleteCompleteTemp(destPath + ".recoverretry");
                TryDeleteCompleteTemp(destPath + ".finalretry");
            }
        }
    }

    private static void RecoverRetryDestFromBackup(string backup, string dest)
    {
        var staged = new List<(string TempPath, string DestPath, string Name)>();
        var missing = new List<string>();
        try
        {
            // Stage every retry-recovery copy before any dest write.
            StageBackupSources(
                backup,
                dest,
                ".recoverretry",
                removeTempDirectory: false,
                staged,
                missing,
                onCopied: null);
            MoveStagedBackupSourcesOntoDest(staged, onDest: null);
            foreach (var destPath in missing)
                RemovePackArtifact(destPath);
        }
        catch (Exception ex)
        {
            // A later recovery copy can fail after one retry dest write.
            // Restore dest from backup so dest does not keep mixed bytes.
            try
            {
                WriteFinalRetryDestFromBackup(backup, dest);
            }
            catch (Exception finalEx)
            {
                throw new IOException(ex.Message + "; dest restore retry recovery failed: " + finalEx.Message, finalEx);
            }

            throw;
        }
        finally
        {
            foreach (var (tempPath, _, _) in staged)
                TryDeleteCompleteTemp(tempPath);
            foreach (var name in PackArtifactNames)
            {
                var destPath = Path.Combine(dest, name);
                TryDeleteCompleteTemp(destPath + ".recoverretry");
                TryDeleteCompleteTemp(destPath + ".finalretry");
            }
        }
    }

    private static void WriteFinalRetryDestFromBackup(string backup, string dest)
    {
        var staged = new List<(string TempPath, string DestPath, string Name)>();
        var missing = new List<string>();
        try
        {
            StageBackupSources(
                backup,
                dest,
                ".finalretry",
                removeTempDirectory: true,
                staged,
                missing,
                onCopied: null);
            MoveStagedBackupSourcesOntoDest(staged, onDest: null);
            foreach (var destPath in missing)
                RemovePackArtifact(destPath);
        }
        finally
        {
            foreach (var (tempPath, _, _) in staged)
                TryDeleteCompleteTemp(tempPath);
            foreach (var name in PackArtifactNames)
                TryDeleteCompleteTemp(Path.Combine(dest, name) + ".finalretry");
        }
    }

    private static void StageBackupSources(
        string backup,
        string dest,
        string suffix,
        bool removeTempDirectory,
        List<(string TempPath, string DestPath, string Name)> staged,
        List<string> missing,
        Action<string>? onCopied)
    {
        foreach (var name in PackArtifactNames)
        {
            var destPath = Path.Combine(dest, name);
            var backupPath = Path.Combine(backup, name);
            var sidecar = destPath + ".recovery";
            var tempPath = destPath + suffix;
            if (removeTempDirectory)
                TryDeleteCompleteTemp(tempPath);
            else
                TryDeleteFile(tempPath);

            var source = File.Exists(sidecar)
                ? sidecar
                : File.Exists(backupPath) ? backupPath : null;
            if (source is null)
            {
                missing.Add(destPath);
                continue;
            }

            File.Copy(source, tempPath, overwrite: true);
            staged.Add((tempPath, destPath, name));
            onCopied?.Invoke(name);
        }
    }

    private static void MoveStagedBackupSourcesOntoDest(
        List<(string TempPath, string DestPath, string Name)> staged,
        Action<string>? onDest)
    {
        foreach (var (tempPath, destPath, name) in staged)
        {
            if (Directory.Exists(destPath))
                RemovePackArtifact(destPath);
            File.Move(tempPath, destPath, overwrite: true);
            onDest?.Invoke(name);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteCompleteTemp(string path)
    {
        TryDeleteFile(path);
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void RemovePackArtifact(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
        else if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    /// <summary>Remove a pack directory that this capture created.</summary>
    private static void TryDeletePackDirectory(string dest)
    {
        try
        {
            if (Directory.Exists(dest))
                Directory.Delete(dest, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record GitTextResult(bool Ok, string? Stdout, string? Detail);
    private sealed record GitBytesResult(bool Ok, byte[]? Bytes, string? Detail);

    private GitTextResult RunGit(string cwd, IReadOnlyList<string> args)
    {
        var psi = CreateGitStartInfo(cwd, args);
        using var p = Process.Start(psi);
        if (p is null)
            return new GitTextResult(false, null, "failed to start git");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            return new GitTextResult(false, stdout, stderr.Trim().Length > 0 ? stderr.Trim() : $"git exit {p.ExitCode}");
        return new GitTextResult(true, stdout, null);
    }

    private GitBytesResult RunGitBytes(string cwd, IReadOnlyList<string> args)
    {
        var psi = CreateGitStartInfo(cwd, args);
        using var p = Process.Start(psi);
        if (p is null)
            return new GitBytesResult(false, null, "failed to start git");
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            return new GitBytesResult(false, null, stderr.Trim().Length > 0 ? stderr.Trim() : $"git exit {p.ExitCode}");
        return new GitBytesResult(true, ms.ToArray(), null);
    }

    private ProcessStartInfo CreateGitStartInfo(string? cwd, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _gitFileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(cwd))
            psi.WorkingDirectory = cwd;
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        if (_gitEnvironment is not null)
        {
            foreach (var (key, value) in _gitEnvironment)
                psi.Environment[key] = value;
        }

        return psi;
    }
}
