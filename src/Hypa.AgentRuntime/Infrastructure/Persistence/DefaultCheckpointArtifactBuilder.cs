using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// artifact floor: always metadata JSON; optional scrollback, workspace walk, git probe.
/// Paths under the checkpoint directory are relative with posix separators for Atomic fakes.
/// </summary>
public sealed class DefaultCheckpointArtifactBuilder : ICheckpointArtifactBuilder
{
    private readonly IGitWorkspaceProbe _git;
    private readonly ICheckpointExportHooks _exportHooks;
    private readonly IWorkspaceWalkHooks _walkHooks;

    public DefaultCheckpointArtifactBuilder(
        IGitWorkspaceProbe? git = null,
        ICheckpointExportHooks? exportHooks = null,
        IWorkspaceWalkHooks? walkHooks = null)
    {
        _git = git ?? new StubGitWorkspaceProbe();
        _exportHooks = exportHooks ?? NoOpCheckpointExportHooks.Instance;
        _walkHooks = walkHooks ?? NoOpWorkspaceWalkHooks.Instance;
    }

    public bool TryPinProjectRoot(
        string? projectRoot,
        out ulong device,
        out ulong inode,
        out bool wasSymlink)
    {
        device = 0;
        inode = 0;
        wasSymlink = false;
        if (string.IsNullOrWhiteSpace(projectRoot))
            return false;
        return NoFollowWorkspaceWalker.TryPinWalkRoot(
            projectRoot, out device, out inode, out wasSymlink, out _, _walkHooks);
    }

