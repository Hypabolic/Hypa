namespace Hypa.Runtime.Domain.Runner;

public sealed record CommandInvocation
{
    public string Executable { get; init; } = string.Empty;
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string OriginalCommand { get; init; } = string.Empty;
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string> EnvOverrides { get; init; } =
        new Dictionary<string, string>();
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public ToolRunMode Mode { get; init; } = ToolRunMode.Buffered;

    public static CommandInvocation Buffered(
        string executable,
        IReadOnlyList<string> arguments,
        string original) =>
        new()
        {
            Executable = executable,
            Arguments = arguments,
            OriginalCommand = original,
            Mode = ToolRunMode.Buffered,
            Timeout = TimeSpan.FromSeconds(30),
        };

    public static CommandInvocation Passthrough(
        string executable,
        IReadOnlyList<string> arguments,
        string original) =>
        new()
        {
            Executable = executable,
            Arguments = arguments,
            OriginalCommand = original,
            Mode = ToolRunMode.Passthrough,
            Timeout = TimeSpan.FromSeconds(30),
        };
}
