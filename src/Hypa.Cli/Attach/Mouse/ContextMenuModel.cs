using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.Plugins;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Plugins;

namespace Hypa.Cli.Attach.Mouse;

public enum ContextMenuKind
{
    Pane,
    Tab,
    Workspace,
    Cube,
    Global,
    CollectionItem,
    HiddenPane,
    HiddenList,
}

public sealed record ContextMenuItem(
    string Id,
    string Label,
    KeyActionId? Action = null,
    bool Enabled = true,
    string? PluginQualifiedActionId = null);

public sealed class ContextMenuModel
{
    public const string Rename = "rename";
    public const string ClearName = "clear_name";
    public const string Swap = "swap";
    public const string SplitRight = "split_right";
    public const string SplitDown = "split_down";
    public const string Zoom = "zoom";
    public const string RightClickHypa = "right_click_hypa";
    public const string RightClickPane = "right_click_pane";
    public const string Close = "close";
    public const string NewTab = "new_tab";
    public const string Detach = "detach";
    public const string Connect = SidebarCubeRowActions.Connect;
    public const string MoveWork = SidebarCubeRowActions.MoveWork;
    public const string RevokeDevice = "revoke_device";
    public const string RevokeDeviceLabel = "Remove";
    public const string Transfer = "transfer";
    public const string TransferLabel = "Transfer";
    public const string NewWorktree = "new_worktree";
    public const string OpenWorktree = "open_worktree";
    public const string RemoveWorktree = "remove_worktree";
    public const string ToggleWorktreeGroup = "toggle_worktree_group";

    private ContextMenuModel(
        ContextMenuKind kind,
        string targetId,
        IReadOnlyList<ContextMenuItem> items,
        CellRect rect)
    {
        Kind = kind;
        TargetId = targetId;
        Items = items;
        Rect = rect;
    }

    public ContextMenuKind Kind { get; }

    public string TargetId { get; }

    public IReadOnlyList<ContextMenuItem> Items { get; }

    public CellRect Rect { get; }

    public int Selected { get; private set; }

    public bool HasDetach =>
        Items.Any(i => i.Id == Detach || i.Action is KeyActionId.Detach);

    public ContextMenuItem? SelectedItem =>
        Selected >= 0 && Selected < Items.Count ? Items[Selected] : null;

    public const string MoveWorkPrefix = MoveWork + ":";

    public static ContextMenuModel ForPane(
        string paneId,
        int col,
        int row,
        int cols,
        int rows,
        IReadOnlyList<SidebarCubeItem>? cubes = null,
        bool continuityEnabled = false,
        IEnumerable<InstalledPlugin>? plugins = null) =>
        Create(
            ContextMenuKind.Pane,
            paneId,
            PaneItems(cubes, continuityEnabled, plugins),
            col,
            row,
            cols,
            rows);

    public static ContextMenuModel ForTab(
        string tabId,
        int col,
        int row,
        int cols,
        int rows,
        IEnumerable<InstalledPlugin>? plugins = null) =>
        Create(ContextMenuKind.Tab, tabId, TabItems(plugins), col, row, cols, rows);

    public static ContextMenuModel ForWorkspace(
        string workspaceId,
        int col,
        int row,
        int cols,
        int rows,
        bool continuityEnabled = false,
        bool isGit = false,
        bool isLinkedWorktree = false,
        bool hasWorktreeChildren = false,
        bool collapsed = false,
        IEnumerable<InstalledPlugin>? plugins = null) =>
        Create(
            ContextMenuKind.Workspace,
            workspaceId,
            WorkspaceItems(
                continuityEnabled,
                isGit,
                isLinkedWorktree,
                hasWorktreeChildren,
                collapsed,
                plugins),
            col,
            row,
            cols,
            rows);

    public static ContextMenuModel ForCube(
        string placementId,
        int col,
        int row,
        int cols,
        int rows,
        bool continuityEnabled = false) =>
        Create(ContextMenuKind.Cube, placementId, CubeItems(continuityEnabled), col, row, cols, rows);

    public static ContextMenuModel ForGlobal(
        int col,
        int row,
        int cols,
        int rows,
        IEnumerable<InstalledPlugin>? plugins = null) =>
        Create(ContextMenuKind.Global, "global", GlobalItems(plugins), col, row, cols, rows);

    public static ContextMenuModel ForCollectionItem(
        string itemId,
        int col,
        int row,
        int cols,
        int rows,
        IEnumerable<InstalledPlugin>? plugins = null) =>
        Create(
            ContextMenuKind.CollectionItem,
            itemId,
            CollectionItemMenuItems(plugins),
            col,
            row,
            cols,
            rows);

    public static IReadOnlyList<ContextMenuItem> PaneItems(
        IReadOnlyList<SidebarCubeItem>? cubes = null,
        bool continuityEnabled = false,
        IEnumerable<InstalledPlugin>? plugins = null)
    {
        var items = new List<ContextMenuItem>
        {
            new(Rename, "Rename", KeyActionId.RenamePane),
            new(ClearName, "Clear name"),
            new(Swap, "Swap with focused pane"),
            new(SplitRight, "Split right", KeyActionId.SplitVertical),
            new(SplitDown, "Split down", KeyActionId.SplitHorizontal),
            new(Zoom, "Zoom", KeyActionId.Zoom),
            new(RightClickHypa, "Use Hypa right-click menu"),
            new(RightClickPane, "Send right-clicks to pane"),
            new(Close, "Close pane", KeyActionId.ClosePane),
        };
        _ = cubes;
        if (continuityEnabled)
            items.Add(new(Transfer, TransferLabel));
        AppendPluginItems(items, plugins, PluginMenuContexts.Pane);
        return items;
    }

