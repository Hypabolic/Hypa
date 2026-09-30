using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Canonical agent kinds, aliases, and start executables.
/// :193-220 lookup_agent, :224-228 versioned Muse binaries.
/// </summary>
public static class AgentKindCatalog
{
    public static readonly string[] All =
    [
        "pi",
        "claude",
        "codex",
        "gemini",
        "cursor",
        "devin",
        "agy",
        "cline",
        "omp",
        "mastracode",
        "opencode",
        "copilot",
        "kimi",
        "kiro",
        "droid",
        "amp",
        "grok",
        "hermes",
        "kilo",
        "qodercli",
        "qwen",
        "maki",
        "muse",
    ];

    /// <summary>
    /// Omp and Mastracode are process/start only.
    /// </summary>
    public static readonly string[] ScreenManifestIds =
    [
        "pi",
        "claude",
        "codex",
        "gemini",
        "cursor",
        "devin",
        "agy",
        "cline",
        "opencode",
        "copilot",
        "kimi",
        "kiro",
        "droid",
        "amp",
        "grok",
        "hermes",
        "kilo",
        "qodercli",
        "qwen",
        "maki",
        "muse",
    ];

    public static bool IsKnown(string? kind) =>
        !string.IsNullOrEmpty(kind) && Array.IndexOf(All, kind) >= 0;

    public static bool IsScreenManifest(string? kind) =>
        !string.IsNullOrEmpty(kind) && Array.IndexOf(ScreenManifestIds, kind) >= 0;

    public static bool IsProcessOnly(string? kind) =>
        kind is "omp" or "mastracode";

    /// <summary>
    /// Resolve a process name, alias, or start kind to a canonical label.
    /// Unknown input fails closed.
    /// </summary>
    public static bool TryResolve(string? input, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var name = NormalizeLookupName(PathBasename(input));
        var resolved = IdentifyNormalized(name);
        if (resolved is null)
            return false;

        canonical = resolved;
        return true;
    }

    /// <summary>
    /// Cursor uses cursor-agent (cursor-agent.cmd on Windows). Kiro uses kiro-cli.
    /// </summary>
    public static string InteractiveExecutable(string canonical)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonical);
        return canonical switch
        {
            "cursor" => OperatingSystem.IsWindows() ? "cursor-agent.cmd" : "cursor-agent",
            "kiro" => "kiro-cli",
            _ when IsKnown(canonical) => canonical,
            _ => throw new ArgumentOutOfRangeException(nameof(canonical), canonical, "Unknown agent kind."),
        };
    }

    // / <summary>Occupant spawn recipe.
    public static OccupantManifest CreateOccupantManifest(string canonicalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalId);
        if (!IsKnown(canonicalId))
            throw new ArgumentOutOfRangeException(nameof(canonicalId), canonicalId, "Unknown agent kind.");

        return new OccupantManifest
        {
            Id = canonicalId,
            Command = [InteractiveExecutable(canonicalId)],
            Cwd = "{workspace}",
            Env = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = "{cube_home}",
                ["HYPA_CUBE_HOME"] = "{cube_home}",
            },
            TranscriptSource = canonicalId,
            TranscriptRoot = "{cube_home}/." + canonicalId + "/agent",
        };
    }

    public static string UnsupportedKindMessage(string kind) =>
        "unsupported interactive agent kind: " + kind;

    private static string? IdentifyNormalized(string name) =>
        name switch
        {
            "pi" => "pi",
            "claude" or "claude-code" => "claude",
            "codex" => "codex",
            "gemini" => "gemini",
            "cursor" or "cursor-agent" => "cursor",
            "devin" or "devin-cli" or "devin cli" => "devin",
            "agy" or "antigravity" or "antigravity-cli" => "agy",
            "cline" => "cline",
            "omp" => "omp",
            "mastracode" or "mastra-code" or "mastra code" => "mastracode",
            "opencode" or "opencode2" or "open-code" => "opencode",
            "copilot" or "github-copilot" or "ghcs" => "copilot",
            "kimi" or "kimi-code" or "kimi code" => "kimi",
            "kiro" or "kiro-cli" => "kiro",
            "droid" => "droid",
            "amp" or "amp-local" => "amp",
            "grok" or "grok-build" => "grok",
            "hermes" or "hermes-agent" => "hermes",
            "kilo" or "kilo-code" or "kilo code" => "kilo",
            "qodercli" or "qoderclicn" or "qoder" or "qodercn" => "qodercli",
            "qwen" or "qwen-code" or "qwen code" => "qwen",
            "maki" => "maki",
            "muse" or "muse-code" or "muse-cli" => "muse",
            _ when IsMuseVersionedBinary(name) => "muse",
            _ => null,
        };

    /// <summary>
    // Digit must follow muse-bin-.
    /// Bare muse-bin and muse-binary stay unmatched.
    /// </summary>
    private static bool IsMuseVersionedBinary(string name)
    {
        const string prefix = "muse-bin-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var rest = name[prefix.Length..];
        return rest.Length > 0 && char.IsAsciiDigit(rest[0]);
    }

    private static string PathBasename(string path)
    {
        var trimmed = path.Trim().Trim('"');
        var slash = trimmed.LastIndexOfAny(['/', '\\']);
        if (slash >= 0 && slash + 1 < trimmed.Length)
            return trimmed[(slash + 1)..];
        return trimmed;
    }

    internal static string NormalizeLookupName(string name)
    {
        var lower = name.Trim().ToLowerInvariant();
        foreach (var suffix in new[] { ".exe", ".cmd", ".bat", ".ps1", ".js" })
        {
            if (lower.EndsWith(suffix, StringComparison.Ordinal))
                return lower[..^suffix.Length];
        }

        return lower;
    }
}
