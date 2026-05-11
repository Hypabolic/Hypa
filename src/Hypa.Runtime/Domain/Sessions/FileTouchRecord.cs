namespace Hypa.Runtime.Domain.Sessions;

public sealed record FileTouchRecord : EvidenceRecord
{
    public override string Kind => "FileTouch";
    public string Path { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public string? Hash { get; init; }
}
