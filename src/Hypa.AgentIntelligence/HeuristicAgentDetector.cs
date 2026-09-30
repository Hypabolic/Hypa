using Hypa.AgentIntelligence.Detection;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hypa.AgentIntelligence;

/// <summary>
/// Screen heuristics for common coding agents + generic shell prompts.
/// Scans a bounded recent window (last lines) so older prompts do not stick forever.
/// </summary>
public sealed class HeuristicAgentDetector : IAgentDetector, IAgentManifestCatalog
{
    private const int RecentLineWindow = 24;
    private readonly AgentManifestCache _cache;
    private readonly ILogger _logger;

    public HeuristicAgentDetector()
        : this(AgentManifestCache.BundledOnly())
    {
    }

    public HeuristicAgentDetector(
        IAttachConfigEnvironment env,
        IAttachConfigFiles files,
        IAgentManifestTextFetcher? fetcher = null,
        TimeProvider? time = null,
        ILogger? logger = null)
        : this(new AgentManifestCache(files, new AgentDetectionPaths(env), fetcher, time, logger), logger)
    {
    }

    internal HeuristicAgentDetector(AgentManifestCache cache, ILogger? logger = null)
    {
        _cache = cache;
        _logger = logger ?? NullLogger.Instance;
    }

    public IReadOnlyList<AgentManifestSummary> ListSummaries() => _cache.ListSummaries();

    public IReadOnlyList<AgentManifestSummary> Reload() => _cache.Reload();

    public long? LastCheckUnix => _cache.LastCheckUnix;

    public string? LastResult => _cache.LastResult;

    public void CheckRemoteUpdates(bool enabled) => _cache.CheckRemoteUpdates(enabled);

    private static readonly string[] BlockedMarkers =
    [
        "Do you want to proceed",
        "Allow this action",
        "Waiting for input",
        "waiting for your input",
        "Press Enter to continue",
        "Press enter to confirm",
        "permission to",
        "Approve this",
        "Approve?",
        "Awaiting approval",
        "Needs your approval",
        "I trust this folder",
        "(y/n)",
        "[Y/n]",
        "[y/N]",
    ];

    private static readonly string[] WorkingMarkers =
    [
        "Thinking",
        "Running",
        "Tool call",
        "Invoking",
        "Generating",
        "Compiling",
        "Building",
        "esc to interrupt",
        "████",
    ];

    private static readonly string[] LastLineDoneTerminators =
    [
        "Done.",
        "Completed",
        "All tasks complete",
    ];

    public DetectionResult Detect(string snapshotText, string? processName = null) =>
        Detect(snapshotText, processName, oscTitle: "", oscProgress: "");

    /// <summary>
    // Screen
    /// manifests classify state. Isolated transcript phrases do not pick a kind.
    /// </summary>
    public DetectionResult Detect(
        string snapshotText,
        string? processName,
        string oscTitle,
        string oscProgress)
    {
        var kind = DetectKind(processName);
        if (kind is null)
            return new DetectionResult { Status = AgentStatus.Unknown, Confidence = 0 };

        if (ScreenAgentCatalog.IsScreenManifest(kind))
        {
            return DetectFromManifest(
                snapshotText ?? "",
                kind,
                oscTitle ?? "",
                oscProgress ?? "");
        }

        var heuristic = DetectHeuristic(snapshotText ?? "", kind);
        if (heuristic.Status == AgentStatus.Unknown)
            return KnownAgentIdle(kind);
        return heuristic;
    }