    public async Task<RuntimeResult<CheckpointArtifactBuildResult>> BuildAsync(
        CheckpointArtifactBuildRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            await _exportHooks.DelayExportIoAsync(ct).ConfigureAwait(false);

            var artifacts = new List<CheckpointArtifactEntry>();
            var warnings = new List<string>();
            var incomplete = false;
            long totalBytes = 0;

            Directory.CreateDirectory(request.CheckpointDirectory);
            var artifactsDir = Path.Combine(request.CheckpointDirectory, "artifacts");
            Directory.CreateDirectory(artifactsDir);

            // 1) Always: session/workspace/tab/pane metadata (never credentials).
            var metaEntry = await WriteMetadataAsync(request, artifactsDir, ct).ConfigureAwait(false);
            artifacts.Add(metaEntry);
            totalBytes += metaEntry.ByteCount;

            // 2) Optional scrollback (bounded empty stub when requested — full scrollback segments are later).
            if (request.Record.IncludeScrollback)
            {
                var scrollRel = "artifacts/scrollback/README.txt";
                var scrollAbs = Path.Combine(request.CheckpointDirectory, "artifacts", "scrollback");
                Directory.CreateDirectory(scrollAbs);
                var scrollPath = Path.Combine(scrollAbs, "README.txt");
                var scrollText =
                    "Scrollback capture requested. Bounded text export is a future extension; " +
                    "pane metadata is present under artifacts/metadata.json.\n";
                var scrollBytes = Encoding.UTF8.GetBytes(scrollText);
                await File.WriteAllBytesAsync(scrollPath, scrollBytes, ct).ConfigureAwait(false);
                var scrollEntry = new CheckpointArtifactEntry
                {
                    RelativePath = scrollRel,
                    Sha256 = Sha256Hex(scrollBytes),
                    ByteCount = scrollBytes.LongLength,
                    Kind = CheckpointArtifactKinds.Scrollback,
                };
                artifacts.Add(scrollEntry);
                totalBytes += scrollEntry.ByteCount;
                warnings.Add("scrollback: bounded text files not fully implemented; metadata only");
                incomplete = true;
            }

            // 3) Optional workspace walk under project_root with excludes + budgets.
            // A prepared walk requires a pin. Never follow an unpinned symlink root.
            var projectRoot = ResolveProjectRoot(request.Session);
            if (request.Record.IncludeWorkspaceFiles)
            {
                if (string.IsNullOrWhiteSpace(projectRoot))
                {
                    warnings.Add("workspace: project_root missing or not a directory; skipped");
                    incomplete = true;
                }
                else if (CheckpointSecretFilePolicy.IsExactUnsafeRoot(
                             projectRoot,
                             Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
                {
                    warnings.Add("workspace: omit walk of unsafe project_root");
                    incomplete = true;
                }
                else if (!request.Record.HasProjectRootPin)
                {
                    warnings.Add("workspace: skipped; project_root pin missing");
                    incomplete = true;
                }
                else
                {
                    var walk = await WalkWorkspaceAsync(
                        projectRoot,
                        Path.Combine(request.CheckpointDirectory, "artifacts", "workspace"),
                        request.MaxWorkspaceBytes,
                        request.MaxFileBytes,
                        request.Record,
                        ct).ConfigureAwait(false);
                    artifacts.AddRange(walk.Entries);
                    totalBytes += walk.TotalBytes;
                    incomplete |= walk.Incomplete;
                    warnings.AddRange(walk.Warnings);
                }
            }

            // Git: re-open the pinned inode immediately before probe. Never trust a
            // stale AllowGitProbe or a followable Directory.Exists path string.
            var git = await TryAddGitMetaIfPinnedAsync(
                artifactsDir, artifacts, projectRoot, request.Record, ct).ConfigureAwait(false);
            incomplete |= git.Incomplete;
            totalBytes += git.Bytes;
            warnings.AddRange(git.Warnings);

            return RuntimeResult<CheckpointArtifactBuildResult>.Ok(new CheckpointArtifactBuildResult
            {
                Artifacts = artifacts,
                TransferIncomplete = incomplete,
                Warnings = warnings,
                TotalArtifactBytes = totalBytes,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            return RuntimeResult<CheckpointArtifactBuildResult>.Fail(
                RuntimePersistenceError.Io(ex.Message));
        }
    }

    private static async Task<CheckpointArtifactEntry> WriteMetadataAsync(
        CheckpointArtifactBuildRequest request,
        string artifactsDir,
        CancellationToken ct)
    {
        var session = request.Session;
        var dto = new CheckpointMetadataArtifactDto
        {
            CheckpointId = request.Record.CheckpointId,
            RuntimeSessionId = session.Id.Value,
            SessionName = session.Name,
            Placement = session.Placement,
            PlacementGeneration = session.PlacementGeneration,
            SessionFingerprint = request.Record.SessionFingerprint,
            BarrierSeq = request.Record.BarrierSeq,
            Binding = AtomicBindingStorageDto.FromDomain(session.Binding),
            Governed = session.Governed,
            ProtocolVersion = session.ProtocolVersion,
            ReplayComplete = session.ReplayComplete,
            Workspaces = session.Workspaces.Keys
                .OrderBy(k => k, StringComparer.Ordinal)
                .Select(k =>
                {
                    var ws = session.Workspaces[k];
                    return new CheckpointWorkspaceArtifactDto
                    {
                        WorkspaceId = ws.Id.Value,
                        Label = ws.Label,
                        Cwd = ws.Cwd,
                        Binding = AtomicBindingStorageDto.FromDomain(ws.Binding),
                    };
                })
                .ToList(),
            Panes = session.Panes.Keys
                .OrderBy(k => k, StringComparer.Ordinal)
                .Select(k =>
                {
                    var pane = session.Panes[k];
                    return new CheckpointPaneArtifactDto
                    {
                        PaneId = pane.Id.Value,
                        WorkspaceId = pane.WorkspaceId.Value,
                        TabId = pane.TabId.Value,
                        Label = pane.Label,
                        Cwd = pane.Cwd,
                        Command = pane.Command,
                        LifecycleState = pane.LifecycleState,
                        OccupantGeneration = pane.OccupantGeneration,
                        Binding = AtomicBindingStorageDto.FromDomain(pane.Binding),
                    };
                })
                .ToList(),
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            dto, RuntimeStorageJsonContext.Default.CheckpointMetadataArtifactDto);
        var path = Path.Combine(artifactsDir, "metadata.json");
        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
        return new CheckpointArtifactEntry
        {
            RelativePath = "artifacts/metadata.json",
            Sha256 = Sha256Hex(bytes),
            ByteCount = bytes.LongLength,
            Kind = CheckpointArtifactKinds.Metadata,
        };
    }

    private async Task<WorkspaceWalkResult> WalkWorkspaceAsync(
        string projectRoot,
        string destRoot,
        long maxWorkspaceBytes,
        long maxFileBytes,
        CheckpointRecord record,
        CancellationToken ct)
    {
        Directory.CreateDirectory(destRoot);
        var entries = new List<CheckpointArtifactEntry>();
        var warnings = new List<string>();
        var incomplete = false;
        long total = 0;

        var rootFull = Path.GetFullPath(projectRoot);
        var destRootFull = Path.GetFullPath(destRoot);
        var pin = PinFrom(record);

        // Fd-relative walk (Unix openat O_DIRECTORY|O_NOFOLLOW / Windows reparse-safe handles).
        // Holding an open directory fd and opening children by single-component name closes
        // intermediate-symlink TOCTOU: a path-string stack + EnumerateFileSystemEntries can
        // follow a directory swapped to a symlink after classification.
        if (!NoFollowWorkspaceWalker.TryOpenWalkRoot(rootFull, pin, out var rootHandle, out var rootDetail, _walkHooks))
        {
            warnings.Add("workspace: open project_root failed"
                + (string.IsNullOrEmpty(rootDetail) ? "" : ": " + rootDetail));
            return new WorkspaceWalkResult
            {
                Entries = entries,
                Warnings = warnings,
                Incomplete = true,
                TotalBytes = 0,
                AllowGitProbe = false,
            };
        }

        var allowGit = CanTrustPathForGit(rootFull, record, rootHandle);

        // Bind deny to inode identity before any allowed-name copy. A later rename
        // of .env/id_rsa onto safe.txt must not export those bytes.
        var deniedIdentities = new HashSet<NoFollowWorkspaceWalker.FileIdentity>();
        var dirs = new Stack<(Microsoft.Win32.SafeHandles.SafeFileHandle Handle, string Abs, string Rel)>();
        var rootOnStack = false;
        try
        {
            HarvestDeniedIdentities(
                rootHandle, rootFull, deniedIdentities, warnings, ref incomplete, ct);
            _exportHooks.AfterSecretDenyCheck(rootFull);

            // Stack of (open dir handle, absolute path for dest, posix relative path).
            // Depth-first: one open fd per stack frame (bounded by tree depth, not file count).
            // Name listing is fd-relative (fdopendir). Child opens use openat/O_NOFOLLOW.
            dirs.Push((rootHandle, rootFull, ""));
            rootOnStack = true;

            while (dirs.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (dirHandle, dirAbs, dirRel) = dirs.Pop();
                try
                {
                    IReadOnlyList<string> names;
                    try
                    {
                        names = NoFollowWorkspaceWalker.ListEntryNames(dirHandle, dirAbs, _walkHooks);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        warnings.Add("workspace: enumerate failed under "
                            + (string.IsNullOrEmpty(dirRel) ? "." : dirRel) + ": " + ex.Message);
                        incomplete = true;
                        continue;
                    }

                    var stopWalking = false;
                    foreach (var name in names)
                    {
                        ct.ThrowIfCancellationRequested();

                        if (!NoFollowWorkspaceWalker.IsSafeSingleName(name))
                        {
                            warnings.Add("workspace: skip unsafe name under "
                                + (string.IsNullOrEmpty(dirRel) ? "." : dirRel));
                            incomplete = true;
                            continue;
                        }

                        var childRel = string.IsNullOrEmpty(dirRel) ? name : dirRel + "/" + name;
                        if (IsUnsafeRelativePath(childRel))
                        {
                            warnings.Add("workspace: skip unsafe relative path " + childRel);
                            incomplete = true;
                            continue;
                        }

                        if (DefaultCheckpointExcludes.IsExcluded(childRel))
                            continue;

                        // Secret deny list is not silent: omit without copy and mark incomplete.
                        if (CheckpointSecretFilePolicy.IsDenied(childRel))
                        {
                            var deniedKind = NoFollowWorkspaceWalker.ClassifyEntry(
                                dirHandle, name, out var deniedId, out _);
                            if (deniedKind is NoFollowWorkspaceWalker.EntryKind.RegularFile
                                or NoFollowWorkspaceWalker.EntryKind.Directory)
                            {
                                HarvestDeniedIdentities(
                                    dirHandle, dirAbs, name, deniedKind, deniedId, deniedIdentities);
                            }

                            warnings.Add(CheckpointSecretFilePolicy.WarningFor(childRel));
                            incomplete = true;
                            continue;
                        }

                        var kind = NoFollowWorkspaceWalker.ClassifyEntry(
                            dirHandle, name, out var classifiedId, out var classDetail);

                        if (kind == NoFollowWorkspaceWalker.EntryKind.SymlinkOrReparse)
                        {
                            warnings.Add("workspace: skip symlink " + childRel
                                + (string.IsNullOrEmpty(classDetail) ? "" : " (" + classDetail + ")"));
                            incomplete = true;
                            continue;
                        }

                        if (kind == NoFollowWorkspaceWalker.EntryKind.OtherOrUnavailable)
                        {
                            var special = !string.IsNullOrEmpty(classDetail)
                                && classDetail.Contains("not a regular file", StringComparison.Ordinal);
                            warnings.Add(
                                (special
                                    ? "workspace: skip special file "
                                    : "workspace: skip unreadable entry ")
                                + childRel
                                + (string.IsNullOrEmpty(classDetail) ? "" : ": " + classDetail));
                            incomplete = true;
                            continue;
                        }

                        if ((kind is NoFollowWorkspaceWalker.EntryKind.RegularFile
                                or NoFollowWorkspaceWalker.EntryKind.Directory)
                            && classifiedId != default
                            && deniedIdentities.Contains(classifiedId))
                        {
                            warnings.Add(CheckpointSecretFilePolicy.WarningFor(childRel));
                            incomplete = true;
                            continue;
                        }

                        if (kind == NoFollowWorkspaceWalker.EntryKind.Directory)
                        {
                            if (!NoFollowWorkspaceWalker.TryOpenSubdirectory(
                                    dirHandle, name, out var childDir, out var openDirDetail))
                            {
                                // Directory became a symlink/reparse between classify and open.
                                warnings.Add("workspace: skip symlink " + childRel
                                    + (string.IsNullOrEmpty(openDirDetail)
                                        ? ""
                                        : " (" + openDirDetail + ")"));
                                incomplete = true;
                                continue;
                            }

                            var childAbs = Path.Combine(dirAbs, name);
                            dirs.Push((childDir, childAbs, childRel));
                            continue;
                        }

                        // Regular file: openat(O_NOFOLLOW) / OPEN_REPARSE_POINT relative to dir fd.
                        // Stat size before read; hard-cap so a racing grow cannot exceed the budget.
                        // Read must reopen the same inode that passed classify/deny.
                        var remaining = maxWorkspaceBytes - total;
                        if (remaining <= 0)
                        {
                            warnings.Add(
                                "workspace: max_workspace_bytes budget reached; remaining files skipped");
                            incomplete = true;
                            stopWalking = true;
                            break;
                        }

                        var cap = Math.Min(maxFileBytes, remaining);
                        NoFollowFileReader.OpenStatus openStatus;
                        byte[] bytes;
                        long inodeSize;
                        NoFollowWorkspaceWalker.FileIdentity readId;
                        string? openDetail;
                        try
                        {
                            openStatus = NoFollowWorkspaceWalker.TryReadFile(
                                dirHandle,
                                name,
                                cap,
                                ct,
                                classifiedId,
                                out bytes,
                                out inodeSize,
                                out readId,
                                out openDetail);
                        }
                        catch (OutOfMemoryException)
                        {
                            warnings.Add("workspace: memory limit while reading " + childRel);
                            incomplete = true;
                            stopWalking = true;
                            break;
                        }

                        if (openStatus == NoFollowFileReader.OpenStatus.IdentityMismatch
                            || (readId != default && deniedIdentities.Contains(readId)))
                        {
                            warnings.Add(
                                openStatus == NoFollowFileReader.OpenStatus.IdentityMismatch
                                    ? CheckpointSecretFilePolicy.WarningForReplaced(childRel)
                                    : CheckpointSecretFilePolicy.WarningFor(childRel));
                            incomplete = true;
                            continue;
                        }

                        if (openStatus == NoFollowFileReader.OpenStatus.SymlinkOrReparse)
                        {
                            warnings.Add("workspace: skip symlink " + childRel
                                + (string.IsNullOrEmpty(openDetail) ? "" : " (" + openDetail + ")"));
                            incomplete = true;
                            continue;
                        }

                        if (openStatus == NoFollowFileReader.OpenStatus.NotRegular)
                        {
                            warnings.Add("workspace: skip special file " + childRel
                                + (string.IsNullOrEmpty(openDetail) ? "" : " (" + openDetail + ")"));
                            incomplete = true;
                            continue;
                        }

                        if (openStatus == NoFollowFileReader.OpenStatus.Oversize)
                        {
                            if (inodeSize > maxFileBytes)
                            {
                                warnings.Add(
                                    $"workspace: skip oversize file {childRel} ({inodeSize} bytes)");
                            }
                            else
                            {
                                warnings.Add(
                                    "workspace: max_workspace_bytes budget reached; remaining files skipped");
                                stopWalking = true;
                            }

                            incomplete = true;
                            continue;
                        }

                        if (openStatus != NoFollowFileReader.OpenStatus.Ok)
                        {
                            warnings.Add("workspace: read failed " + childRel
                                + (string.IsNullOrEmpty(openDetail) ? "" : ": " + openDetail));
                            incomplete = true;
                            continue;
                        }

                        if (bytes.LongLength > maxFileBytes || inodeSize > maxFileBytes)
                        {
                            warnings.Add(
                                $"workspace: skip oversize file {childRel} ({inodeSize} bytes)");
                            incomplete = true;
                            continue;
                        }

                        if (total + bytes.LongLength > maxWorkspaceBytes)
                        {
                            warnings.Add(
                                "workspace: max_workspace_bytes budget reached; remaining files skipped");
                            incomplete = true;
                            stopWalking = true;
                            break;
                        }

                        var destRel = "artifacts/workspace/" + childRel;
                        var destAbs = Path.GetFullPath(
                            Path.Combine(
                                destRootFull,
                                childRel.Replace('/', Path.DirectorySeparatorChar)));
                        if (!IsPathInsideRoot(destRootFull, destAbs))
                        {
                            warnings.Add("workspace: skip dest escape " + childRel);
                            incomplete = true;
                            continue;
                        }

                        var destDir = Path.GetDirectoryName(destAbs);
                        if (!string.IsNullOrEmpty(destDir))
                            Directory.CreateDirectory(destDir);
                        await File.WriteAllBytesAsync(destAbs, bytes, ct).ConfigureAwait(false);

                        entries.Add(new CheckpointArtifactEntry
                        {
                            RelativePath = destRel,
                            Sha256 = Sha256Hex(bytes),
                            ByteCount = bytes.LongLength,
                            Kind = CheckpointArtifactKinds.WorkspaceFile,
                        });
                        total += bytes.LongLength;
                    }

                    if (stopWalking)
                    {
                        // Drain remaining open handles without processing.
                        while (dirs.Count > 0)
                        {
                            var (h, _, _) = dirs.Pop();
                            h.Dispose();
                        }
                    }
                }
                finally
                {
                    dirHandle.Dispose();
                }
            }
        }
        finally
        {
            if (!rootOnStack)
                rootHandle.Dispose();
            while (dirs.Count > 0)
            {
                var (h, _, _) = dirs.Pop();
                h.Dispose();
            }
        }

        return new WorkspaceWalkResult
        {
            Entries = entries,
            Warnings = warnings,
            Incomplete = incomplete,
            TotalBytes = total,
            AllowGitProbe = allowGit,
        };
    }

    private void HarvestDeniedIdentities(
        Microsoft.Win32.SafeHandles.SafeFileHandle rootHandle,
        string rootAbs,
        HashSet<NoFollowWorkspaceWalker.FileIdentity> denied,
        List<string> warnings,
        ref bool incomplete,
        CancellationToken ct)
    {
        var dirs = new Stack<(
            Microsoft.Win32.SafeHandles.SafeFileHandle Handle,
            string Abs,
            string Rel,
            bool Owns)>();
        dirs.Push((rootHandle, rootAbs, "", false));
        try
        {
            while (dirs.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (dirHandle, dirAbs, dirRel, owns) = dirs.Pop();
                try
                {
                    IReadOnlyList<string> names;
                    try
                    {
                        names = NoFollowWorkspaceWalker.ListEntryNames(dirHandle, dirAbs, _walkHooks);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        warnings.Add("workspace: enumerate failed under "
                            + (string.IsNullOrEmpty(dirRel) ? "." : dirRel) + ": " + ex.Message);
                        incomplete = true;
                        continue;
                    }

                    foreach (var name in names)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!NoFollowWorkspaceWalker.IsSafeSingleName(name))
                            continue;

                        var childRel = string.IsNullOrEmpty(dirRel) ? name : dirRel + "/" + name;
                        if (IsUnsafeRelativePath(childRel))
                            continue;
                        if (DefaultCheckpointExcludes.IsExcluded(childRel))
                            continue;

                        var deniedByName = CheckpointSecretFilePolicy.IsDenied(childRel);
                        var kind = NoFollowWorkspaceWalker.ClassifyEntry(
                            dirHandle, name, out var id, out _);

                        if (deniedByName)
                        {
                            if (kind is NoFollowWorkspaceWalker.EntryKind.RegularFile
                                or NoFollowWorkspaceWalker.EntryKind.Directory)
                            {
                                HarvestDeniedIdentities(
                                    dirHandle, dirAbs, name, kind, id, denied);
                            }

                            warnings.Add(CheckpointSecretFilePolicy.WarningFor(childRel));
                            incomplete = true;
                            continue;
                        }

                        if (kind == NoFollowWorkspaceWalker.EntryKind.Directory
                            && NoFollowWorkspaceWalker.TryOpenSubdirectory(
                                dirHandle, name, out var childDir, out _))
                        {
                            dirs.Push((childDir, Path.Combine(dirAbs, name), childRel, true));
                        }
                    }
                }
                finally
                {
                    if (owns)
                        dirHandle.Dispose();
                }
            }
        }
        finally
        {
            while (dirs.Count > 0)
            {
                var (h, _, _, owns) = dirs.Pop();
                if (owns)
                    h.Dispose();
            }
        }
    }

    private void HarvestDeniedIdentities(
        Microsoft.Win32.SafeHandles.SafeFileHandle parentDir,
        string parentAbs,
        string name,
        NoFollowWorkspaceWalker.EntryKind kind,
        NoFollowWorkspaceWalker.FileIdentity identity,
        HashSet<NoFollowWorkspaceWalker.FileIdentity> denied)
    {
        if (identity != default)
            denied.Add(identity);

        if (kind != NoFollowWorkspaceWalker.EntryKind.Directory)
            return;

        if (!NoFollowWorkspaceWalker.TryOpenSubdirectory(parentDir, name, out var childDir, out _))
            return;

        var childAbs = Path.Combine(parentAbs, name);
        try
        {
            IReadOnlyList<string> names;
            try
            {
                names = NoFollowWorkspaceWalker.ListEntryNames(childDir, childAbs, _walkHooks);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            foreach (var childName in names)
            {
                if (!NoFollowWorkspaceWalker.IsSafeSingleName(childName))
                    continue;

                var childKind = NoFollowWorkspaceWalker.ClassifyEntry(
                    childDir, childName, out var childId, out _);
                if (childKind is NoFollowWorkspaceWalker.EntryKind.RegularFile
                    or NoFollowWorkspaceWalker.EntryKind.Directory)
                {
                    HarvestDeniedIdentities(
                        childDir, childAbs, childName, childKind, childId, denied);
                }
            }
        }
        finally
        {
            childDir.Dispose();
        }
    }

    private static NoFollowWorkspaceWalker.ProjectRootPin? PinFrom(CheckpointRecord record)
    {
        if (record.ProjectRootDevice is ulong dev
            && record.ProjectRootInode is ulong ino
            && record.ProjectRootWasSymlink is bool was)
        {
            return new NoFollowWorkspaceWalker.ProjectRootPin(
                new NoFollowWorkspaceWalker.FileIdentity(dev, ino), was);
        }

        return null;
    }

    /// <summary>
    /// True when git may run against <paramref name="path"/> without following a swapped root.
    /// Pin fields are required. Missing pin is fail-closed.
    /// </summary>
    internal static bool CanTrustPathForGit(
        string? path,
        CheckpointRecord record,
        Microsoft.Win32.SafeHandles.SafeFileHandle? openedHandle)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        if (!record.HasProjectRootPin)
            return false;
        if (record.ProjectRootDevice is not ulong pdev || record.ProjectRootInode is not ulong pino)
            return false;

        if (NoFollowWorkspaceWalker.IsKernelFdDirectoryPath(path))
        {
            if (openedHandle is null)
                return false;
            if (!NoFollowWorkspaceWalker.TryFstatIdentity(openedHandle, out var fdId, out _))
                return false;
            return fdId.Device == pdev && fdId.Inode == pino;
        }

        if (!NoFollowWorkspaceWalker.TryClassifyWalkRoot(path, out var isLink, out _))
            return false;

        if (record.ProjectRootWasSymlink == false && isLink)
            return false;

        if (openedHandle is not null)
        {
            if (!NoFollowWorkspaceWalker.TryFstatIdentity(openedHandle, out var openedId, out _))
                return false;
            if (openedId.Device != pdev || openedId.Inode != pino)
                return false;

            if (isLink)
            {
                if (record.ProjectRootWasSymlink != true)
                    return false;
                if (!NoFollowWorkspaceWalker.TryResolveFinalDirectory(path, out var resolved, out _))
                    return false;
                if (!NoFollowWorkspaceWalker.TryLstatIdentity(resolved, out var target, out var targetLink, out _))
                    return false;
                if (targetLink)
                    return false;
                return target.Device == openedId.Device && target.Inode == openedId.Inode;
            }

            if (!NoFollowWorkspaceWalker.TryLstatIdentity(path, out var now, out var nowLink, out _))
                return false;
            if (nowLink)
                return false;
            return now.Device == openedId.Device && now.Inode == openedId.Inode;
        }

        if (isLink)
            return false;
        if (!NoFollowWorkspaceWalker.TryLstatIdentity(path, out var id, out var linkNow, out _))
            return false;
        if (linkNow)
            return false;
        return id.Device == pdev && id.Inode == pino;
    }

    private async Task<(bool Incomplete, long Bytes, IReadOnlyList<string> Warnings)> TryAddGitMetaIfPinnedAsync(
        string artifactsDir,
        List<CheckpointArtifactEntry> artifacts,
        string? projectRoot,
        CheckpointRecord record,
        CancellationToken ct)
    {
        var warnings = new List<string>();
        _exportHooks.BeforeGitProbe();

        // No project_root: git is not expected. Every other skip is incomplete.
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            if (record.HasProjectRootPin)
            {
                warnings.Add("git: skipped; project_root path missing after pin");
                return (true, 0, warnings);
            }

            return (false, 0, warnings);
        }

        if (!record.HasProjectRootPin)
        {
            warnings.Add("git: skipped; project_root pin missing");
            return (true, 0, warnings);
        }

        var pin = PinFrom(record);
        if (pin is null)
        {
            warnings.Add("git: skipped; project_root pin missing");
            return (true, 0, warnings);
        }

        if (!NoFollowWorkspaceWalker.TryOpenWalkRoot(
                Path.GetFullPath(projectRoot), pin, out var handle, out var openDetail, _walkHooks))
        {
            warnings.Add("git: skipped; project_root is not the prepared inode"
                + (string.IsNullOrEmpty(openDetail) ? "" : ": " + openDetail));
            return (true, 0, warnings);
        }

        try
        {
            // chdir follows a final-component symlink. Keep the pinned directory
            // fd open. Linux uses /proc/self/fd/N as cwd. macOS fchdir that fd
            // in the child — /dev/fd/N is ENOTDIR.
            string probePath;
            string? pathDetail = null;
            if (NoFollowWorkspaceWalker.TryFormatKernelFdDirectoryPath(
                    handle, out var fdPath, out pathDetail))
            {
                probePath = fdPath;
            }
            else if (OperatingSystem.IsWindows()
                && NoFollowWorkspaceWalker.TryGetVerifiedDirectoryPath(
                    handle, projectRoot, pin, out var winPath, out pathDetail))
            {
                probePath = winPath;
            }
            else
            {
                warnings.Add("git: skipped; pinned directory fd path unavailable"
                    + (string.IsNullOrEmpty(pathDetail) ? "" : ": " + pathDetail));
                return (true, 0, warnings);
            }

            _exportHooks.AfterGitProbePathPublished(probePath);

            if (!CanTrustPathForGit(probePath, record, handle))
            {
                warnings.Add("git: skipped; project_root is not the prepared inode");
                return (true, 0, warnings);
            }

            var written = await AddGitMetaAsync(
                artifactsDir,
                artifacts,
                probePath,
                pin.Value.Identity.Device,
                pin.Value.Identity.Inode,
                ct).ConfigureAwait(false);
            warnings.AddRange(written.Warnings);

            // Post-exit: held fd must still be the pin. Discard HEAD otherwise.
            if (!NoFollowWorkspaceWalker.TryFstatIdentity(handle, out var afterId, out _)
                || afterId.Device != pin.Value.Identity.Device
                || afterId.Inode != pin.Value.Identity.Inode)
            {
                warnings.Add("git: probe cwd is not the prepared inode");
                var bytes = RewriteGitMetaDiscardingHead(artifactsDir, artifacts);
                return (true, bytes, warnings);
            }

            return (written.Incomplete, written.Bytes, warnings);
        }
        finally
        {
            handle.Dispose();
        }
    }

