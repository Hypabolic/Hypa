namespace Hypa.Runtime.Domain.Runner;

public sealed record CommandOutput
{
    public string Stdout { get; init; } = string.Empty;
    public string Stderr { get; init; } = string.Empty;
    public int ExitCode { get; init; }
    public TimeSpan Duration { get; init; }
    public bool WasTimedOut { get; init; }

    public static CommandOutput Captured(
        string stdout,
        string stderr,
        int exitCode,
        TimeSpan duration) =>
        new()
        {
            Stdout = stdout,
            Stderr = stderr,
            ExitCode = exitCode,
            Duration = duration,
            WasTimedOut = false,
        };

    public const int TimeoutExitCode = 124;

    public static CommandOutput CreateTimedOut(
        TimeSpan elapsed,
        string stdout = "",
        string stderr = "") =>
        new()
        {
            Stdout = stdout,
            Stderr = stderr,
            ExitCode = TimeoutExitCode,
            Duration = elapsed,
            WasTimedOut = true,
        };
}
