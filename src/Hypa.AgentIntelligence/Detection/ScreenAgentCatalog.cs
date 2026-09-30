using Hypa.AgentRuntime.Application;

namespace Hypa.AgentIntelligence.Detection;

/// <summary>
// / Screen-manifest agent ids.
/// Identification is not occupant start. Do not use this set for agent.start.
/// </summary>
internal static class ScreenAgentCatalog
{
    public const string DefaultKnownAgentIdleFallback = "default_known_agent_idle_fallback";

    public static readonly string[] Ids = AgentKindCatalog.ScreenManifestIds;

    public static bool IsScreenManifest(string? kind) => AgentKindCatalog.IsScreenManifest(kind);

    public static string? Identify(string? processName) =>
        AgentKindCatalog.TryResolve(processName, out var id) ? id : null;

    public static bool ManifestMatchesAgent(string manifestId, IReadOnlyList<string> aliases, string agentId)
    {
        if (string.Equals(manifestId, agentId, StringComparison.Ordinal))
            return true;
        foreach (var alias in aliases)
        {
            if (string.Equals(alias, agentId, StringComparison.Ordinal))
                return true;
            if (string.Equals(Identify(alias), agentId, StringComparison.Ordinal))
                return true;
        }

        return string.Equals(Identify(manifestId), agentId, StringComparison.Ordinal);
    }
}