    private DetectionResult DetectFromManifest(
        string snapshotText,
        string kind,
        string oscTitle = "",
        string oscProgress = "")
    {
        var loaded = _cache.Get(kind);
        if (loaded is null)
            return KnownAgentIdle(kind);

        if (!string.IsNullOrEmpty(loaded.Warning))
            _logger.LogWarning("{Warning}", loaded.Warning);

        var match = ManifestEngine.Evaluate(
            loaded,
            new ManifestDetectionInput(snapshotText, oscTitle, oscProgress));
        var explained = ApplyExplain(new DetectionResult
        {
            AgentKind = kind,
            Confidence = 0.4,
        }, match);

        //   let detection = detect_agent_with_osc(...);
        //   (!detection.skip_state_update).then_some(detection)
        // Overlay: drop the update so the previous AgentStatus stays.
        if (match.SkipStateUpdate)
        {
            return explained with
            {
                Status = AgentStatus.Unknown,
                SkipStateUpdate = true,
                MatchedRuleId = match.MatchedRuleId,
                FallbackReason = match.FallbackReason,
            };
        }

        //   let Some((rule, region_name)) = matched else { return fallback_explain(...) };
        //   state = rule.state mapped to AgentState
        //   matched_rule: Some(MatchedRule { id, ..., state })
        // A matched rule is authoritative. Heuristic Done must not replace it.
        if (match.MatchedRuleId is not null)
        {
            return explained with
            {
                Status = match.State,
                Message = match.MatchedRuleId,
                MatchedRuleId = match.MatchedRuleId,
                Confidence = match.State switch
                {
                    AgentStatus.Blocked => 0.75,
                    AgentStatus.Working => 0.6,
                    _ => 0.5,
                },
            };
        }

        // no rule → Idle + DEFAULT_KNOWN_AGENT_IDLE_FALLBACK for a known agent.
        // Do not apply generic heuristic Working/Blocked/Idle. Heuristic Done
        // may still apply when no rule matched (recorded transcripts).
        var heuristic = DetectHeuristic(snapshotText, kind);
        if (heuristic.Status == AgentStatus.Done)
            return ApplyExplain(heuristic, match) with { MatchedRuleId = null, FallbackReason = null };

        return explained with
        {
            Status = AgentStatus.Idle,
            FallbackReason = ScreenAgentCatalog.DefaultKnownAgentIdleFallback,
            Confidence = 0.4,
        };
    }

    private static DetectionResult KnownAgentIdle(string kind) =>
        new()
        {
            AgentKind = kind,
            Status = AgentStatus.Idle,
            FallbackReason = ScreenAgentCatalog.DefaultKnownAgentIdleFallback,
            Confidence = 0.4,
        };

    private static DetectionResult ApplyExplain(DetectionResult result, ManifestMatch match) =>
        result with
        {
            ManifestSourceKind = match.Origin?.SourceKind,
            ManifestSource = match.Origin?.Label,
            ManifestVersion = match.ManifestVersion,
            Warning = match.Warning,
        };

    private DetectionResult DetectHeuristic(string snapshotText, string? kind)
    {
        var window = TakeRecentWindow(snapshotText);
        var lines = window.Split('\n');
        var lastPrompt = LastMatchingIndex(lines, IsShellPromptLine, after: -1, skipStatus: false);

        // Include the prompt line so "❯ 1. Yes, I trust this folder" stays blocked.
        // Live working (spinner / esc to interrupt / token stream) beats a later ›/❯.
        // Recorded Codex/Claude keep the composer glyph on working and done frames.
        var after = lastPrompt >= 0 ? lastPrompt - 1 : -1;
        var lastBlocked = LastMatchingIndex(lines, IsBlockedLine, after, skipStatus: true);
        var lastLiveWorking = LastMatchingIndex(lines, IsLiveWorkingLine, after: -1, skipStatus: true);
        var lastDone = LastMatchingIndex(lines, IsDoneLine, after: -1, skipStatus: true);
        var lastWorking = LastMatchingIndex(lines, IsWorkingLine, after, skipStatus: true);
        if (lastLiveWorking > lastWorking)
            lastWorking = lastLiveWorking;
        var last = MaxIndex(lastBlocked, lastDone, lastWorking);

        if (lastPrompt >= 0 && last < 0)
        {
            return new DetectionResult
            {
                Status = AgentStatus.Idle,
                AgentKind = kind ?? "shell",
                Confidence = 0.5,
            };
        }

        if (last == lastBlocked && lastBlocked >= 0)
        {
            return new DetectionResult
            {
                Status = AgentStatus.Blocked,
                AgentKind = kind,
                Message = FirstMarker(lines[lastBlocked], BlockedMarkers),
                Confidence = 0.75,
            };
        }

        if (last == lastDone && lastDone >= 0)
        {
            TryFindDoneOnLine(lines[lastDone], out var doneMarker);
            return new DetectionResult
            {
                Status = AgentStatus.Done,
                AgentKind = kind,
                Message = doneMarker,
                Confidence = 0.55,
            };
        }

        if (last == lastWorking && lastWorking >= 0)
        {
            return new DetectionResult
            {
                Status = AgentStatus.Working,
                AgentKind = kind,
                Message = FirstMarker(lines[lastWorking], WorkingMarkers),
                Confidence = 0.6,
            };
        }

        return new DetectionResult
        {
            Status = AgentStatus.Unknown,
            AgentKind = kind,
            Confidence = 0.0,
        };
    }

