using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;

namespace Hypa.Cli.Attach.Sidebar;

/// <summary>Shared action items for the sidebar tree and the global list.</summary>
public static class HiddenPaneMenuModel
{
    public const string HiddenPanes = "hidden_panes";
    public const string HiddenPanesLabel = "hidden panes";

    public static IReadOnlyList<ContextMenuItem> Items(bool hidden, bool overlayVisible)
    {
        var hide = !hidden || overlayVisible;
        var items = new List<ContextMenuItem>
        {
            new(HiddenPaneActions.ShowNewTab, "Show in new tab"),
            new(HiddenPaneActions.SplitRight, "Split right"),
            new(HiddenPaneActions.SplitBelow, "Split below"),
            new(HiddenPaneActions.ShowModal, "Show modal"),
        };
        if (hide)
            items.Add(new(HiddenPaneActions.Hide, "Hide"));
        items.Add(new(HiddenPaneActions.Close, "Close", KeyActionId.ClosePane));
        return items;
    }

    public static ContextMenuModel ForPane(
        string paneId,
        int col,
        int row,
        int cols,
        int rows,
        bool hidden,
        bool overlayVisible) =>
        ContextMenuModel.Create(
            ContextMenuKind.HiddenPane,
            paneId,
            Items(hidden, overlayVisible),
            col,
            row,
            cols,
            rows);

    public static ContextMenuModel ForList(
        IReadOnlyList<HiddenPaneRecord> catalog,
        int col,
        int row,
        int cols,
        int rows)
    {
        var items = new List<ContextMenuItem>();
        foreach (var pane in catalog)
        {
            if (!pane.Hidden && string.IsNullOrWhiteSpace(pane.ParentPaneId))
                continue;
            var label = HiddenPaneLabelFit.Fit(pane.Label, pane.AgentKind, pane.State, 40);
            items.Add(new(pane.PaneId, label.Length > 0 ? label : pane.PaneId));
        }

        return ContextMenuModel.Create(
            ContextMenuKind.HiddenList,
            "hidden_list",
            items,
            col,
            row,
            cols,
            rows);
    }

    public static string ListRowLabel(HiddenPaneRecord pane, int maxCols) =>
        HiddenPaneLabelFit.Fit(pane.Label, pane.AgentKind, pane.State, maxCols);
}
