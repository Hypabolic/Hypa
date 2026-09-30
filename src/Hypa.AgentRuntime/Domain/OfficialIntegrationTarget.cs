namespace Hypa.AgentRuntime.Domain;

/// <summary>
// / Official agent install target.
/// <c>src/api/schema/integrations.rs</c> snake_case. Labels match
/// <c>src/integration/registry.rs</c> <c>integration_target_label</c>.
/// </summary>
public enum OfficialIntegrationTarget
{
    Pi,
    Omp,
    Claude,
    Codex,
    Copilot,
    Devin,
    Droid,
    Kimi,
    Opencode,
    Kilo,
    Hermes,
    Qodercli,
    Qwen,
    Cursor,
    Mastracode,
    AntigravityCli,
    Grok,
}

public static class OfficialIntegrationTargets
{
    public static IReadOnlyList<OfficialIntegrationTarget> All { get; } =
    [
        OfficialIntegrationTarget.Pi,
        OfficialIntegrationTarget.Omp,
        OfficialIntegrationTarget.Claude,
        OfficialIntegrationTarget.Codex,
        OfficialIntegrationTarget.Copilot,
        OfficialIntegrationTarget.Devin,
        OfficialIntegrationTarget.Droid,
        OfficialIntegrationTarget.Kimi,
        OfficialIntegrationTarget.Opencode,
        OfficialIntegrationTarget.Kilo,
        OfficialIntegrationTarget.Hermes,
        OfficialIntegrationTarget.Qodercli,
        OfficialIntegrationTarget.Qwen,
        OfficialIntegrationTarget.Cursor,
        OfficialIntegrationTarget.Mastracode,
        OfficialIntegrationTarget.AntigravityCli,
        OfficialIntegrationTarget.Grok,
    ];

    public static string WireName(this OfficialIntegrationTarget target) =>
        target switch
        {
            OfficialIntegrationTarget.Pi => "pi",
            OfficialIntegrationTarget.Omp => "omp",
            OfficialIntegrationTarget.Claude => "claude",
            OfficialIntegrationTarget.Codex => "codex",
            OfficialIntegrationTarget.Copilot => "copilot",
            OfficialIntegrationTarget.Devin => "devin",
            OfficialIntegrationTarget.Droid => "droid",
            OfficialIntegrationTarget.Kimi => "kimi",
            OfficialIntegrationTarget.Opencode => "opencode",
            OfficialIntegrationTarget.Kilo => "kilo",
            OfficialIntegrationTarget.Hermes => "hermes",
            OfficialIntegrationTarget.Qodercli => "qodercli",
            OfficialIntegrationTarget.Qwen => "qwen",
            OfficialIntegrationTarget.Cursor => "cursor",
            OfficialIntegrationTarget.Mastracode => "mastracode",
            OfficialIntegrationTarget.AntigravityCli => "antigravity_cli",
            OfficialIntegrationTarget.Grok => "grok",
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };

    public static string Label(this OfficialIntegrationTarget target) =>
        target is OfficialIntegrationTarget.AntigravityCli
            ? "antigravity-cli"
            : target.WireName();

    /// <summary>
    // / First PATH command.
    /// Cursor uses <c>cursor-agent</c>. Antigravity uses <c>agy</c>.
    /// </summary>
    public static string CommandName(this OfficialIntegrationTarget target) =>
        target.CommandNames()[0];

    public static IReadOnlyList<string> CommandNames(this OfficialIntegrationTarget target) =>
        target switch
        {
            OfficialIntegrationTarget.Kilo => ["kilo", "kilo-code"],
            OfficialIntegrationTarget.Cursor => ["cursor-agent"],
            OfficialIntegrationTarget.AntigravityCli => ["agy"],
            _ => [target.WireName()],
        };

    /// <summary>
    /// Agent identity stored with official session refs.
    /// </summary>
    public static string AgentName(this OfficialIntegrationTarget target) =>
        target is OfficialIntegrationTarget.AntigravityCli ? "agy" : target.WireName();

    public static bool TryParse(string? value, out OfficialIntegrationTarget target)
    {
        target = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        switch (value.Trim())
        {
            case "pi":
                target = OfficialIntegrationTarget.Pi;
                return true;
            case "omp":
                target = OfficialIntegrationTarget.Omp;
                return true;
            case "claude":
                target = OfficialIntegrationTarget.Claude;
                return true;
            case "codex":
                target = OfficialIntegrationTarget.Codex;
                return true;
            case "copilot":
                target = OfficialIntegrationTarget.Copilot;
                return true;
            case "devin":
                target = OfficialIntegrationTarget.Devin;
                return true;
            case "droid":
                target = OfficialIntegrationTarget.Droid;
                return true;
            case "kimi":
                target = OfficialIntegrationTarget.Kimi;
                return true;
            case "opencode":
                target = OfficialIntegrationTarget.Opencode;
                return true;
            case "kilo":
                target = OfficialIntegrationTarget.Kilo;
                return true;
            case "hermes":
                target = OfficialIntegrationTarget.Hermes;
                return true;
            case "qodercli":
                target = OfficialIntegrationTarget.Qodercli;
                return true;
            case "qwen":
                target = OfficialIntegrationTarget.Qwen;
                return true;
            case "cursor":
                target = OfficialIntegrationTarget.Cursor;
                return true;
            case "mastracode":
                target = OfficialIntegrationTarget.Mastracode;
                return true;
            case "antigravity-cli":
            case "antigravity_cli":
                target = OfficialIntegrationTarget.AntigravityCli;
                return true;
            case "grok":
                target = OfficialIntegrationTarget.Grok;
                return true;
            default:
                return false;
        }
    }
}
