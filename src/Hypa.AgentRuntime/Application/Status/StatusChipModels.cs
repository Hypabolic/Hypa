using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.AgentRuntime.Application.Status;

public enum StatusChipKind
{
    Session,
    Workspace,
    Pane,
    Agent,
    Resource,
    Error,
}

public sealed record StatusChip
{
    public required StatusChipKind Kind { get; init; }

    public required string Text { get; init; }

    public string? Severity { get; init; }

    public int DisplayWidth { get; init; }
}

public sealed record StatusChipLine
{
    public required IReadOnlyList<StatusChip> Chips { get; init; }

    public required string Text { get; init; }
}

public sealed record StatusChipComposeInput
{
    public string? SessionName { get; init; }

    public string? WorkspaceLabel { get; init; }

    public string? PaneLabel { get; init; }

    public string? PaneId { get; init; }

    public string? AgentName { get; init; }

    public string? AgentState { get; init; }

    public StatusIndicatorStyle StatusIndicators { get; init; } = StatusIndicatorStyle.Dots;

    public IReadOnlyList<StatusChip> Extras { get; init; } = [];

    public string? Error { get; init; }

    public int MaxCols { get; init; } = 80;
}
