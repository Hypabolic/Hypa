using Hypa.AgentRuntime.Domain.Worktrees;

namespace Hypa.AgentRuntime.Domain;

public sealed record TabState
{
    public required TabId Id { get; init; }
    public required WorkspaceId WorkspaceId { get; init; }
    public string Label { get; init; } = "main";
    public int Ordinal { get; init; }
    public PaneId? FocusedPaneId { get; init; }
    /// <summary>Stable directory source, promoted when it leaves the tiled layout.</summary>
    public PaneId? IdentityPaneId { get; init; }
    public IReadOnlyList<PaneId> PaneIds { get; init; } = [];
    /// <summary>Off-layout occupants. Disjoint from <see cref="PaneIds"/> / layout leaves.</summary>
    public IReadOnlyList<PaneId> HiddenPaneIds { get; init; } = [];
    public LayoutNode? LayoutRoot { get; init; }
    public bool Zoomed { get; init; }
    public PaneId? ZoomedPaneId { get; init; }
    /// <summary>
    /// True when create or rename supplied a name. Auto names stay false.
    /// </summary>
    public bool CustomLabel { get; init; }

    /// <summary>
    /// Tab that had focus when this tab was opened to reveal a hidden pane.
    /// Memory only. A hide that removes the last leaf returns focus here
    /// when this tab still exists in the workspace.
    /// </summary>
    public TabId? OpenedFromTabId { get; init; }
}

public sealed record WorkspaceState
{
    public required WorkspaceId Id { get; init; }
    public int Ordinal { get; init; }
    public string Label { get; init; } = string.Empty;
    public bool CustomLabel { get; init; }
    public string Cwd { get; init; } = Environment.CurrentDirectory;
    public TabId? FocusedTabId { get; init; }
    public IReadOnlyList<TabId> TabIds { get; init; } = [];
    public AtomicBinding? Binding { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public WorktreeSpaceMembership? Worktree { get; init; }

    /// <summary>
    /// True when <c>workspace.create</c> requested a default pane that has not
    /// registered yet. Intentional <c>create_pane = false</c> stays false.
    /// Restore drops a pending workspace that still has no pane.
    /// </summary>
    public bool DefaultPanePending { get; init; }
}
