using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.AgentRuntime.Application.Sidebar;

public enum SidebarRowKind
{
    Workspace,
    Tab,
    Pane,
    Agent,
    Cube,
    Header,
    FooterNew,
    FooterMenu,
    Group,
    HiddenPane,
    CollectionItem,
    Notice,
}

public enum SidebarCubeKind
{
    Local,
    Peer,
    Cube,
}

public enum SidebarCubeReachability
{
    Local,
    Reachable,
    Unreachable,
    Asleep,
}

public enum SidebarCubeCatalogState
{
    Ready,
    Unavailable,
}

public enum SidebarFocusKind
{
    Workspace,
    Tab,
    Pane,
    Agent,
}

public enum SidebarCollapseDisplay
{
    Expanded,
    Compact,
    Hidden,
}

/// <summary>
/// <c>endpoint_status_presentation</c> kinds.
/// </summary>
public enum CubeTrailingStatusKind
{
    None,
    Online,
    Connecting,
    Disabled,
}

public enum SidebarPaneSlot
{
    Spaces,
    Agents,
    Cubes,
    Resource,
}

public enum SidebarPrefixRole
{
    None,
    TreeBranch,
    TreeLast,
    TreeBar,
}

public enum SidebarActionAlign
{
    Left,
    Right,
}

public sealed record SidebarRowToken(
    string Id,
    string Text,
    SidebarTokenStyle? Style = null);

public sealed record SidebarActionHit(
    string Id,
    string Label,
    SidebarActionAlign Align,
    int Width,
    bool AttentionBadgeVisible = false);

public sealed record SidebarGitInfo(string Branch = "", string Status = "", string RepositoryName = "");

public sealed record SidebarWorkspaceItem
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string Cwd { get; init; } = "";
    /// <summary>Host-resolved metadata; null for older hosts using client probes.</summary>
    public SidebarGitInfo? Git { get; init; }
    public string? FocusedTabId { get; init; }
    public int Order { get; init; }
    public string WorktreeKey { get; init; } = "";
    public string WorktreeLabel { get; init; } = "";
    public bool IsLinkedWorktree { get; init; }
    public string Branch { get; init; } = "";
    public bool CustomLabel { get; init; }
    public IReadOnlyDictionary<string, string> Tokens { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed record SidebarTabItem
{
    public required string Id { get; init; }
    public required string WorkspaceId { get; init; }
    public required string Label { get; init; }
    public string? FocusedPaneId { get; init; }
    public int Ordinal { get; init; }
}

public sealed record SidebarPaneItem
{
    public required string Id { get; init; }
    public required string TabId { get; init; }
    public required string WorkspaceId { get; init; }
    public string Label { get; init; } = "";
    public string? Agent { get; init; }
    public string State { get; init; } = SidebarTokenGrammar.Unknown;
    public string TerminalTitle { get; init; } = "";
    public bool Hidden { get; init; }
    public string Placement { get; init; } = "tiled";
    public string? ParentPaneId { get; init; }
    public IReadOnlyDictionary<string, string> Tokens { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed record SidebarCubeItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required SidebarCubeKind Kind { get; init; }
    public required SidebarCubeReachability Reachability { get; init; }
    public string? WorkTitle { get; init; }
    public string? ProviderSuffix { get; init; }
    public bool ConnectEnabled { get; init; } = true;
    public string? ConnectDetail { get; init; }
    public bool ShowPlacementId { get; init; }
    public string? EnrolledDeviceId { get; init; }
}

public sealed record SidebarRowAction
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required bool Enabled { get; init; }
}

public static class SidebarCubeRowActions
{
    public const string Connect = "connect";
    public const string MoveWork = "move_work";
    public const string ConnectLabel = "Connect";
    public const string MoveWorkLabel = "Move Work here";

    public static IReadOnlyList<SidebarRowAction> Default { get; } =
    [
        new() { Id = Connect, Label = ConnectLabel, Enabled = true },
        new() { Id = MoveWork, Label = MoveWorkLabel, Enabled = true },
    ];

    public static IReadOnlyList<SidebarRowAction> ForCube(
        SidebarCubeItem cube,
        bool continuityEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(cube);
        return
        [
            new() { Id = Connect, Label = ConnectLabel, Enabled = cube.ConnectEnabled },
            new() { Id = MoveWork, Label = MoveWorkLabel, Enabled = continuityEnabled },
        ];
    }
}

public sealed record SidebarPaintedRow
{
    public required string Id { get; init; }
    public required SidebarRowKind Kind { get; init; }
    public required string Label { get; init; }
    public string State { get; init; } = SidebarTokenGrammar.Unknown;
    public string CompactLabel { get; init; } = "";
    public string? WorkspaceId { get; init; }
    public string? TabId { get; init; }
    public string? PaneId { get; init; }
    public string? PlacementId { get; init; }
    public bool Selected { get; init; }

    /// <summary>
    /// highlight. <see cref="Selected"/> is workspace or pane focus.
    /// Header-only: markers and name emphasis use these flags.
    /// </summary>
    public bool Navigated { get; init; }

    /// <summary>
    /// <c>697-711</c> paint <c>active_row_bg</c> on the whole card
    /// rect. Distinct from <see cref="Selected"/> so metadata rows
    /// do not repeat the name marker.
    /// </summary>
    public bool CardFocused { get; init; }

    /// <summary>
    /// <c>697-711</c> paint <c>selection_bg</c> on the whole card
    /// rect. Distinct from <see cref="Navigated"/>.
    /// </summary>
    public bool CardNavigated { get; init; }
    public IReadOnlyList<SidebarRowAction> Actions { get; init; } = [];
    public IReadOnlyList<SidebarRowToken> Tokens { get; init; } = [];
    public SidebarPrefixRole PrefixRole { get; init; }
    public int IndentCols { get; init; }
    public int CardRowIndex { get; init; }
    public int NameColumn { get; init; }
    public SidebarPaneSlot PaneSlot { get; init; }
    public string ScrollId { get; init; } = "";
    public string? GroupKey { get; init; }

    /// <summary>
    /// right-aligned endpoint status glyph.
    /// </summary>
    public string? TrailingStatus { get; init; }

    public CubeTrailingStatusKind TrailingStatusKind { get; init; }

    /// <summary>
    /// parent uses <c>▸</c>. Expanded parent uses <c>▾</c>.
    /// </summary>
    public bool GroupCollapsed { get; init; }
    public bool Expandable { get; init; }
    public bool TreeCollapsed { get; init; }
    public bool Hidden { get; init; }

    public SidebarCollectionActivation? CollectionActivation { get; init; }
}

public sealed record SidebarSectionView
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required int Order { get; init; }
    public required bool Visible { get; init; }
    public required bool Collapsed { get; init; }
    public string? SelectedRowId { get; init; }
    public SidebarFocusKind? SelectedKind { get; init; }
    public string EmptyText { get; init; } = "";
    public IReadOnlyList<SidebarPaintedRow> Rows { get; init; } = [];
}

