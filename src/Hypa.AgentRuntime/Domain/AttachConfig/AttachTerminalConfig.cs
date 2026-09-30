namespace Hypa.AgentRuntime.Domain.AttachConfig;

public enum TerminalShellMode
{
    Auto,
    Login,
    NonLogin,
}

public enum TerminalNewCwdKind
{
    Follow,
    Home,
    Current,
    Path,
}

public sealed record TerminalNewCwdSpec
{
    public TerminalNewCwdKind Kind { get; init; } = TerminalNewCwdKind.Follow;

    public string? Path { get; init; }

    public static TerminalNewCwdSpec Follow { get; } = new() { Kind = TerminalNewCwdKind.Follow };

    public static TerminalNewCwdSpec Home { get; } = new() { Kind = TerminalNewCwdKind.Home };

    public static TerminalNewCwdSpec Current { get; } = new() { Kind = TerminalNewCwdKind.Current };

    public static TerminalNewCwdSpec ForPath(string path) => new()
    {
        Kind = TerminalNewCwdKind.Path,
        Path = path,
    };
}

public sealed record AttachTerminalConfig
{
    public string DefaultShell { get; init; } = "";

    public TerminalShellMode ShellMode { get; init; } = TerminalShellMode.Auto;

    public TerminalNewCwdSpec NewCwd { get; init; } = TerminalNewCwdSpec.Follow;

    public static AttachTerminalConfig Default { get; } = new();
}
