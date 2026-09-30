using Hypa.Continuity.Application;
using Hypa.Continuity.Domain;

namespace Hypa.Continuity.Harnesses.Fake;

/// <summary>
/// Fake harness for CI: conversation id is a single file under the store.
/// File copy is StorePresence. HarnessReported needs dest start and a live occupant.
/// A WorkPack is not safe to share. Transcript bodies may still hold secrets.
/// </summary>
public sealed class FakeHarnessAdapter : IHarnessAdapter
{
    public const string Id = "fake";
    public const string ConversationFileName = "conversation_id";
    public const string VersionFileName = "version.txt";
    public const string StartedFileName = "started";

    /// <summary>Spec §2.2 capture path list relative to <c>.fake/agent</c>.</summary>
    public static readonly IReadOnlyList<string> CaptureRelativePathList =
    [
        VersionFileName,
        ConversationFileName,
    ];

    public string AdapterId => Id;

    internal IReadOnlyList<string> LastCopiedLockResidue { get; private set; } = [];

    internal int LastStrippedWrittenLockCount { get; private set; }

    public ContinuityOutcome ReadVersion(string home, out string? version)
    {
        version = null;
        var path = Path.Combine(AgentStoreRoot(home), VersionFileName);
        if (!File.Exists(path))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.HarnessVersionUnreadable,
                "version.txt missing");
        }

        var text = File.ReadAllText(path).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.HarnessVersionUnreadable,
                "version.txt empty");
        }

        version = text;
        return ContinuityOutcome.Success();
    }

    public string ReadVersionString(string home)
    {
        var path = Path.Combine(AgentStoreRoot(home), VersionFileName);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
    }

    public ContinuityOutcome Quiesce(HarnessRunContext run, TimeSpan timeout)
    {
        _ = timeout;
        if (run.OccupantStreaming)
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.NotQuiescent,
                "occupant still streaming");
        }

        var idPath = Path.Combine(AgentStoreRoot(run.Home), ConversationFileName);
        if (!File.Exists(idPath))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.NotQuiescent,
                "conversation id file missing");
        }

        var id = File.ReadAllText(idPath).Trim();
        if (!string.Equals(id, run.ConversationId, StringComparison.Ordinal))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.NotQuiescent,
                "conversation id mismatch at quiesce");
        }

        return ContinuityOutcome.Success();
    }

    public ContinuityOutcome CaptureStore(
        string home,
        string destStoreRoot,
        string sourceCwd,
        string conversationId)
    {
        _ = sourceCwd;
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

            var src = AgentStoreRoot(home);
            if (!Directory.Exists(src))
            {
                return ContinuityOutcome.Failure(
                    ContinuityReasons.Internal,
                    "fake store missing");
            }

            var idPath = Path.Combine(src, ConversationFileName);
            if (!File.Exists(idPath))
            {
                return ContinuityOutcome.Failure(
                    ContinuityReasons.Internal,
                    "conversation id file missing");
            }

            var packedId = File.ReadAllText(idPath).Trim();
            if (!string.Equals(packedId, conversationId, StringComparison.Ordinal))
            {
                return ContinuityOutcome.Failure(
                    ContinuityReasons.Internal,
                    "conversation id mismatch at capture");
            }

            var dest = Path.Combine(destStoreRoot, ".fake", "agent");
            Directory.CreateDirectory(dest);
            File.Copy(idPath, Path.Combine(dest, ConversationFileName), overwrite: true);

            var versionSrc = Path.Combine(src, VersionFileName);
            if (File.Exists(versionSrc) && !HarnessStoreFileRules.IsExcluded(VersionFileName))
            {
                File.Copy(versionSrc, Path.Combine(dest, VersionFileName), overwrite: true);
            }

            return ContinuityOutcome.Success();
        }
        catch (Exception ex)
        {
            return ContinuityOutcome.Failure(ContinuityReasons.Internal, ex.Message);
        }
    }

    public ContinuityOutcome RestoreStore(
        string destHome,
        string sourceStoreRoot,
        string destCwd,
        string conversationId)
    {
        _ = destCwd;
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

            var captured = Path.Combine(sourceStoreRoot, ".fake", "agent");
            if (!Directory.Exists(captured))
            {
                return ContinuityOutcome.Failure(
                    ContinuityReasons.StoreRestoreFailed,
                    "captured fake store missing");
            }

            var capturedIdPath = Path.Combine(captured, ConversationFileName);
            if (!File.Exists(capturedIdPath))
            {
                return ContinuityOutcome.Failure(
                    ContinuityReasons.StoreRestoreFailed,
                    "conversation id missing after restore");
            }

            var packedId = File.ReadAllText(capturedIdPath).Trim();
            if (!string.Equals(packedId, conversationId, StringComparison.Ordinal))
            {
                return ContinuityOutcome.Failure(
                    ContinuityReasons.StoreRestoreFailed,
                    $"packed id {packedId} != {conversationId}");
            }

            var dest = AgentStoreRoot(destHome);
            var destIdPath = Path.Combine(dest, ConversationFileName);
            if (File.Exists(destIdPath))
            {
                var destId = File.ReadAllText(destIdPath).Trim();
                if (!string.Equals(destId, packedId, StringComparison.Ordinal))
                {
                    return ContinuityOutcome.Failure(
                        ContinuityReasons.StoreRestoreFailed,
                        "dest conversation exists and is not this Work");
                }
            }

            Directory.CreateDirectory(dest);
            var written = new List<string>();
            if (!File.Exists(destIdPath))
            {
                File.Copy(capturedIdPath, destIdPath);
                written.Add(destIdPath);
            }

            var capturedVersion = Path.Combine(captured, VersionFileName);
            var destVersion = Path.Combine(dest, VersionFileName);
            if (File.Exists(capturedVersion) && !File.Exists(destVersion))
            {
                File.Copy(capturedVersion, destVersion);
                written.Add(destVersion);
            }

            var lockStart = written.Count;
            HarnessStoreFileRules.CopyLockResidue(captured, dest, written);
            LastCopiedLockResidue = written.Skip(lockStart).ToArray();
            LastStrippedWrittenLockCount = HarnessStoreFileRules.StripWrittenLocks(written);
            HarnessStoreFileRules.StripLockResidueIn(dest);
            ClearDestStartMarker(dest);
            return ContinuityOutcome.Success();
        }
        catch (Exception ex)
        {
            return ContinuityOutcome.Failure(ContinuityReasons.StoreRestoreFailed, ex.Message);
        }
    }

    public ContinuityOutcome BuildStartArgs(
        string cwd,
        string home,
        string conversationId,
        out HarnessStartArgs? args)
    {
        args = null;
        var probe = ProbeConversationId(home, cwd);
        if (!probe.Ok || !string.Equals(probe.ConversationId, conversationId, StringComparison.Ordinal))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.StartFailed,
                "fake dest conversation missing for resume");
        }

        args = new HarnessStartArgs
        {
            Argv = ["fake-harness", "--cwd", cwd, "--home", home, "--resume", conversationId],
            Env = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["FAKE_HOME"] = home,
                ["FAKE_CWD"] = cwd,
                ["HOME"] = home,
                ["HYPA_CUBE_HOME"] = home,
            },
        };
        return ContinuityOutcome.Success();
    }

    public ResumeProbeResult ProbeConversationId(string home, string cwd)
    {
        _ = cwd;
        var path = Path.Combine(AgentStoreRoot(home), ConversationFileName);
        if (!File.Exists(path))
        {
            return ResumeProbeResult.Fail(
                ContinuityReasons.ConversationMismatch,
                "conversation id file missing on dest");
        }

        var conversationId = File.ReadAllText(path).Trim();
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return ResumeProbeResult.Fail(
                ContinuityReasons.ConversationMismatch,
                "conversation id empty");
        }

        // File copy, including a planted started marker, stays StorePresence.
        return ResumeProbeResult.Found(conversationId, ResumeEvidence.StorePresence);
    }

    public async ValueTask<ResumeProbeResult> WaitForHarnessReportAsync(
        string home,
        string cwd,
        HarnessStartArgs startArgs,
        TimeSpan timeout,
        IDestOccupantLiveness? liveness = null,
        CancellationToken cancellationToken = default)
    {
        _ = startArgs;
        _ = timeout;
        var probe = ProbeConversationId(home, cwd);
        if (!probe.Ok)
            return probe;

        if (liveness is null)
        {
            return ResumeProbeResult.Fail(
                ContinuityReasons.ResumeUnproven,
                "fake dest process was not started",
                probe.Evidence);
        }

        var alive = await liveness.IsAliveAsync(cancellationToken).ConfigureAwait(false);
        if (!alive)
        {
            return ResumeProbeResult.Fail(
                ContinuityReasons.ResumeUnproven,
                "fake dest occupant is not alive",
                probe.Evidence);
        }

        if (!DestStartRan(home, probe.ConversationId!))
        {
            return ResumeProbeResult.Fail(
                ContinuityReasons.ResumeUnproven,
                "fake dest start path did not run",
                probe.Evidence);
        }

        return ResumeProbeResult.Found(probe.ConversationId!, ResumeEvidence.HarnessReported);
    }

    public async ValueTask<ResumeProbeResult> ProbeLiveOccupantAsync(
        LivePaneOccupant occupant,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(occupant);
        if (occupant.Liveness is null)
        {
            return ResumeProbeResult.Fail(
                ContinuityReasons.ResumeUnproven,
                "occupant process was not probed");
        }

        var startArgs = occupant.ResumeStartArgs;
        if (startArgs is null)
        {
            var built = BuildStartArgs(
                occupant.Cwd,
                occupant.Home,
                ProbeConversationId(occupant.Home, occupant.Cwd).ConversationId ?? "",
                out startArgs);
            if (!built.Ok || startArgs is null)
            {
                startArgs = new HarnessStartArgs { Argv = ["fake-harness"] };
            }
        }

        return await WaitForHarnessReportAsync(
                occupant.Home,
                occupant.Cwd,
                startArgs,
                timeout,
                occupant.Liveness,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Fake dest start path. File copy is not this path. The started file is not evidence.
    /// </summary>
    public ContinuityOutcome RunDestStart(string home)
    {
        try
        {
            var root = AgentStoreRoot(home);
            var idPath = Path.Combine(root, ConversationFileName);
            if (!File.Exists(idPath))
            {
                return ContinuityOutcome.Failure(
                    ContinuityReasons.StartFailed,
                    "fake dest conversation missing for dest start");
            }

            var conversationId = File.ReadAllText(idPath).Trim();
            if (string.IsNullOrWhiteSpace(conversationId))
            {
                return ContinuityOutcome.Failure(
                    ContinuityReasons.StartFailed,
                    "fake dest conversation empty for dest start");
            }

            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, StartedFileName), conversationId);
            return ContinuityOutcome.Success();
        }
        catch (Exception ex)
        {
            return ContinuityOutcome.Failure(ContinuityReasons.StartFailed, ex.Message);
        }
    }

    public static void SeedHome(string home, string conversationId, string version = "0.0.0")
    {
        var root = AgentStoreRoot(home);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ConversationFileName), conversationId);
        File.WriteAllText(Path.Combine(root, VersionFileName), version);
    }

    public static void SeedVersion(string home, string version)
    {
        var root = AgentStoreRoot(home);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, VersionFileName), version);
    }

    public static void SetConversationId(string home, string conversationId)
    {
        var root = AgentStoreRoot(home);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ConversationFileName), conversationId);
    }

    public static string AgentStoreRoot(string home) =>
        Path.Combine(home, ".fake", "agent");

    private static bool DestStartRan(string home, string conversationId)
    {
        var startedPath = Path.Combine(AgentStoreRoot(home), StartedFileName);
        if (!File.Exists(startedPath))
            return false;

        var reported = File.ReadAllText(startedPath).Trim();
        return string.Equals(reported, conversationId, StringComparison.Ordinal);
    }

    private static void ClearDestStartMarker(string root)
    {
        var started = Path.Combine(root, StartedFileName);
        if (File.Exists(started))
            File.Delete(started);
    }
}
