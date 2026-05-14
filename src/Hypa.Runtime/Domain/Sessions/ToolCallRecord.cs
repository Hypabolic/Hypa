namespace Hypa.Runtime.Domain.Sessions;

public sealed record ToolCallRecord : EvidenceRecord
{
    public override string Kind => "ToolCall";
    public string ToolName { get; init; } = string.Empty;
    public string Args { get; init; } = string.Empty;
    public string ArgsHash { get; init; } = string.Empty;
    public string Result { get; init; } = string.Empty;
    public string OutputHash { get; init; } = string.Empty;
    public long DurationMs { get; init; }
}
