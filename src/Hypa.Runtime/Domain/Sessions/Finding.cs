namespace Hypa.Runtime.Domain.Sessions;

public sealed record Finding : EvidenceRecord
{
    public override string Kind => "Finding";
    public string Summary { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string[] Tags { get; init; } = [];
}
