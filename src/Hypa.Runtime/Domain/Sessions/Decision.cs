namespace Hypa.Runtime.Domain.Sessions;

public sealed record Decision : EvidenceRecord
{
    public override string Kind => "Decision";
    public string Description { get; init; } = string.Empty;
    public string Rationale { get; init; } = string.Empty;
}
