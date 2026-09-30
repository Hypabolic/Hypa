using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Theme;

namespace Hypa.Cli.Attach.Worktrees;

public sealed record WorktreeDialogLayout(
    CellRect Panel,
    CellRect Primary,
    CellRect Cancel,
    CellRect? Input,
    CellRect? Search,
    IReadOnlyList<(CellRect Rect, int Index)> Rows);

/// <summary>
// / Host-cell worktree dialogs.
/// <c>src/client/shell/worktree_overlays.rs</c>.
/// </summary>
public static class WorktreeDialogPainter
{
    public const string CreateTitle = "new worktree";
    public const string OpenTitle = "open worktree";
    public const string RemoveTitle = " delete worktree checkout?";
    public const string CreatePrimary = " ↵ create and open ";
    public const string OpenPrimary = " ↵ open ";
    public const string RemovePrimary = " ↵ remove ";
    public const string ForcePrimary = " ↵ delete anyway ";
    public const string CancelLabel = " esc cancel ";

    public static WorktreeDialogLayout Measure(WorktreeDialogModel model, int cols, int rows)
    {
        ArgumentNullException.ThrowIfNull(model);
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        return model.Kind switch
        {
            WorktreeDialogKind.Create => MeasureCreate(model.Create, cols, rows),
            WorktreeDialogKind.Open => MeasureOpen(model.Open, cols, rows),
            WorktreeDialogKind.Remove => MeasureRemove(model.Remove, cols, rows),
            _ => new WorktreeDialogLayout(default, default, default, null, null, []),
        };
    }

    internal static void Stamp(
        IHostCellSink sink,
        WorktreeDialogModel model,
        int cols,
        int rows,
        ThemePalette? theme = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(model);
        if (!model.IsOpen)
            return;
        theme ??= ThemePalette.Catppuccin;
        var layout = Measure(model, cols, rows);
        model.Layout = layout;
        if (layout.Panel.Cols <= 0 || layout.Panel.Rows <= 0)
            return;
        for (var r = layout.Panel.Row; r < layout.Panel.EndRow; r++)
            Write(sink, layout.Panel.Col, r, new string(' ', layout.Panel.Cols), theme.Text, theme.PanelBg, layout.Panel.Cols);

        switch (model.Kind)
        {
            case WorktreeDialogKind.Create:
                StampCreate(sink, model.Create!, layout, theme);
                break;
            case WorktreeDialogKind.Open:
                StampOpen(sink, model.Open!, layout, theme);
                break;
            case WorktreeDialogKind.Remove:
                StampRemove(sink, model.Remove!, layout, theme);
                break;
        }
    }

    public static string Paint(WorktreeDialogModel model, int cols, int rows, ThemePalette? theme = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        theme ??= ThemePalette.Catppuccin;
        var host = new HostFrame();
        host.Resize(Math.Max(1, cols), Math.Max(1, rows));
        Stamp(new HostFrameCellSink(host), model, cols, rows, theme);
        var sb = new StringBuilder();
        for (var r = 0; r < host.Rows; r++)
        {
            for (var c = 0; c < host.Cols; c++)
                sb.Append(host.CellAt(c, r).Text);
            if (r + 1 < host.Rows)
                sb.Append('\n');
        }

        return sb.ToString();
    }

    private static WorktreeDialogLayout MeasureCreate(WorktreeCreateState? create, int cols, int rows)
    {
        _ = create;
        var panel = Center(cols, rows, Math.Min(68, cols), Math.Min(12, rows));
        var inner = Inset(panel);
        var buttons = ButtonRow(inner, 20, 12, inner.Row + 9);
        var input = new CellRect(inner.Col, inner.Row + 3, inner.Cols, 1);
        return new WorktreeDialogLayout(panel, buttons.Primary, buttons.Cancel, input, null, []);
    }

