using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application.Integrations;

/// <summary>
// / Official session restore.
/// <c>plan</c> and <c>session_ref_from_snapshot</c>. Only official
/// stored session references produce a plan.
/// </summary>
public static class OfficialAgentResumePlanner
{
    public static OfficialAgentResumePlan? TryPlan(
        NativeAgentSessionRef? session,
        bool resumeEnabled)
    {
        if (!resumeEnabled || session is null)
            return null;
        if (!OfficialAgentSources.IsOfficial(session.Source, session.Agent))
            return null;
        if (!IsValidStoredRef(session))
            return null;

        var argv = Argv(session);
        if (argv is null)
            return null;

        return new OfficialAgentResumePlan
        {
            Agent = session.Agent,
            Argv = argv,
            DedupeKey = session.Source + "\0" + session.Agent + "\0" + session.Kind + "\0" + session.Value,
        };
    }

    /// <summary>
    /// type the resume argv into the restored shell.
    /// </summary>
    public static string ToShellCommand(OfficialAgentResumePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var parts = new string[plan.Argv.Count];
        for (var i = 0; i < plan.Argv.Count; i++)
            parts[i] = Quote(plan.Argv[i]);
        return string.Join(" ", parts);
    }

    /// <summary>
    // Path refs are only pi and omp.
    /// Cursor resume argv0 is <c>cursor-agent</c> on Unix.
    /// </summary>
    private static IReadOnlyList<string>? Argv(NativeAgentSessionRef session)
    {
        var value = session.Value;
        return (session.Source, session.Agent, session.Kind) switch
        {
            (OfficialAgentSources.Claude, "claude", NativeAgentSessionRef.KindId) =>
                ["claude", "--resume", value],
            (OfficialAgentSources.Codex, "codex", NativeAgentSessionRef.KindId) =>
                ["codex", "resume", value],
            (OfficialAgentSources.Copilot, "copilot", NativeAgentSessionRef.KindId) =>
                ["copilot", "--resume=" + value],
            (OfficialAgentSources.Devin, "devin", NativeAgentSessionRef.KindId) =>
                ["devin", "--resume", value],
            (OfficialAgentSources.Droid, "droid", NativeAgentSessionRef.KindId) =>
                ["droid", "--resume", value],
            (OfficialAgentSources.Kimi, "kimi", NativeAgentSessionRef.KindId) =>
                ["kimi", "--session", value],
            (OfficialAgentSources.Mastracode, "mastracode", NativeAgentSessionRef.KindId) =>
                ["mastracode", "--thread", value],
            (OfficialAgentSources.Pi, "pi", NativeAgentSessionRef.KindId
                or NativeAgentSessionRef.KindPath) =>
                ["pi", "--session", value],
            (OfficialAgentSources.Omp, "omp", NativeAgentSessionRef.KindId
                or NativeAgentSessionRef.KindPath) =>
                ["omp", "--resume=" + value],
            (OfficialAgentSources.Hermes, "hermes", NativeAgentSessionRef.KindId) =>
                ["hermes", "--resume", value],
            (OfficialAgentSources.Opencode, "opencode", NativeAgentSessionRef.KindId) =>
                ["opencode", "--session", value],
            (OfficialAgentSources.Qodercli, "qodercli", NativeAgentSessionRef.KindId) =>
                ["qodercli", "--resume", value],
            (OfficialAgentSources.Qwen, "qwen", NativeAgentSessionRef.KindId) =>
                ["qwen", "--resume", value],
            (OfficialAgentSources.Kilo, "kilo", NativeAgentSessionRef.KindId) =>
                ["kilo", "--session", value],
            (OfficialAgentSources.Cursor, "cursor", NativeAgentSessionRef.KindId) =>
                ["cursor-agent", "--resume", value],
            (OfficialAgentSources.AntigravityCli, "agy", NativeAgentSessionRef.KindId) =>
                ["agy", "--conversation", value],
            (OfficialAgentSources.Grok, "grok", NativeAgentSessionRef.KindId) =>
                ["grok", "--resume", value],
            _ => null,
        };
    }

    /// <summary>
    /// require an absolute session path.
    /// </summary>
    private static bool IsValidStoredRef(NativeAgentSessionRef session) =>
        session.Kind switch
        {
            NativeAgentSessionRef.KindId => NativeAgentSessionRef.IsValidId(session.Value),
            NativeAgentSessionRef.KindPath => NativeAgentSessionRef.IsValidPath(session.Value),
            _ => false,
        };

    /// <summary>
    /// unquoted only when every byte is in the safe allowlist.
    /// </summary>
    private static string Quote(string value)
    {
        if (value.Length == 0)
            return "''";
        if (IsUnquotedSafe(value))
            return value;

        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static bool IsUnquotedSafe(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsAsciiLetterOrDigit(ch))
                continue;
            if (ch is '_' or '-' or '.' or '/' or ':' or '@' or '%' or '+' or '=')
                continue;
            return false;
        }

        return true;
    }
}
