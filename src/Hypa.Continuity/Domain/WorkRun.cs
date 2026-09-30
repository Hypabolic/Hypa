namespace Hypa.Continuity.Domain;

/// <summary>Durable Work identifier. Distinct from Hypa ContextSession.</summary>
public readonly record struct WorkId
{
    public string Value { get; } = "";

    private WorkId(string value) => Value = value;

    public override string ToString() => Value;

    public static WorkId New()
    {
        if (!TryParse("wrk_" + Guid.NewGuid().ToString("N"), out var id))
            throw new InvalidOperationException("work id mint failed");
        return id;
    }

    public static WorkId Parse(string value)
    {
        if (!TryParse(value, out var id))
            throw new ArgumentException("work id is invalid", nameof(value));
        return id;
    }

    public static bool TryParse(string? value, out WorkId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (!trimmed.StartsWith("wrk_", StringComparison.Ordinal))
            return false;
        if (trimmed.Length is < 5 or > 128)
            return false;

        foreach (var c in trimmed)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                return false;
        }

        id = new WorkId(trimmed);
        return true;
    }
}

/// <summary>Run status per cubes-v0-spec §4.1.</summary>
public enum RunStatus
{
    Active = 0,
    Fencing = 1,
    Dead = 2,
    Failed = 3,
}

/// <summary>
/// Durable Work. Generation lives on Run rows; active ownership is the unique active Run.
/// </summary>
public sealed record WorkRecord
{
    public required WorkId Id { get; init; }
    public required string HarnessAdapterId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>One Run of a Work on a placement at a generation.</summary>
public sealed record RunRecord
{
    public required WorkId WorkId { get; init; }
    public required long Generation { get; init; }
    public required string PlacementId { get; init; }
    public required string MuxEndpoint { get; init; }
    public required RunStatus Status { get; init; }
    public ResumeEvidence ResumeEvidence { get; init; } = ResumeEvidence.None;
    public string? Home { get; init; }
    public string? Cwd { get; init; }
    public string? AttemptId { get; init; }
    public string? WorkPackSha256 { get; init; }
}

/// <summary>Result of a generation-fenced ownership check or mutation.</summary>
public sealed record FenceResult
{
    public required bool Ok { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }
    public WorkRecord? Work { get; init; }
    public RunRecord? Run { get; init; }
    public RunRecord? SourceRun { get; init; }

    public static FenceResult Pass(WorkRecord work, RunRecord? run = null, RunRecord? source = null) =>
        new() { Ok = true, Work = work, Run = run, SourceRun = source };

    public static FenceResult Fail(string reason, string detail) =>
        new() { Ok = false, Reason = reason, Detail = detail };
}

/// <summary>
/// Active Work plus its active Run. Menu Move Work uses this list.
/// </summary>
public sealed record ActiveWorkRun
{
    public required WorkRecord Work { get; init; }
    public required RunRecord Run { get; init; }
}

/// <summary>
/// Active Run selected as an attach target. Missing Work and fenced Runs fail closed.
/// </summary>
public sealed record AttachRunResult
{
    public required bool Ok { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }
    public WorkRecord? Work { get; init; }
    public RunRecord? Run { get; init; }

    public static AttachRunResult Pass(WorkRecord work, RunRecord run) =>
        new() { Ok = true, Work = work, Run = run };

    public static AttachRunResult Fail(string reason, string detail) =>
        new() { Ok = false, Reason = reason, Detail = detail };
}
