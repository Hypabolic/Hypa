namespace Hypa.Runtime.Domain.Sessions;

public sealed record SessionResolveOptions
{
    public Guid? ExplicitSessionId { get; init; }
    public string? AtomicAgentSessionId { get; init; }
    public string? ProjectRoot { get; init; }
    public bool CreateIfMissing { get; init; } = true;
}
