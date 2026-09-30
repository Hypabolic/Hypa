namespace Hypa.AgentRuntime.Domain;

/// <summary>
/// Resume argv for an official stored session reference.
/// </summary>
public sealed record OfficialAgentResumePlan
{
    public required string Agent { get; init; }
    public required IReadOnlyList<string> Argv { get; init; }
    public required string DedupeKey { get; init; }
}