    private static string TakeRecentWindow(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        if (lines.Length <= RecentLineWindow)
            return normalized;
        return string.Join('\n', lines.AsSpan(lines.Length - RecentLineWindow).ToArray());
    }

    private static bool IsBlockedLine(string line) =>
        FirstMarker(line, BlockedMarkers).Length > 0;

    private static bool IsWorkingLine(string line) =>
        FirstMarker(line, WorkingMarkers).Length > 0 || IsLiveWorkingLine(line);

    private static bool IsLiveWorkingLine(string line)
    {
        if (IsStatusBarLine(line))
            return false;

        var t = line.Trim();
        // Recorded Codex: "• Working (0s • esc to interrupt)" stays live under a later ›.
        if (t.Contains("Working", StringComparison.OrdinalIgnoreCase)
            && t.Contains("esc to interrupt", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Claude Code streams a random gerund plus a token counter.
        if (t.Contains("tokens)", StringComparison.OrdinalIgnoreCase)
            && (t.Contains('↓') || t.Contains('…') || t.Contains("...")))
        {
            return true;
        }

        return false;
    }

    private static bool IsDoneLine(string line) =>
        TryFindDoneOnLine(line, out _);

    private static bool TryFindDoneOnLine(string line, out string found)
    {
        if (TryFindLastLineDoneTerminator(line, out found))
            return true;

        if (line.Contains("finished successfully", StringComparison.OrdinalIgnoreCase))
        {
            found = "finished successfully";
            return true;
        }

        if (!IsShellPromptLine(line))
        {
            if (line.Contains("Churned for", StringComparison.OrdinalIgnoreCase))
            {
                found = "Churned for";
                return true;
            }

            if (line.Contains("Baked for", StringComparison.OrdinalIgnoreCase))
            {
                found = "Baked for";
                return true;
            }

            var trimmed = line.Trim();
            if (trimmed.Contains("Done.", StringComparison.Ordinal)
                && trimmed.IndexOf("Done.", StringComparison.Ordinal) >= 0
                && !trimmed.StartsWith('›')
                && !trimmed.StartsWith('❯'))
            {
                found = "Done.";
                return true;
            }
        }

        found = string.Empty;
        return false;
    }

    private static string FirstMarker(string line, string[] markers)
    {
        foreach (var marker in markers)
        {
            if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return marker;
        }

        return string.Empty;
    }

    private static int LastMatchingIndex(
        string[] lines,
        Func<string, bool> match,
        int after,
        bool skipStatus)
    {
        for (var i = lines.Length - 1; i > after; i--)
        {
            if (skipStatus && IsStatusBarLine(lines[i]))
                continue;
            if (match(lines[i]))
                return i;
        }

        return -1;
    }

    private static int MaxIndex(int a, int b, int c)
    {
        var max = a;
        if (b > max)
            max = b;
        if (c > max)
            max = c;
        return max;
    }

    private static bool TryFindLastLineDoneTerminator(string line, out string found)
    {
        var t = line.Trim();
        if (t.Length == 0)
        {
            found = string.Empty;
            return false;
        }

        foreach (var marker in LastLineDoneTerminators)
        {
            if (t.Equals(marker, StringComparison.OrdinalIgnoreCase))
            {
                found = marker;
                return true;
            }

            if (!t.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
                continue;

            if (IsDoneTerminatorSuffix(t.AsSpan(marker.Length)))
            {
                found = marker;
                return true;
            }
        }

        found = string.Empty;
        return false;
    }

    private static bool IsDoneTerminatorSuffix(ReadOnlySpan<char> rest)
    {
        if (rest.IsEmpty)
            return false;

        foreach (var ch in rest)
        {
            if (ch is not ('.' or '!' or ' ' or '\t'))
                return false;
        }

        return true;
    }

    private static bool IsShellPromptLine(string line)
    {
        if (string.IsNullOrEmpty(line))
            return false;

        var trimmed = line.Trim();
        if (trimmed.Length == 0)
            return false;

        // Recorded Codex/Claude put ›/❯ at the start, then typed text.
        if (trimmed[0] is '›' or '❯')
            return true;

        // Prompt when › ❯ $ % # is the whole trimmed line or follows whitespace.
        // '~' before '$' is a path prompt (user@host:~$). '=' before '$' is VAR=$.
        // A letter before '#' is C#. A digit before '%' is 42%.
        var t = line.TrimEnd();
        var last = t[^1];
        if (last is not ('›' or '❯' or '$' or '%' or '#'))
            return false;

        if (t.Length == 1)
            return true;

        var prev = t[^2];
        if (char.IsWhiteSpace(prev))
            return true;

        return last == '$' && prev == '~';
    }

    private static bool IsStatusBarLine(string line)
    {
        var t = line.Trim();
        if (t.Length == 0)
            return true;

        if (IsBoxOnly(t))
            return true;

        if (t.Contains("Context ", StringComparison.OrdinalIgnoreCase) && t.Contains('%'))
            return true;

        if (t.Contains("shift+tab", StringComparison.OrdinalIgnoreCase))
            return true;

        if (t.Contains("auto mode", StringComparison.OrdinalIgnoreCase))
            return true;

        if (HasClock(t))
            return true;

        if (t.Contains('·') &&
            (t.Contains("gpt-", StringComparison.OrdinalIgnoreCase)
             || t.Contains("Workspace", StringComparison.OrdinalIgnoreCase)
             || t.Contains("Opus", StringComparison.OrdinalIgnoreCase)
             || t.Contains("Sonnet", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static bool IsBoxOnly(string t)
    {
        var any = false;
        foreach (var ch in t)
        {
            if (char.IsWhiteSpace(ch))
                continue;
            any = true;
            if (ch is not ('─' or '━' or '═' or '│' or '┌' or '┐' or '└' or '┘'
                or '├' or '┤' or '┬' or '┴' or '┼' or '·' or '•' or '|' or '-' or '_'))
            {
                return false;
            }
        }

        return any;
    }

    private static bool HasClock(string t)
    {
        for (var i = 0; i + 7 < t.Length; i++)
        {
            if (char.IsDigit(t[i]) && char.IsDigit(t[i + 1]) && t[i + 2] == ':'
                && char.IsDigit(t[i + 3]) && char.IsDigit(t[i + 4]) && t[i + 5] == ':'
                && char.IsDigit(t[i + 6]) && char.IsDigit(t[i + 7]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    // Kind is the
    /// process identity, not a screen substring.
    /// </summary>
    private static string? DetectKind(string? processName)
    {
        if (AgentKindCatalog.TryResolve(processName, out var identified))
            return identified;
        return null;
    }

}
