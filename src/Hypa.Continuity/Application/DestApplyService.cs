using Hypa.Continuity.Domain;

namespace Hypa.Continuity.Application;

/// <summary>
/// Destination worker. Pack verify, workspace apply, dest start, dest probe,
/// dest store bootstrap, dest Run n+1. Dest SQLite may be empty.
/// </summary>
public sealed class DestApplyService
{
    private readonly IWorkService _works;
    private readonly IWorkspacePacker _workspace;
    private readonly IWorkPackCodec _codec;

    public DestApplyService(
        IWorkService works,
        IWorkspacePacker workspace,
        IWorkPackCodec codec)
    {
        _works = works ?? throw new ArgumentNullException(nameof(works));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
    }

    public async ValueTask<DestApplyResult> ApplyPackAsync(
        string packPath,
        DestApplyRequest request,
        DestApplyHostContext host,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packPath);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(host.Harness);
        var attemptId = string.IsNullOrWhiteSpace(request.AttemptId)
            ? HandoffRelayFailure.NewAttemptId()
            : request.AttemptId.Trim();

        DestApplyResult Fail(string reason, string detail) =>
            DestApplyResult.Fail(attemptId, reason, detail);

        if (string.IsNullOrWhiteSpace(request.WorkId)
            || string.IsNullOrWhiteSpace(request.DestPlacementId)
            || string.IsNullOrWhiteSpace(host.DestHome)
            || string.IsNullOrWhiteSpace(host.DestWorkspace)
            || string.IsNullOrWhiteSpace(host.DestMuxEndpoint))
        {
            return Fail(ContinuityReasons.Internal, "dest apply request is incomplete");
        }

        string? destHomeFull = null;
        string? destWs = null;
        var destWasEmpty = false;
        Dictionary<string, byte[]>? destHomeSnapshot = null;
        var destTouched = false;

        void CleanupDest()
        {
            if (!destTouched)
                return;
            if (destWs is not null)
                TryRemoveEmptyDestThisRun(destWs, destWasEmpty);
            if (destHomeFull is not null && destHomeSnapshot is not null)
                TryRevertDestHome(destHomeFull, destHomeSnapshot);
        }