    private async Task<(bool Incomplete, long Bytes, IReadOnlyList<string> Warnings)> AddGitMetaAsync(
        string artifactsDir,
        List<CheckpointArtifactEntry> artifacts,
        string projectRoot,
        ulong expectedDevice,
        ulong expectedInode,
        CancellationToken ct)
    {
        var warnings = new List<string>();
        var git = await _git.ProbeAsync(
            projectRoot, ct, expectedDevice, expectedInode).ConfigureAwait(false);
        if (!git.IsOk)
        {
            warnings.Add("git: probe failed: " + git.Error.Message);
            return (true, 0, warnings);
        }

        var incomplete = git.Value.Incomplete;
        if (!string.IsNullOrWhiteSpace(git.Value.Warning))
            warnings.Add("git: " + git.Value.Warning);

        var gitBytes = SerializeGitMeta(new CheckpointGitMetaArtifactDto
        {
            Head = git.Value.Head,
            Dirty = git.Value.Dirty,
            Incomplete = git.Value.Incomplete,
        });
        var gitPath = Path.Combine(artifactsDir, "git_meta.json");
        await File.WriteAllBytesAsync(gitPath, gitBytes, ct).ConfigureAwait(false);
        var gitEntry = new CheckpointArtifactEntry
        {
            RelativePath = "artifacts/git_meta.json",
            Sha256 = Sha256Hex(gitBytes),
            ByteCount = gitBytes.LongLength,
            Kind = CheckpointArtifactKinds.GitMeta,
        };
        artifacts.Add(gitEntry);
        return (incomplete, gitEntry.ByteCount, warnings);
    }

