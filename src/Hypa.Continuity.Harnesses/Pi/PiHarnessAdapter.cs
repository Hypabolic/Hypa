using System.Text;
using System.Text.Json;
using Hypa.Continuity.Application;
using Hypa.Continuity.Domain;
using Hypabolic.Trajectory;

namespace Hypa.Continuity.Harnesses.Pi;

/// <summary>
/// Pi harness adapter (C-00): store under <c>~/.pi/agent</c>, Trajectory probe.
/// Capture copies one conversation. Restore does not wipe dest <c>.pi/agent</c>.
/// A WorkPack is not safe to share. Transcript bodies may still hold secrets.
/// </summary>
public sealed class PiHarnessAdapter : IHarnessAdapter
{
    public const string Id = "pi";
    public const int MinSupportedVersion = 1;
    public const int MaxSupportedVersion = 3;
    public static readonly TimeSpan DefaultStableWrite = TimeSpan.FromMilliseconds(500);

    public string AdapterId => Id;

    internal IReadOnlyList<string> LastCopiedLockResidue { get; private set; } = [];

    internal int LastStrippedWrittenLockCount { get; private set; }

    public ContinuityOutcome ReadVersion(string home, out string? version)
    {
        version = null;
        var root = AgentStoreRoot(home);
        if (!Directory.Exists(root))
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.HarnessVersionUnreadable,
                ".pi/agent missing");
        }

        var versionFile = Path.Combine(root, "version.txt");
        if (File.Exists(versionFile))
        {
            var text = File.ReadAllText(versionFile).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return ContinuityOutcome.Failure(
                    ContinuityReasons.HarnessVersionUnreadable,
                    "version.txt empty");
            }

            version = text;
            return ContinuityOutcome.Success();
        }

        foreach (var session in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
        {
            var header = TryReadSessionHeader(session);
            if (header is not null)
            {
                version = header.FormatVersion.ToString();
                return ContinuityOutcome.Success();
            }
        }

        return ContinuityOutcome.Failure(
            ContinuityReasons.HarnessVersionUnreadable,
            "no readable Pi session under HOME");
    }

    public ContinuityOutcome Quiesce(HarnessRunContext run, TimeSpan timeout)
    {
        if (run.OccupantStreaming)
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.NotQuiescent,
                "occupant still streaming");
        }

        var deadline = DateTime.UtcNow + timeout;
        var target = FindSessionFile(run.Home, run.Cwd, run.ConversationId);
        if (target is null)
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.NotQuiescent,
                "no Pi session file for conversation under workspace");
        }

        while (true)
        {
            if (IsFileStable(target, DefaultStableWrite, out var error))
                return ContinuityOutcome.Success();

            if (DateTime.UtcNow >= deadline)
            {
                return ContinuityOutcome.Failure(
                    ContinuityReasons.NotQuiescent,
                    error ?? "session not quiescent within timeout");
            }

            Thread.Sleep(50);
        }
    }

    public ContinuityOutcome CaptureStore(
        string home,
        string destStoreRoot,
        string sourceCwd,
        string conversationId)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceCwd);
            ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

            var src = AgentStoreRoot(home);
            if (!Directory.Exists(src))
            {
                return ContinuityOutcome.Failure(
                    ContinuityReasons.Internal,
                    ".pi/agent missing");
            }

            var session = FindSessionFile(home, sourceCwd, conversationId);
            if (session is null)
            {
                return ContinuityOutcome.Failure(
                    ContinuityReasons.Internal,
                    "no Pi session file for conversation under source cwd");
            }

            if (HarnessStoreFileRules.IsExcluded(Path.GetFileName(session)))
            {
                return ContinuityOutcome.Failure(
                    ContinuityReasons.Internal,
                    "packed session file is an excluded store name");
            }

            var attrs = File.GetAttributes(session);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                return ContinuityOutcome.Failure(
                    ContinuityReasons.Internal,
                    "packed session file is not a regular file");
            }

            var dest = Path.Combine(destStoreRoot, ".pi", "agent");
            Directory.CreateDirectory(dest);

            var versionSrc = Path.Combine(src, PiStorePaths.VersionFileName);
            if (File.Exists(versionSrc)
                && !HarnessStoreFileRules.IsExcluded(PiStorePaths.VersionFileName))
            {
                File.Copy(
                    versionSrc,
                    Path.Combine(dest, PiStorePaths.VersionFileName),
                    overwrite: true);
            }

            var encoded = EncodeWorkspaceForStore(sourceCwd);
            var destSessions = Path.Combine(
                dest,
                "sessions",
                "--" + encoded + "--");
            Directory.CreateDirectory(destSessions);
            File.Copy(
                session,
                Path.Combine(destSessions, Path.GetFileName(session)),
                overwrite: true);
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
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(destCwd);
            ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

            var captured = Path.Combine(sourceStoreRoot, ".pi", "agent");
            if (!Directory.Exists(captured))
            {
                return ContinuityOutcome.Failure(
                    ContinuityReasons.StoreRestoreFailed,
                    "captured .pi/agent missing");
            }

            var packedSession = FindDeclaredPackedSession(captured, conversationId);
            if (packedSession is null)
            {
                return ContinuityOutcome.Failure(
                    ContinuityReasons.StoreRestoreFailed,
                    "packed conversation session missing");
            }

            var destCwdFull = Path.GetFullPath(destCwd);
            var wouldWrite = SessionTextWithCwd(packedSession, destCwdFull);
            var destDir = SessionsDirectoryFor(destHome, destCwd);
            var destTarget = Path.Combine(destDir, Path.GetFileName(packedSession));
            var destRoot = AgentStoreRoot(destHome);

            foreach (var existingHomeWide in EnumerateSessionFilesForConversation(
                destRoot,
                conversationId))
            {
                var existingText = File.ReadAllText(existingHomeWide);
                if (!string.Equals(existingText, wouldWrite, StringComparison.Ordinal))
                {
                    return ContinuityOutcome.Failure(
                        ContinuityReasons.StoreRestoreFailed,
                        "dest session for conversation exists and is not this Work");
                }
            }

            var existing = FindSessionFile(destHome, destCwd, conversationId);
            if (existing is null && File.Exists(destTarget))
            {
                return ContinuityOutcome.Failure(
                    ContinuityReasons.StoreRestoreFailed,
                    "dest session path exists for a different conversation");
            }

            Directory.CreateDirectory(destDir);

            var packedVersion = Path.Combine(captured, PiStorePaths.VersionFileName);
            var destVersion = Path.Combine(destRoot, PiStorePaths.VersionFileName);
            var written = new List<string>();
            if (File.Exists(packedVersion) && !File.Exists(destVersion))
            {
                File.Copy(packedVersion, destVersion);
                written.Add(destVersion);
            }

            if (existing is null)
            {
                File.WriteAllText(destTarget, wouldWrite, new UTF8Encoding(false));
                written.Add(destTarget);
            }

            var packedSessionDir = Path.GetDirectoryName(packedSession);
            var lockStart = written.Count;
            if (!string.IsNullOrEmpty(packedSessionDir))
                HarnessStoreFileRules.CopyLockResidue(packedSessionDir, destDir, written);
            HarnessStoreFileRules.CopyLockResidue(captured, destRoot, written);
            LastCopiedLockResidue = written.Skip(lockStart).ToArray();
            LastStrippedWrittenLockCount = HarnessStoreFileRules.StripWrittenLocks(written);
            HarnessStoreFileRules.StripLockResidueIn(destDir);
            HarnessStoreFileRules.StripLockResidueIn(destRoot);
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
        var match = FindSessionFile(home, cwd, conversationId);
        if (match is null)
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.StartFailed,
                "dest session file missing for conversation under dest cwd");
        }

        var attemptId = PiResumeReport.NewAttemptId();
        var reportPath = PiResumeReport.ReportFile(home, attemptId);
        var reporterPath = PiResumeReport.ReporterPath(home);
        try
        {
            Directory.CreateDirectory(PiResumeReport.ReportsDirectory(home));
            Directory.CreateDirectory(PiResumeReport.ContinuityDir(home));
            File.WriteAllText(
                reporterPath,
                PiResumeReporterScript.TypeScript,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            return ContinuityOutcome.Failure(
                ContinuityReasons.StartFailed,
                "resume reporter write failed: " + ex.Message);
        }

        args = new HarnessStartArgs
        {
            Argv =
            [
                "--session",
                Path.GetFullPath(match),
                "-e",
                reporterPath,
            ],
            Env = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = home,
                ["HYPA_CUBE_HOME"] = home,
                [PiResumeReport.AttemptIdEnv] = attemptId,
                [PiResumeReport.ReportPathEnv] = reportPath,
            },
        };
        return ContinuityOutcome.Success();
    }

    public async ValueTask<ResumeProbeResult> WaitForHarnessReportAsync(
        string home,
        string cwd,
        HarnessStartArgs startArgs,
        TimeSpan timeout,
        IDestOccupantLiveness? liveness = null,
        CancellationToken cancellationToken = default)
    {
        _ = cwd;
        ArgumentNullException.ThrowIfNull(startArgs);
        if (!PiResumeReport.TryReadEnv(startArgs.Env, out var attemptId, out var reportPath)
            || !PiResumeReport.IsUnderHome(reportPath, home))
        {
            return ResumeProbeResult.Fail(
                ContinuityReasons.ResumeUnproven,
                "dest start missing resume report env");
        }

        if (liveness is null)
        {
            return ResumeProbeResult.Fail(
                ContinuityReasons.ResumeUnproven,
                "dest Pi process was not started");
        }

        var bound = timeout <= TimeSpan.Zero ? TimeSpan.Zero : timeout;
        var deadline = DateTime.UtcNow + bound;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var alive = await liveness.IsAliveAsync(cancellationToken).ConfigureAwait(false);
            if (!alive)
            {
                return ResumeProbeResult.Fail(
                    ContinuityReasons.ResumeUnproven,
                    "dest Pi exited before reporting a conversation id");
            }

            var parsed = await TryReadMatchingReportAsync(
                    reportPath,
                    attemptId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (parsed is not null)
                return parsed;

            if (DateTime.UtcNow >= deadline)
            {
                return ResumeProbeResult.Fail(
                    ContinuityReasons.ResumeUnproven,
                    "dest Pi did not report a conversation id");
            }

            var remaining = deadline - DateTime.UtcNow;
            var delay = remaining < TimeSpan.FromMilliseconds(10)
                ? remaining
                : TimeSpan.FromMilliseconds(10);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
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
                "dest Pi process was not started");
        }

        var alive = await occupant.Liveness.IsAliveAsync(cancellationToken).ConfigureAwait(false);
        if (!alive)
        {
            return ResumeProbeResult.Fail(
                ContinuityReasons.ResumeUnproven,
                "dest Pi exited before reporting a conversation id");
        }

        if (occupant.ResumeStartArgs is null)
        {
            return ResumeProbeResult.Fail(
                ContinuityReasons.ResumeUnproven,
                "live Pi did not report a conversation id");
        }

        return await WaitForHarnessReportAsync(
                occupant.Home,
                occupant.Cwd,
                occupant.ResumeStartArgs,
                timeout,
                occupant.Liveness,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<ResumeProbeResult?> TryReadMatchingReportAsync(
        string reportPath,
        string attemptId,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(reportPath))
            return null;

        string json;
        try
        {
            json = await File.ReadAllTextAsync(reportPath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(json))
            return null;

        return PiResumeReport.ReadIfMatching(json, attemptId);
    }

    public ResumeProbeResult ProbeConversationId(string home, string cwd)
    {
        try
        {
            var dir = SessionsDirectoryFor(home, cwd);
            if (!Directory.Exists(dir))
            {
                return ResumeProbeResult.Fail(
                    ContinuityReasons.ConversationMismatch,
                    "no Pi sessions dir for dest cwd");
            }

            var sessionFiles = Directory.GetFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly)
                .OrderByDescending(p => File.GetLastWriteTimeUtc(p))
                .ToArray();
            if (sessionFiles.Length == 0)
            {
                return ResumeProbeResult.Fail(
                    ContinuityReasons.ConversationMismatch,
                    "no Pi session under dest cwd");
            }

            // Prefer Trajectory-listed path that lives in this dest cwd dir.
            var listed = ListConversationPaths(home);
            foreach (var path in sessionFiles)
            {
                var full = Path.GetFullPath(path);
                if (!listed.Any(p => string.Equals(Path.GetFullPath(p), full, StringComparison.Ordinal)))
                    continue;
                var header = TryReadSessionHeader(path);
                if (header is null)
                    continue;
                return ResumeProbeResult.Found(
                    header.ConversationId,
                    ResumeEvidence.IndexListing);
            }

            // No Trajectory hit yet (pre-start). Exactly one readable session in dest cwd is OK.
            if (sessionFiles.Length == 1)
            {
                var header = TryReadSessionHeader(sessionFiles[0]);
                if (header is null)
                {
                    return ResumeProbeResult.Fail(
                        ContinuityReasons.ConversationMismatch,
                        "dest session header unreadable",
                        ResumeEvidence.StorePresence);
                }

                return ResumeProbeResult.Found(
                    header.ConversationId,
                    ResumeEvidence.StorePresence);
            }

            return ResumeProbeResult.Fail(
                ContinuityReasons.ConversationMismatch,
                "multiple Pi sessions under dest cwd; refuse arbitrary pick",
                ResumeEvidence.StorePresence);
        }
        catch (Exception ex)
        {
            return ResumeProbeResult.Fail(ContinuityReasons.Internal, ex.Message);
        }
    }

    public string AgentStoreRoot(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        return Path.Combine(Path.GetFullPath(home.Trim()), ".pi", "agent");
    }

    public string EncodeWorkspaceForStore(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var full = Path.GetFullPath(workspacePath.Trim());
        var trimmed = full.TrimStart('/', '\\');
        return trimmed.Replace('/', '-').Replace('\\', '-').Replace(':', '-');
    }

    public string SessionsDirectoryFor(string home, string workspacePath)
    {
        var encoded = EncodeWorkspaceForStore(workspacePath);
        return Path.Combine(AgentStoreRoot(home), "sessions", "--" + encoded + "--");
    }

    public string? FindSessionFile(string home, string workspacePath, string conversationId)
    {
        var dir = SessionsDirectoryFor(home, workspacePath);
        if (!Directory.Exists(dir))
            return null;

        foreach (var file in Directory.GetFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly))
        {
            var header = TryReadSessionHeader(file);
            if (header is not null
                && string.Equals(header.ConversationId, conversationId, StringComparison.Ordinal))
            {
                return file;
            }
        }

        return null;
    }

    private static string? FindDeclaredPackedSession(string agentRoot, string conversationId)
    {
        string? match = null;
        foreach (var file in EnumerateSessionFilesForConversation(agentRoot, conversationId))
        {
            if (match is not null)
                return null;

            match = file;
        }

        return match;
    }

    private static IEnumerable<string> EnumerateSessionFilesForConversation(
        string agentRoot,
        string conversationId)
    {
        var sessionsRoot = Path.Combine(agentRoot, PiStorePaths.SessionsDirectoryName);
        if (!Directory.Exists(sessionsRoot))
            yield break;

        foreach (var dir in Directory.EnumerateDirectories(sessionsRoot))
        {
            var name = Path.GetFileName(dir);
            if (!name.StartsWith("--", StringComparison.Ordinal)
                || !name.EndsWith("--", StringComparison.Ordinal)
                || name.Length <= 4)
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                if (HarnessStoreFileRules.IsExcluded(Path.GetFileName(file)))
                    continue;

                var header = TryReadSessionHeader(file);
                if (header is null
                    || !string.Equals(header.ConversationId, conversationId, StringComparison.Ordinal))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private static IReadOnlyList<string> ListConversationPaths(string home)
    {
        var root = Path.Combine(Path.GetFullPath(home.Trim()), ".pi", "agent");
        if (!Directory.Exists(root))
            return [];

        var paths = new List<string>();
        string? cursor = null;
        do
        {
            var page = TrajectoryConverter.ListPiTrajectoriesAsync(
                root: root,
                cursor: cursor,
                limit: 100,
                cancellationToken: CancellationToken.None).AsTask().GetAwaiter().GetResult();

            foreach (var item in page.Items)
                paths.Add(item.Path);

            cursor = string.IsNullOrEmpty(page.NextCursor) ? null : page.NextCursor;
        }
        while (cursor is not null);

        return paths;
    }

    internal static string ExtractConversationId(string listingId)
    {
        var idx = listingId.LastIndexOf('_');
        return idx < 0 ? listingId : listingId[(idx + 1)..];
    }

    private sealed record SessionHeader(string ConversationId, int FormatVersion, string WorkspacePath);

    private static SessionHeader? TryReadSessionHeader(string sessionFilePath)
    {
        if (string.IsNullOrWhiteSpace(sessionFilePath) || !File.Exists(sessionFilePath))
            return null;

        string first;
        try
        {
            using var reader = new StreamReader(sessionFilePath, Encoding.UTF8);
            first = reader.ReadLine() ?? "";
        }
        catch (IOException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(first))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(first);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type)
                || !string.Equals(type.GetString(), "session", StringComparison.Ordinal))
            {
                return null;
            }

            if (!root.TryGetProperty("id", out var idProp))
                return null;
            var id = idProp.GetString();
            if (string.IsNullOrWhiteSpace(id))
                return null;

            var version = 1;
            if (root.TryGetProperty("version", out var ver) && ver.TryGetInt32(out var v))
                version = v;
            if (version < MinSupportedVersion || version > MaxSupportedVersion)
                return null;

            var cwd = root.TryGetProperty("cwd", out var cwdProp)
                ? cwdProp.GetString() ?? ""
                : "";
            if (string.IsNullOrWhiteSpace(cwd))
                return null;

            return new SessionHeader(id.Trim(), version, cwd);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SessionTextWithCwd(string sessionFile, string destCwd)
    {
        var lines = File.ReadAllLines(sessionFile);
        if (lines.Length == 0)
            return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(lines[0]);
            var root = doc.RootElement;
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var prop in root.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "cwd", StringComparison.Ordinal))
                    {
                        writer.WriteString("cwd", destCwd);
                        continue;
                    }

                    prop.WriteTo(writer);
                }

                if (!root.TryGetProperty("cwd", out _))
                    writer.WriteString("cwd", destCwd);
                writer.WriteEndObject();
            }

            lines[0] = Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            // Leave header unchanged; StartArgs/probe will fail closed if needed.
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static bool IsFileStable(string path, TimeSpan settle, out string? error)
    {
        error = null;
        long len1;
        try
        {
            len1 = new FileInfo(path).Length;
        }
        catch (IOException ex)
        {
            error = ex.Message;
            return false;
        }

        if (TryReadSessionHeader(path) is null)
        {
            error = "session header unreadable";
            return false;
        }

        if (settle > TimeSpan.Zero)
            Thread.Sleep(settle);

        long len2;
        try
        {
            len2 = new FileInfo(path).Length;
        }
        catch (IOException ex)
        {
            error = ex.Message;
            return false;
        }

        if (len1 != len2)
        {
            error = "session file still growing";
            return false;
        }

        return true;
    }
}
