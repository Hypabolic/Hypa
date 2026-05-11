namespace Hypa.Runtime.Domain.Sessions;

public sealed record SessionStats
{
    public int ToolCallCount { get; init; }
    public int FileTouchCount { get; init; }
    public long TokensSaved { get; init; }
}