    public static bool IsContinuityAction(string? itemId) =>
        itemId == Transfer
        || IsMoveWork(itemId);

    public static bool IsMoveWork(string? itemId) =>
        !string.IsNullOrWhiteSpace(itemId)
        && (string.Equals(itemId, MoveWork, StringComparison.Ordinal)
            || itemId.StartsWith(MoveWorkPrefix, StringComparison.Ordinal));

    public static string? MoveWorkPlacementId(string? itemId, string? cubePlacementId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return cubePlacementId;
        if (itemId.StartsWith(MoveWorkPrefix, StringComparison.Ordinal))
            return itemId[MoveWorkPrefix.Length..];
        if (string.Equals(itemId, MoveWork, StringComparison.Ordinal))
            return cubePlacementId;
        return null;
    }

    public static IReadOnlyList<ContextMenuItem> TabItems(IEnumerable<InstalledPlugin>? plugins = null)
    {
        var items = new List<ContextMenuItem>
        {
            new(NewTab, "New tab", KeyActionId.NewTab),
            new(Rename, "Rename", KeyActionId.RenameTab),
            new(Close, "Close", KeyActionId.CloseTab),
        };
        AppendPluginItems(items, plugins, PluginMenuContexts.Tab);
        return items;
    }

    public static IReadOnlyList<ContextMenuItem> WorkspaceItems(
        bool continuityEnabled = false,
        bool isGit = false,
        bool isLinkedWorktree = false,
        bool hasWorktreeChildren = false,
        bool collapsed = false,
        IEnumerable<InstalledPlugin>? plugins = null)
    {
        var items = new List<ContextMenuItem>
        {
            new(Rename, "Rename", KeyActionId.RenameWorkspace),
            new(Close, hasWorktreeChildren ? "Close group" : "Close", KeyActionId.CloseWorkspace),
        };
        if (isLinkedWorktree)
            items.Add(new(RemoveWorktree, "Delete worktree checkout...", KeyActionId.RemoveWorktree));
        else if (isGit)
        {
            items.Add(new(NewWorktree, "New worktree", KeyActionId.NewWorktree));
            items.Add(new(OpenWorktree, "Open worktree...", KeyActionId.OpenWorktree));
        }

        if (hasWorktreeChildren)
            items.Add(new(ToggleWorktreeGroup, collapsed ? "Expand" : "Collapse"));
        if (continuityEnabled)
            items.Add(new(Transfer, TransferLabel));
        AppendPluginItems(items, plugins, PluginMenuContexts.WorkspaceRow);
        return items;
    }

    public static IReadOnlyList<ContextMenuItem> CubeItems(bool continuityEnabled = false)
    {
        var items = new List<ContextMenuItem>
        {
            new(Connect, SidebarCubeRowActions.ConnectLabel),
        };
        if (continuityEnabled)
            items.Add(new(MoveWork, SidebarCubeRowActions.MoveWorkLabel));
        items.Add(new(RevokeDevice, RevokeDeviceLabel));
        return items;
    }

    public static IReadOnlyList<ContextMenuItem> GlobalItems(IEnumerable<InstalledPlugin>? plugins = null) =>
        Hypa.Cli.Attach.Sidebar.GlobalMenuModel.Items(plugins);

    public static IReadOnlyList<ContextMenuItem> CollectionItemMenuItems(
        IEnumerable<InstalledPlugin>? plugins = null)
    {
        var items = new List<ContextMenuItem>();
        AppendPluginItems(items, plugins, PluginMenuContexts.CollectionItem);
        return items;
    }

    private static void AppendPluginItems(
        List<ContextMenuItem> items,
        IEnumerable<InstalledPlugin>? plugins,
        string context)
    {
        if (plugins is null)
            return;
        foreach (var pluginItem in PluginChromeCatalog.MenuItems(plugins, context))
            items.Add(pluginItem);
    }

    public void Move(int delta)
    {
        if (Items.Count == 0)
            return;
        var next = Selected + delta;
        if (next < 0)
            next = Items.Count - 1;
        else if (next >= Items.Count)
            next = 0;
        Selected = next;
    }

    public bool TryHit(int col, int row, out int index)
    {
        index = -1;
        if (!Rect.Contains(col, row))
            return false;
        var i = row - Rect.Row;
        if (i < 0 || i >= Items.Count)
            return false;
        index = i;
        Selected = i;
        return true;
    }

    internal static ContextMenuModel Create(
        ContextMenuKind kind,
        string targetId,
        IReadOnlyList<ContextMenuItem> items,
        int col,
        int row,
        int cols,
        int rows)
    {
        var width = 1;
        foreach (var item in items)
            width = Math.Max(width, item.Label.Length + 2);
        width = Math.Min(width, Math.Max(1, cols));
        var height = Math.Min(items.Count, Math.Max(1, rows));
        var left = col;
        if (left + width > cols)
            left = Math.Max(0, cols - width);
        var top = row;
        if (top + height > rows)
            top = Math.Max(0, rows - height);
        return new ContextMenuModel(kind, targetId, items, new CellRect(left, top, width, height));
    }
}
