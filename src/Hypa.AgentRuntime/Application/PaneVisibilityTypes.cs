using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>Expected visibility failure. Does not throw.</summary>
public sealed record PaneVisibilityError
{
    public const string NotFoundCode = "not_found";
    public const string InvalidTargetCode = "invalid_target";
    public const string UnsupportedModeCode = "unsupported_mode";
    public const string DuplicateLeafCode = "duplicate_leaf";

    public required string Code { get; init; }

    public required string Message { get; init; }

    public static PaneVisibilityError NotFound(string message) =>
        new() { Code = NotFoundCode, Message = message };

    public static PaneVisibilityError InvalidTarget(string message) =>
        new() { Code = InvalidTargetCode, Message = message };

    public static PaneVisibilityError UnsupportedMode(string message) =>
        new() { Code = UnsupportedModeCode, Message = message };

    public static PaneVisibilityError DuplicateLeaf(string message) =>
        new() { Code = DuplicateLeafCode, Message = message };
}

public sealed record PaneShowTiledRequest
{
    public required PaneId PaneId { get; init; }

    public TabId? TabId { get; init; }

    public PaneId? TargetPaneId { get; init; }

    public string? Direction { get; init; }

    public double Ratio { get; init; } = 0.5;

    public bool Focus { get; init; } = true;
}

public sealed record PaneHideRequest
{
    public required PaneId PaneId { get; init; }
}

public sealed record PaneVisibilityChange
{
    public required PaneState Pane { get; init; }

    public required TabId SourceTabId { get; init; }

    public required TabId TargetTabId { get; init; }

    public required PanePlacement From { get; init; }

    public required PanePlacement To { get; init; }

    public required bool Changed { get; init; }

    public TabId? CreatedTabId { get; init; }

    /// <summary>
    /// Tab removed because this hide took its last tiled leaf.
    /// Hidden occupants stay alive on <see cref="TargetTabId"/>.
    /// </summary>
    public TabId? ClosedTabId { get; init; }
}

/// <summary>
/// Parent split that held this pane. Inverse of hide is wrap the sibling
// / with the same direction, ratio, and side.
/// </summary>
public sealed record LayoutInsertion
{
    public required string Direction { get; init; }

    public required double Ratio { get; init; }

    public required bool IncomingIsSecond { get; init; }

    public required IReadOnlyList<PaneId> SiblingPaneIds { get; init; }
}

/// <summary>
/// Scoped restore for a failed durable visibility write. Holds this pane's
/// placement, its tab insertion, and the focus this call may have stolen.
/// </summary>
public sealed record VisibilityRestorePoint
{
    public required PaneId PaneId { get; init; }

    public required PanePlacement Placement { get; init; }

    public required TabId TabId { get; init; }

    public required WorkspaceId WorkspaceId { get; init; }

    public LayoutInsertion? Insertion { get; init; }

    public PaneId? TabFocusedPaneId { get; init; }

    public bool Zoomed { get; init; }

    public PaneId? ZoomedPaneId { get; init; }

    public WorkspaceId? FocusedWorkspaceId { get; init; }

    public TabId? FocusedTabId { get; init; }

    /// <summary>
    /// Tab graph before this call. Revert inserts it when a hide removed
    /// the tab after taking its last leaf.
    /// </summary>
    public TabState? SourceTab { get; init; }

    /// <summary>
    /// Session tab this mutation focused. Revert restores prior focus
    /// only while this tab is still focused.
    /// </summary>
    public TabId? OperationFocusedTabId { get; init; }

    /// <summary>
    /// Tab pane focus after this mutation. Revert restores the prior
    /// focused pane only while this pane is still focused.
    /// </summary>
    public PaneId? OperationFocusedPaneId { get; init; }
}