    private static long RewriteGitMetaDiscardingHead(
        string artifactsDir, List<CheckpointArtifactEntry> artifacts)
    {
        var gitBytes = SerializeGitMeta(new CheckpointGitMetaArtifactDto
        {
            Head = null,
            Dirty = null,
            Incomplete = true,
        });
        var gitPath = Path.Combine(artifactsDir, "git_meta.json");
        File.WriteAllBytes(gitPath, gitBytes);

        artifacts.RemoveAll(a =>
            string.Equals(a.RelativePath, "artifacts/git_meta.json", StringComparison.Ordinal));
        artifacts.Add(new CheckpointArtifactEntry
        {
            RelativePath = "artifacts/git_meta.json",
            Sha256 = Sha256Hex(gitBytes),
            ByteCount = gitBytes.LongLength,
            Kind = CheckpointArtifactKinds.GitMeta,
        });
        return gitBytes.LongLength;
    }

    /// <summary>
    /// True when <paramref name="candidateFull"/> is <paramref name="rootFull"/> or a
    /// descendant (resolved full paths; separator-safe).
    /// </summary>
    internal static bool IsPathInsideRoot(string rootFull, string candidateFull)
    {
        if (string.Equals(rootFull, candidateFull, StringComparison.Ordinal))
            return true;

        var root = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        return candidateFull.StartsWith(root, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reject relative paths that escape via <c>..</c>, are rooted, or are empty/current-dir.
    /// </summary>
    internal static bool IsUnsafeRelativePath(string relativePosix)
    {
        if (string.IsNullOrWhiteSpace(relativePosix))
            return true;
        if (relativePosix is "." or "..")
            return true;
        if (Path.IsPathRooted(relativePosix))
            return true;
        if (relativePosix.StartsWith("/", StringComparison.Ordinal)
            || relativePosix.StartsWith("\\", StringComparison.Ordinal))
            return true;

        var parts = relativePosix.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part is ".." or ".")
                return true;
        }

        return false;
    }

