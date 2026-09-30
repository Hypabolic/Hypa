using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.AgentRuntime.Application;

/// <summary>Refuse attach and serve when nested and <c>experimental.allow_nested</c> is false.</summary>
public static class NestedAttachGuard
{
    public const string EnvName = "HYPA_ENV";
    public const string EnvValue = "1";

    public const string Message =
        "hypa: nested attach is disabled (experimental.allow_nested = false). HYPA_ENV=1 is set.";

    public static bool IsBlocked(AttachClientConfig config, string? hypaEnv) =>
        !config.Experimental.AllowNested && hypaEnv == EnvValue;

    public static bool IsBlocked(AttachClientConfig config, IAttachConfigEnvironment env) =>
        IsBlocked(config, env.GetVariable(EnvName));
}
