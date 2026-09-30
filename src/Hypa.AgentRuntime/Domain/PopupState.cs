namespace Hypa.AgentRuntime.Domain;

/// <summary>
/// Session-modal popup. Not a pane. Has no public id and is not persisted.
/// </summary>
public sealed record PopupState
{
    public required string Command { get; init; }

    public IReadOnlyList<string> Args { get; init; } = [];

    public string? Cwd { get; init; }

    public int AreaCols { get; init; }

    public int AreaRows { get; init; }

    public required int InnerCols { get; init; }

    public required int InnerRows { get; init; }

    public DateTimeOffset StartedAt { get; init; }
}
