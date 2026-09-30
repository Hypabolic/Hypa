using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.AgentRuntime.Application;

/// <summary>
// / Hidden-cursor reveal for CJK IME.
/// <c>src/app/mod.rs:199-212</c> <c>parse_cjk_ime_agents</c> and
/// <c>src/ui/tab_surface.rs:172-205</c> <c>tab_surface_cursor</c>.
/// </summary>
public sealed record CjkImeRevealFilter(
    bool Enabled,
    bool FilterConfigured,
    IReadOnlyList<string> Agents,
    int CursorShape)
{
    public static CjkImeRevealFilter Disabled { get; } = new(false, false, [], 2);

    public static CjkImeRevealFilter From(AttachExperimentalConfig experimental)
    {
        ArgumentNullException.ThrowIfNull(experimental);
        return new(
            experimental.RevealHiddenCursorForCjkIme,
            experimental.CjkImeAgents.Count > 0,
            ParseAgents(experimental.CjkImeAgents),
            ToDecscusr(experimental.CjkImeCursorShape));
    }

    public bool ShouldReveal(string? detectedAgent)
    {
        if (!Enabled)
            return false;
        if (!FilterConfigured)
            return true;
        if (string.IsNullOrWhiteSpace(detectedAgent))
            return false;
        if (!AgentKindCatalog.TryResolve(detectedAgent, out var canonical))
            return false;
        for (var i = 0; i < Agents.Count; i++)
        {
            if (string.Equals(Agents[i], canonical, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public static IReadOnlyList<string> ParseAgents(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
            return [];
        var parsed = new List<string>(names.Count);
        foreach (var name in names)
        {
            if (!AgentKindCatalog.TryResolve(name, out var canonical))
                continue;
            var seen = false;
            for (var i = 0; i < parsed.Count; i++)
            {
                if (string.Equals(parsed[i], canonical, StringComparison.Ordinal))
                {
                    seen = true;
                    break;
                }
            }

            if (!seen)
                parsed.Add(canonical);
        }

        return parsed;
    }

    /// <summary>
    // / DECSCUSR 1–6.
    /// <c>ImeCursorShape::to_decscusr</c>. Unknown names use steady block.
    /// </summary>
    public static int ToDecscusr(string? shape) =>
        shape switch
        {
            "block" => 1,
            "steady_block" => 2,
            "underline" => 3,
            "steady_underline" => 4,
            "bar" => 5,
            "steady_bar" => 6,
            _ => 2,
        };
}
