namespace Hypa.Runtime.Domain.Sessions;

public sealed record SessionBinding
{
    public string? AtomicAgentSessionId { get; init; }
    public string? ExternalRef { get; init; }
}