    private static WorktreeDialogLayout MeasureOpen(WorktreeOpenState? open, int cols, int rows)
    {
        var count = open?.Entries.Count ?? 0;
        var height = Math.Clamp(count * 2 + 7, 12, 26);
        var panel = Center(cols, rows, Math.Min(96, cols), Math.Min(height, rows));
        var inner = Inset(panel);
        var search = new CellRect(inner.Col, inner.Row + 1, inner.Cols, 1);
        var body = new CellRect(inner.Col, inner.Row + 3, inner.Cols, Math.Max(1, inner.Rows - 6));
        var hits = new List<(CellRect, int)>();
        if (open is not null)
        {
            var filtered = open.FilteredIndices();
            var visibleCount = Math.Max(1, body.Rows / 2);
            var selectedPos = filtered.ToList().IndexOf(open.Selected);
            if (selectedPos < 0)
                selectedPos = 0;
            var start = Math.Min(
                Math.Max(0, selectedPos - (visibleCount - 1)),
                Math.Max(0, filtered.Count - visibleCount));
            var visible = 0;
            for (var i = start; i < filtered.Count && visible < visibleCount; i++, visible++)
            {
                var rect = new CellRect(body.Col, body.Row + visible * 2, body.Cols, 2);
                hits.Add((rect, filtered[i]));
            }
        }

        var buttons = ButtonRow(inner, 10, 12, inner.EndRow - 1);
        return new WorktreeDialogLayout(panel, buttons.Primary, buttons.Cancel, null, search, hits);
    }

    private static WorktreeDialogLayout MeasureRemove(WorktreeRemoveState? remove, int cols, int rows)
    {
        _ = remove;
        var panel = Center(cols, rows, Math.Min(72, cols), Math.Min(10, rows));
        var inner = Inset(panel);
        var buttons = ButtonRow(inner, 18, 12, inner.Row + 7);
        return new WorktreeDialogLayout(panel, buttons.Primary, buttons.Cancel, null, null, []);
    }

    private static void StampCreate(
        IHostCellSink sink,
        WorktreeCreateState create,
        WorktreeDialogLayout layout,
        ThemePalette theme)
    {
        var inner = Inset(layout.Panel);
        Write(sink, inner.Col, inner.Row, CreateTitle, theme.Text, theme.PanelBg, inner.Cols, bold: true);
        Write(sink, inner.Col, inner.Row + 2, " branch", theme.Overlay0, theme.PanelBg, inner.Cols);
        if (layout.Input is { } input)
        {
            Write(sink, input.Col, input.Row, " " + create.Branch, theme.Text, theme.Surface0, input.Cols);
        }

        Write(sink, inner.Col, inner.Row + 5, " checkout", theme.Overlay0, theme.PanelBg, inner.Cols);
        Write(sink, inner.Col, inner.Row + 6, " " + create.CheckoutPath, theme.Subtext0, theme.PanelBg, inner.Cols);
        if (create.Creating)
            Write(sink, inner.Col, inner.Row + 8, " creating…", theme.Accent, theme.PanelBg, inner.Cols);
        else if (create.Error is { } error)
            Write(sink, inner.Col, inner.Row + 8, " " + error, theme.Red, theme.PanelBg, inner.Cols);
        StampButtons(sink, layout, CreatePrimary, theme, theme.Accent);
    }

    private static void StampOpen(
        IHostCellSink sink,
        WorktreeOpenState open,
        WorktreeDialogLayout layout,
        ThemePalette theme)
    {
        var inner = Inset(layout.Panel);
        Write(sink, inner.Col, inner.Row, OpenTitle, theme.Text, theme.PanelBg, inner.Cols, bold: true);
        if (layout.Search is { } search)
        {
            var filter = open.SearchFocused || open.Query.Length > 0
                ? " / " + open.Query
                : " / filter worktrees";
            Write(
                sink,
                search.Col,
                search.Row,
                filter,
                open.SearchFocused ? theme.Text : theme.Overlay0,
                theme.PanelBg,
                search.Cols);
        }

        foreach (var (rect, index) in layout.Rows)
        {
            var entry = open.Entries[index];
            var selected = index == open.Selected;
            var fg = selected ? theme.PanelBg : theme.Text;
            var bg = selected ? theme.Accent : theme.PanelBg;
            Write(sink, rect.Col, rect.Row, " " + entry.Label, fg, bg, rect.Cols, bold: true);
            if (rect.Rows > 1)
            {
                Write(
                    sink,
                    rect.Col,
                    rect.Row + 1,
                    " " + entry.Path,
                    selected ? fg : theme.Overlay0,
                    bg,
                    rect.Cols);
            }
        }

        if (layout.Rows.Count == 0)
            Write(sink, inner.Col, inner.Row + 3, " no matching worktrees", theme.Overlay0, theme.PanelBg, inner.Cols);
        if (open.Opening)
            Write(sink, inner.Col, inner.EndRow - 3, " opening…", theme.Accent, theme.PanelBg, inner.Cols);
        else if (open.Error is { } error)
            Write(sink, inner.Col, inner.EndRow - 3, " " + error, theme.Red, theme.PanelBg, inner.Cols);
        StampButtons(sink, layout, OpenPrimary, theme, theme.Accent);
    }