        try
        {
            using var extractDir = new TempDir("hypa-dest-apply-");
            var read = _codec.Read(packPath, extractDir.Path, out var manifest);
            if (!read.Ok || manifest is null)
                return Fail(read.Reason ?? ContinuityReasons.PackInvalid, read.Detail ?? "pack read failed");

            if (!string.Equals(manifest.WorkId, request.WorkId.Trim(), StringComparison.Ordinal))
                return Fail(ContinuityReasons.PackInvalid, "work_id does not match Work");

            if (!string.IsNullOrWhiteSpace(request.HarnessAdapterId)
                && !string.Equals(
                    manifest.Harness.AdapterId,
                    request.HarnessAdapterId.Trim(),
                    StringComparison.Ordinal))
            {
                return Fail(ContinuityReasons.PackInvalid, "harness.adapter_id does not match dest adapter");
            }

            if (!string.Equals(
                    manifest.Harness.AdapterId,
                    host.Harness.AdapterId,
                    StringComparison.Ordinal))
            {
                return Fail(ContinuityReasons.PackInvalid, "harness.adapter_id does not match dest adapter");
            }

            if (!WorkId.TryParse(manifest.WorkId, out var workId))
                return Fail(ContinuityReasons.PackInvalid, "work_id is invalid");
            var packSha = string.IsNullOrWhiteSpace(request.PackSha256)
                ? _codec.HashPackFile(packPath)
                : request.PackSha256.Trim();

            var gen = await _works.CheckDestPackGenerationAsync(
                    workId,
                    manifest.Generation,
                    manifest.Harness.AdapterId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!gen.Ok)
                return Fail(gen.Reason ?? ContinuityReasons.StalePack, gen.Detail ?? "stale pack");

            var imported = await _works.ImportWorkFromPackAsync(
                    new WorkRecord
                    {
                        Id = workId,
                        HarnessAdapterId = manifest.Harness.AdapterId,
                        CreatedAt = DateTimeOffset.TryParse(manifest.CreatedAt, out var created)
                            ? created
                            : DateTimeOffset.UtcNow,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!imported.Ok)
                return Fail(imported.Reason ?? ContinuityReasons.Internal, imported.Detail ?? "dest Work import failed");

            destHomeFull = Path.GetFullPath(host.DestHome);
            destWs = Path.GetFullPath(host.DestWorkspace);
            var destExisted = Directory.Exists(destWs);
            destWasEmpty = !destExisted
                || !Directory.EnumerateFileSystemEntries(destWs).Any();
            if (destExisted && !destWasEmpty)
            {
                return Fail(
                    ContinuityReasons.WorkspaceUnsupported,
                    "dest workspace is not empty; refuse wipe");
            }

            var destVersion = host.Harness.ReadVersion(host.DestHome, out var destVersionText);
            if (!destVersion.Ok)
                return Fail(destVersion.Reason ?? ContinuityReasons.HarnessVersionUnreadable, destVersion.Detail ?? "");
            if (!string.Equals(destVersionText, manifest.Harness.HarnessVersion, StringComparison.Ordinal))
            {
                return Fail(
                    ContinuityReasons.HarnessVersionIncompatible,
                    "dest harness version envelope mismatch");
            }

            destHomeSnapshot = SnapshotHomeFiles(destHomeFull);
            destTouched = true;

            var conversationId = manifest.Harness.ConversationId;
            var restore = host.Harness.RestoreStore(
                host.DestHome,
                Path.Combine(extractDir.Path, "harness", "store"),
                host.DestWorkspace,
                conversationId);
            if (!restore.Ok)
            {
                CleanupDest();
                return Fail(restore.Reason ?? ContinuityReasons.StoreRestoreFailed, restore.Detail ?? "");
            }

            Directory.CreateDirectory(destWs);
            var apply = _workspace.Apply(
                Path.Combine(extractDir.Path, "workspace"),
                destWs);
            if (!apply.Ok)
            {
                CleanupDest();
                return Fail(apply.Reason ?? ContinuityReasons.WorkspaceApplyFailed, apply.Detail ?? "");
            }

            var equiv = _workspace.CheckEquivalence(
                Path.Combine(extractDir.Path, "workspace"),
                destWs);
            if (!equiv.Ok)
            {
                CleanupDest();
                return Fail(equiv.Reason ?? ContinuityReasons.WorkspaceMismatch, equiv.Detail ?? "");
            }

            var startArgsOutcome = host.Harness.BuildStartArgs(
                destWs,
                host.DestHome,
                conversationId,
                out var startArgs);
            if (!startArgsOutcome.Ok || startArgs is null)
            {
                CleanupDest();
                return Fail(
                    startArgsOutcome.Reason ?? ContinuityReasons.StartFailed,
                    startArgsOutcome.Detail ?? "dest start args failed");
            }

            IDestOccupantLiveness? destLiveness = null;
            if (host.RequireDestStart)
            {
                if (host.DestStarter is null)
                {
                    CleanupDest();
                    return Fail(ContinuityReasons.StartFailed, "dest starter missing");
                }

                var destPaneId = !string.IsNullOrWhiteSpace(request.DestPaneId)
                    ? request.DestPaneId.Trim()
                    : host.DestPaneId;
                if (string.IsNullOrWhiteSpace(destPaneId))
                {
                    CleanupDest();
                    return Fail(ContinuityReasons.StartFailed, "dest pane_id required");
                }

                var started = await host.DestStarter.StartResumeAsync(
                        new DestOccupantStartRequest
                        {
                            MuxId = request.DestPlacementId,
                            SocketPath = StripUnixPrefix(host.DestMuxEndpoint),
                            Workspace = destWs,
                            CubeHome = host.DestHome,
                            HarnessId = host.Harness.AdapterId,
                            ResumeArgs = startArgs.Argv,
                            Env = startArgs.Env,
                            PaneId = destPaneId,
                            WorkId = workId.Value,
                            Generation = manifest.Generation + 1,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!started.Ok)
                {
                    CleanupDest();
                    return Fail(
                        ContinuityReasons.StartFailed,
                        started.Error ?? "dest start failed");
                }

                destLiveness = started.Liveness;
            }

            var destProbe = host.RequireDestStart
                ? await host.Harness.WaitForHarnessReportAsync(
                        host.DestHome,
                        destWs,
                        startArgs,
                        host.ResumeReportTimeout,
                        destLiveness,
                        cancellationToken)
                    .ConfigureAwait(false)
                : host.Harness.ProbeConversationId(host.DestHome, destWs);
            if (!destProbe.Ok)
            {
                CleanupDest();
                return Fail(
                    destProbe.Reason ?? ContinuityReasons.ResumeUnproven,
                    destProbe.Detail ?? "dest probe failed");
            }

            if (destProbe.Evidence < ResumeEvidence.HarnessReported)
            {
                CleanupDest();
                return Fail(
                    ContinuityReasons.ResumeUnproven,
                    $"dest evidence {destProbe.Evidence} is below {ResumeEvidence.HarnessReported}");
            }

            if (!string.Equals(destProbe.ConversationId, conversationId, StringComparison.Ordinal))
            {
                CleanupDest();
                return Fail(
                    ContinuityReasons.ConversationMismatch,
                    $"expected {conversationId} got {destProbe.ConversationId}");
            }

            var commit = await _works.CommitDestApplyAsync(
                    workId,
                    manifest.Generation,
                    request.DestPlacementId.Trim(),
                    host.DestMuxEndpoint.Trim(),
                    destProbe.Evidence,
                    attemptId,
                    packSha,
                    destHomeFull,
                    destWs,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!commit.Ok || commit.Run is null)
            {
                CleanupDest();
                return Fail(
                    commit.Reason ?? ContinuityReasons.SourceStillActive,
                    commit.Detail ?? "dest Run commit failed");
            }

            return new DestApplyResult
            {
                Ok = true,
                AttemptId = attemptId,
                DestGeneration = commit.Run.Generation,
                DestResumeEvidence = destProbe.Evidence,
                ConversationId = conversationId,
                DestMuxEndpoint = host.DestMuxEndpoint,
                DestPlacementId = request.DestPlacementId,
                WorkId = workId.Value,
            };
        }
        catch (Exception ex)
        {
            CleanupDest();
            return Fail(ContinuityReasons.Internal, ex.Message);
        }
    }

    private static string StripUnixPrefix(string endpoint)
    {
        const string prefix = "unix:";
        if (endpoint.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return endpoint[prefix.Length..];
        return endpoint;
    }

    private static void TryRemoveEmptyDestThisRun(string destWorkspace, bool destWasEmptyAtStart)
    {
        if (!destWasEmptyAtStart)
            return;
        try
        {
            if (Directory.Exists(destWorkspace))
                Directory.Delete(destWorkspace, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static Dictionary<string, byte[]> SnapshotHomeFiles(string home)
    {
        var snap = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!Directory.Exists(home))
            return snap;

        foreach (var file in Directory.EnumerateFiles(home, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(home, file).Replace('\\', '/');
            snap[rel] = File.ReadAllBytes(file);
        }

        return snap;
    }

    private static void TryRevertDestHome(string destHome, Dictionary<string, byte[]> snapshot)
    {
        try
        {
            if (!Directory.Exists(destHome))
                return;

            foreach (var file in Directory.GetFiles(destHome, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(destHome, file).Replace('\\', '/');
                if (!snapshot.ContainsKey(rel))
                    File.Delete(file);
                else if (!snapshot[rel].AsSpan().SequenceEqual(File.ReadAllBytes(file)))
                    File.WriteAllBytes(file, snapshot[rel]);
            }

            foreach (var (rel, bytes) in snapshot)
            {
                var path = Path.Combine(destHome, rel);
                if (File.Exists(path))
                    continue;
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
                File.WriteAllBytes(path, bytes);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir(string prefix)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
