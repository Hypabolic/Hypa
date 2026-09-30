using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.AgentRuntime.Application;

/// <summary>Session name: --session &gt; HYPA_SESSION &gt; session.name &gt; default.</summary>
public static class AttachSessionResolver
{
    public const string DefaultName = "default";
    public const string SessionEnv = "HYPA_SESSION";

    public static string Resolve(string? sessionOption, string? hypaSessionEnv, AttachClientConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!string.IsNullOrWhiteSpace(sessionOption))
            return sessionOption.Trim();
        if (!string.IsNullOrWhiteSpace(hypaSessionEnv))
            return hypaSessionEnv.Trim();
        if (!string.IsNullOrWhiteSpace(config.Session.Name))
            return config.Session.Name.Trim();
        return DefaultName;
    }

    public static string Resolve(string? sessionOption, AttachClientConfig config) =>
        Resolve(sessionOption, Environment.GetEnvironmentVariable(SessionEnv), config);

    public static string? EnvironmentSession() =>
        Environment.GetEnvironmentVariable(SessionEnv);
}