public sealed record SidebarFooterHit(string Id, string Label);

public sealed record SidebarPaneView
{
    public required string Id { get; init; }
    public required SidebarPaneSlot Slot { get; init; }
    public required string Header { get; init; }
    public string Title { get; init; } = "";
    public int Order { get; init; }
    public bool Visible { get; init; } = true;
    public bool Collapsed { get; init; }
    public string? SelectedId { get; init; }
    public SidebarFocusKind? SelectedKind { get; init; }
    public string EmptyText { get; init; } = "";
    public string ScrollId { get; init; } = "";
    public IReadOnlyList<SidebarActionHit> Actions { get; init; } = [];
    public IReadOnlyList<SidebarPaintedRow> Rows { get; init; } = [];

    public SidebarSectionView ToSection() =>
        new()
        {
            Id = Id,
            Title = string.IsNullOrEmpty(Title) ? Header.Trim() : Title,
            Order = Order,
            Visible = Visible,
            Collapsed = Collapsed,
            SelectedRowId = SelectedId,
            SelectedKind = SelectedKind,
            EmptyText = EmptyText,
            Rows = Rows,
        };
}

public sealed record SidebarFrame
{
    public required int Width { get; init; }
    public required SidebarCollapseDisplay Display { get; init; }
    public IReadOnlyList<SidebarPaneView> Panes { get; init; } = [];
    public IReadOnlyList<SidebarSectionView> Sections { get; init; } = [];
    public IReadOnlyList<SidebarFooterHit> Footer { get; init; } = [];

    public SidebarPaneView? Pane(SidebarPaneSlot slot)
    {
        foreach (var pane in Panes)
        {
            if (pane.Slot == slot)
                return pane;
        }

        return null;
    }
}

public sealed record SidebarComposeInput
{
    public required AttachUiConfig Ui { get; init; }
    public IReadOnlyList<SidebarWorkspaceItem> Workspaces { get; init; } = [];
    public IReadOnlyList<SidebarTabItem> Tabs { get; init; } = [];
    public IReadOnlyList<SidebarPaneItem> Panes { get; init; } = [];
    public IReadOnlyList<SidebarCubeItem> Cubes { get; init; } = [];
    public SidebarCubeCatalogState CubesState { get; init; } = SidebarCubeCatalogState.Ready;
    public string? FocusedWorkspaceId { get; init; }
    public string? FocusedTabId { get; init; }
    public string? FocusedPaneId { get; init; }

    /// <summary>
    // / Selected cube.
    /// <c>src/client/shell/endpoints.rs:173</c>
    /// <c>active_endpoint_id</c>. Workspaces compose from that cube.
    /// </summary>
    public string? FocusedCubeId { get; init; }

    /// <summary>
    /// Mux surface currently attached. Distinct from
    /// <see cref="FocusedCubeId"/> during connect.
    /// </summary>
    public string? ConnectedCubeId { get; init; }

    /// <summary>
    // Distinct from
    /// <see cref="FocusedWorkspaceId"/>.
    /// </summary>
    public string? NavigatedWorkspaceId { get; init; }

    /// <summary>
    // Distinct from
    /// <see cref="FocusedPaneId"/>.
    /// </summary>
    public string? NavigatedPaneId { get; init; }
    public bool Expanded { get; init; } = true;
    public bool MouseCapture { get; init; } = true;
    public int RequestedWidth { get; init; }
    public ISidebarGitStatus? Git { get; init; }
    public IReadOnlyDictionary<string, string>? CustomTokens { get; init; }
    public IReadOnlySet<string>? CollapsedSectionIds { get; init; }
    public IReadOnlySet<string>? CollapsedWorktreeGroups { get; init; }
    public IReadOnlySet<string>? CollapsedTreeIds { get; init; }
    public bool GlobalMenuAttentionBadgeVisible { get; init; }
    public IReadOnlyList<string> AgentOrder { get; init; } = [];
    public string? AgentViewLabel { get; init; }
    public bool? AgentViewHasSort { get; init; }
    public IReadOnlyList<SidebarPluginResourceView> PluginResources { get; init; } = [];
    public IReadOnlySet<string>? LinkedPluginIds { get; init; }
    public bool ContinuityEnabled { get; init; }
    public SidebarUpdateNotice? UpdateNotice { get; init; }
}

public sealed record ResolvedSidebarSection
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required int Order { get; init; }
    public required bool Collapsed { get; init; }
    public required AttachSidebarSectionConfig Config { get; init; }
}