    private static string SafeRel(string rootFull, string path)
    {
        try
        {
            return Path.GetRelativePath(rootFull, path).Replace('\\', '/');
        }
        catch
        {
            return path;
        }
    }

    private static string? ResolveProjectRoot(SessionState session)
    {
        if (!string.IsNullOrWhiteSpace(session.Binding?.ProjectRoot))
            return session.Binding!.ProjectRoot;
        foreach (var ws in session.Workspaces.Values)
        {
            if (!string.IsNullOrWhiteSpace(ws.Binding?.ProjectRoot))
                return ws.Binding!.ProjectRoot;
            if (!string.IsNullOrWhiteSpace(ws.Cwd))
                return ws.Cwd;
        }

        return null;
    }

    private static byte[] SerializeGitMeta(CheckpointGitMetaArtifactDto dto) =>
        JsonSerializer.SerializeToUtf8Bytes(
            dto, RuntimeStorageJsonContext.Default.CheckpointGitMetaArtifactDto);

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record WorkspaceWalkResult
    {
        public required IReadOnlyList<CheckpointArtifactEntry> Entries { get; init; }
        public required IReadOnlyList<string> Warnings { get; init; }
        public bool Incomplete { get; init; }
        public long TotalBytes { get; init; }
        public bool AllowGitProbe { get; init; }
    }
}
