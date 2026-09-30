namespace Hypa.AgentRuntime.Application;

/// <summary>
// / Filter from <c>HYPA_LOG_LEVEL</c>.
// Hypa uses <c>HYPA_LOG_LEVEL</c>. Default
/// is info. <c>off</c> disables the file.
/// </summary>
public static class ProcessLogFilter
{
    public const string EnvVar = "HYPA_LOG_LEVEL";

    public static ProcessLogLevel Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return ProcessLogLevel.Information;

        switch (raw.Trim().ToLowerInvariant())
        {
            case "debug":
            case "trace":
                return ProcessLogLevel.Debug;
            case "info":
            case "information":
                return ProcessLogLevel.Information;
            case "warn":
            case "warning":
                return ProcessLogLevel.Warning;
            case "off":
            case "none":
            case "false":
            case "0":
                return ProcessLogLevel.Off;
            default:
                return ProcessLogLevel.Information;
        }
    }

    public static ProcessLogLevel FromEnvironment() =>
        Parse(Environment.GetEnvironmentVariable(EnvVar));

    public static bool Allows(ProcessLogLevel filter, ProcessLogLevel record)
    {
        if (filter == ProcessLogLevel.Off || record == ProcessLogLevel.Off)
            return false;
        return record >= filter;
    }
}