    private static void StampRemove(
        IHostCellSink sink,
        WorktreeRemoveState remove,
        WorktreeDialogLayout layout,
        ThemePalette theme)
    {
        var inner = Inset(layout.Panel);
        Write(sink, inner.Col, inner.Row, RemoveTitle, theme.Red, theme.PanelBg, inner.Cols, bold: true);
        Write(sink, inner.Col, inner.Row + 1, " This removes the checkout folder:", theme.Text, theme.PanelBg, inner.Cols);
        Write(sink, inner.Col, inner.Row + 2, " " + remove.Path, theme.Subtext0, theme.PanelBg, inner.Cols);
        Write(
            sink,
            inner.Col,
            inner.Row + 3,
            " The branch is not deleted. The workspace will close.",
            theme.Text,
            theme.PanelBg,
            inner.Cols);
        if (remove.ForceConfirmation)
        {
            Write(
                sink,
                inner.Col,
                inner.Row + 4,
                " Dirty or untracked files will be permanently deleted.",
                theme.Red,
                theme.PanelBg,
                inner.Cols);
        }

        if (remove.Removing)
            Write(sink, inner.Col, inner.Row + 5, " removing…", theme.Accent, theme.PanelBg, inner.Cols);
        else if (remove.Error is { } error)
            Write(sink, inner.Col, inner.Row + 5, " " + error, theme.Red, theme.PanelBg, inner.Cols);
        StampButtons(sink, layout, remove.ForceConfirmation ? ForcePrimary : RemovePrimary, theme, theme.Red);
    }

    private static void StampButtons(
        IHostCellSink sink,
        WorktreeDialogLayout layout,
        string primary,
        ThemePalette theme,
        ThemeColor primaryBg)
    {
        Write(sink, layout.Primary.Col, layout.Primary.Row, primary, theme.PanelBg, primaryBg, layout.Primary.Cols, bold: true);
        Write(sink, layout.Cancel.Col, layout.Cancel.Row, CancelLabel, theme.Text, theme.Surface0, layout.Cancel.Cols, bold: true);
    }

    private static CellRect Center(int cols, int rows, int panelCols, int panelRows)
    {
        panelCols = Math.Max(1, Math.Min(panelCols, cols));
        panelRows = Math.Max(1, Math.Min(panelRows, rows));
        return new CellRect(
            Math.Max(0, (cols - panelCols) / 2),
            Math.Max(0, (rows - panelRows) / 2),
            panelCols,
            panelRows);
    }

    private static CellRect Inset(CellRect panel) =>
        new(panel.Col + 1, panel.Row + 1, Math.Max(1, panel.Cols - 2), Math.Max(1, panel.Rows - 2));

    private static (CellRect Primary, CellRect Cancel) ButtonRow(CellRect inner, int primaryCols, int cancelCols, int row)
    {
        primaryCols = Math.Min(primaryCols, inner.Cols);
        cancelCols = Math.Min(cancelCols, Math.Max(1, inner.Cols - primaryCols));
        var y = Math.Clamp(row, inner.Row, inner.EndRow - 1);
        var primary = new CellRect(inner.Col, y, primaryCols, 1);
        var cancel = new CellRect(inner.Col + primaryCols + 1, y, cancelCols, 1);
        return (primary, cancel);
    }

    private static void Write(
        IHostCellSink sink,
        int col,
        int row,
        string text,
        ThemeColor fg,
        ThemeColor bg,
        int cols,
        bool bold = false)
    {
        if (cols <= 0)
            return;
        sink.Write(col, row, SafeDisplayText.PadRight(text, cols), fg, bg, cols, bold: bold);
    }
}
