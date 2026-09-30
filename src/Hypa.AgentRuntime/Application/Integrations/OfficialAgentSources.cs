using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application.Integrations;

/// <summary>
/// Official hook sources. Custom hooks must use a different source.
/// </summary>
public static class OfficialAgentSources
{
    public const string Pi = "hypa:pi";
    public const string Omp = "hypa:omp";
    public const string Claude = "hypa:claude";
    public const string Codex = "hypa:codex";
    public const string Copilot = "hypa:copilot";
    public const string Devin = "hypa:devin";
    public const string Droid = "hypa:droid";
    public const string Kimi = "hypa:kimi";
    public const string Opencode = "hypa:opencode";
    public const string Kilo = "hypa:kilo";
    public const string Hermes = "hypa:hermes";
    public const string Qodercli = "hypa:qodercli";
    public const string Qwen = "hypa:qwen";
    public const string Cursor = "hypa:cursor";
    public const string Mastracode = "hypa:mastracode";
    public const string AntigravityCli = "hypa:antigravity_cli";
    public const string Grok = "hypa:grok";

    public static string For(OfficialIntegrationTarget target) =>
        "hypa:" + target.WireName();

    public static bool IsOfficial(string? source, string? agent)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(agent))
            return false;
        foreach (var target in OfficialIntegrationTargets.All)
        {
            if (string.Equals(source, For(target), StringComparison.Ordinal)
                && string.Equals(agent, target.AgentName(), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsReservedSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return false;
        foreach (var target in OfficialIntegrationTargets.All)
        {
            if (string.Equals(source, For(target), StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
