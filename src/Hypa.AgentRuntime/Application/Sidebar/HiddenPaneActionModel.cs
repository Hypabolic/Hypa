using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application.Sidebar;

public enum HiddenPaneAction
{
    ShowNewTab,
    SplitRight,
    SplitBelow,
    ShowModal,
    Hide,
    Close,
}

public sealed record HiddenPaneActionRequest
{
    public required HiddenPaneAction Action { get; init; }
    public required string PaneId { get; init; }
    public string? CurrentTabId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? PriorTabId { get; init; }
    public string? PriorWorkspaceId { get; init; }
    public string? VisibleTargetPaneId { get; init; }

    /// <summary>
    /// Target-scoped resize or input lease for the hidden pane.
    /// Do not pass the current tiled pane lease.
    /// </summary>
    public string? LeaseId { get; init; }
    public string? AttachClientId { get; init; }
    public bool OverlayVisible { get; init; }
}

public sealed record HiddenPanePlannedCall
{
    public required string Method { get; init; }
    public string? PaneId { get; init; }
    public string? TabId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? TargetPaneId { get; init; }
    public string? Direction { get; init; }
    public string? Mode { get; init; }
    public string? LeaseId { get; init; }
    public string? AttachClientId { get; init; }
    public bool? CreatePane { get; init; }
    public bool IsCleanup { get; init; }
}

public sealed record HiddenPaneActionPlan
{
    public IReadOnlyList<HiddenPanePlannedCall> Calls { get; init; } = [];
    public bool ConfirmClose { get; init; }
    public bool CreateEmptyTab { get; init; }
    public bool CleanupCreatedTabOnFailure { get; init; }
    public bool FocusCreatedTab { get; init; }
    public bool RestoreFocusOnFailure { get; init; }
    public string? PriorTabId { get; init; }
    public string? PriorWorkspaceId { get; init; }
    public string PaneId { get; init; } = "";
}

public sealed record HiddenPaneActionError
{
    public const string NotFoundCode = "not_found";
    public const string NoVisibleTargetCode = "no_visible_target";
    public const string MissingLeaseCode = "missing_lease";
    public const string MissingAttachClientCode = "missing_attach_client";
    public const string InvalidActionCode = "invalid_action";

    public required string Code { get; init; }
    public required string Message { get; init; }

    public static HiddenPaneActionError NotFound(string message) =>
        new() { Code = NotFoundCode, Message = message };

    public static HiddenPaneActionError NoVisibleTarget(string message) =>
        new() { Code = NoVisibleTargetCode, Message = message };

    public static HiddenPaneActionError MissingLease(string message) =>
        new() { Code = MissingLeaseCode, Message = message };

    public static HiddenPaneActionError MissingAttachClient(string message) =>
        new() { Code = MissingAttachClientCode, Message = message };

    public static HiddenPaneActionError InvalidAction(string message) =>
        new() { Code = InvalidActionCode, Message = message };
}

public static class HiddenPaneActions
{
    public const string ShowNewTab = "show_new_tab";
    public const string SplitRight = "split_right";
    public const string SplitBelow = "split_below";
    public const string ShowModal = "show_modal";
    public const string Hide = "hide";
    public const string Close = "close";

    public static IReadOnlyList<(string Id, string Label, HiddenPaneAction Action)> All { get; } =
    [
        (ShowNewTab, "Show in new tab", HiddenPaneAction.ShowNewTab),
        (SplitRight, "Split right", HiddenPaneAction.SplitRight),
        (SplitBelow, "Split below", HiddenPaneAction.SplitBelow),
        (ShowModal, "Show modal", HiddenPaneAction.ShowModal),
        (Hide, "Hide", HiddenPaneAction.Hide),
        (Close, "Close", HiddenPaneAction.Close),
    ];

    public static HiddenPaneAction? Parse(string? id) =>
        id switch
        {
            ShowNewTab => HiddenPaneAction.ShowNewTab,
            SplitRight => HiddenPaneAction.SplitRight,
            SplitBelow => HiddenPaneAction.SplitBelow,
            ShowModal => HiddenPaneAction.ShowModal,
            Hide => HiddenPaneAction.Hide,
            Close => HiddenPaneAction.Close,
            _ => null,
        };
}

/// <summary>
/// Overlay stays <c>placement=hidden</c>. Local owner visibility is
/// separate and informs Hide.
/// </summary>
public static class HiddenPaneOverlayVisibility
{
    public const string OverlayMode = "overlay";

    public static string? Apply(
        string? currentOverlayPaneId,
        string? attachClientId,
        string? eventPaneId,
        string? eventMode,
        string? eventAttachClientId)
    {
        if (string.IsNullOrWhiteSpace(eventPaneId))
            return currentOverlayPaneId;

        if (string.Equals(eventMode, OverlayMode, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(attachClientId)
                && string.Equals(eventAttachClientId, attachClientId, StringComparison.Ordinal))
            {
                return eventPaneId;
            }

            return currentOverlayPaneId;
        }

        return string.Equals(currentOverlayPaneId, eventPaneId, StringComparison.Ordinal)
            ? null
            : currentOverlayPaneId;
    }
}

public static class HiddenPaneVisibleTarget
{
    public static string? Find(
        IReadOnlyList<HiddenPaneRecord> panes,
        string? currentTabId,
        string excludePaneId)
    {
        if (string.IsNullOrWhiteSpace(currentTabId))
            return null;
        foreach (var pane in panes)
        {
            if (pane.Hidden)
                continue;
            if (!string.Equals(pane.TabId, currentTabId, StringComparison.Ordinal))
                continue;
            if (string.Equals(pane.PaneId, excludePaneId, StringComparison.Ordinal))
                continue;
            if (string.Equals(pane.Placement, PanePlacementWire.Hidden, StringComparison.OrdinalIgnoreCase))
                continue;
            return pane.PaneId;
        }

        return null;
    }
}
