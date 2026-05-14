namespace Hypa.Runtime.Domain.Hooks;

public sealed record AgentHookOutput(
    int ExitCode,
    string? JsonBody
);
