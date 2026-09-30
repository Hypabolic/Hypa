namespace Hypa.AgentRuntime.Domain;

/// <summary>
/// Opaque Atomic-supplied binding identifiers (run_id / step_id / memory_id / …).
/// Hypa stores and presents these values and does not own Run/Step lifecycle.
/// </summary>
public sealed record AtomicBinding
{
    public string? AgentSessionId { get; init; }
    public string? RunId { get; init; }
    public string? StepId { get; init; }
    public string? MemoryId { get; init; }
    public string? ProjectRoot { get; init; }

    /// <summary>Opaque tenant claim. Required for governed/remote placement; never inferred.</summary>
    public string? TenantId { get; init; }
}
