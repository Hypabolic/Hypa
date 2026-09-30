namespace Hypa.AgentRuntime.Application.Integrations;

public static class IntegrationHookCommand
{
    public static string ForUnix(string hookPath, string? action)
    {
        var command = "bash " + ShellSingleQuote(hookPath);
        if (!string.IsNullOrEmpty(action))
            command += " " + action;
        return command;
    }

    public static string ForUnixSh(string hookPath, string? action)
    {
        var command = "sh " + ShellSingleQuote(hookPath);
        if (!string.IsNullOrEmpty(action))
            command += " " + action;
        return command;
    }

    public static string ShellSingleQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
