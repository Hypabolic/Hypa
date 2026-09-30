using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Integrations;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Theme;

namespace Hypa.Cli.Attach.Settings;

/// <summary>Paints the settings overlay. Tabs iterate the registry. Clip with SafeDisplayText.</summary>
public static class SettingsPainter
{
    public const string Title = "settings";
    public const string FooterHint = "tab section";
    public const string FooterApplyLabel = "enter apply";
    public const string FooterCloseLabel = "esc close";
    public const string Footer = "tab section  enter apply  esc close";
    public const int ChromeRows = 4;

    /// <summary>Install result lines stop at this count. Skill lines are separate.</summary>
    private const int InstallMessageLineCap = 6;

    public static SettingsLayout Measure(SettingsOverlayModel model, int cols, int rows)
    {
        ArgumentNullException.ThrowIfNull(model);
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        var panelCols = Math.Min(cols, Math.Max(1, cols >= 76 ? 76 : cols));
        var integrations = model.ActivePage.Kind is SettingsPageKind.Integrations;
        var list = model.Items;
        var maxRows = rows > 4 ? rows - 2 : rows;
        var innerCols = Math.Max(1, panelCols - 2);
        int panelRows;
        if (integrations)
        {
            var messages = IntegrationMessageLines(model, innerCols).Count;
            var body = Math.Max(1, model.Integrations.Count);
            var wanted = 14 + body + messages + IntegrationExtraLines(model, innerCols).Count;
            panelRows = Math.Min(maxRows, Math.Max(22, wanted));
        }
        else
        {
            var wantedRows = ChromeRows + 2 + Math.Max(0, list.Count);
            panelRows = Math.Min(maxRows, Math.Max(8, wantedRows));
        }
        var panelCol = Math.Max(0, (cols - panelCols) / 2);
        var panelRow = Math.Max(0, (rows - panelRows) / 2);
        var panel = new CellRect(panelCol, panelRow, panelCols, panelRows);
        var tabRow = panelRow + 2;
        var tabCol = panelCol + 1;
        var tabs = new List<SettingsTabHit>();
        var used = 0;
        foreach (var page in model.Pages)
        {
            var label = SafeDisplayText.Encode(model.TabLabel(page));
            var width = SafeDisplayText.Width(label);
            if (width <= 0)
                continue;
            if (used > 0)
            {
                if (used + 2 + width > innerCols)
                    break;
                used += 2;
            }
            else if (width > innerCols)
            {
                label = SafeDisplayText.Clip(label, innerCols);
                width = SafeDisplayText.Width(label);
            }

            if (used + width > innerCols)
                break;
            tabs.Add(new SettingsTabHit(page.Id, new CellRect(tabCol + used, tabRow, width, 1)));
            used += width;
        }

        var items = new List<SettingsItemHit>();
        var footerRow = panel.EndRow - 2;
        var headerRows = integrations ? 3 : 0;
        var firstItemRow = panel.Row + 4 + headerRows;
        var visibleList = integrations && model.LoadingIntegrations ? 0 : list.Count;
        var visibleSlots = Math.Max(0, footerRow - firstItemRow);
        var pin = PinnedIntegrationLineCount(model, innerCols, visibleSlots);
        var rowSlots = Math.Max(0, visibleSlots - pin);
        var scrollSpan = integrations ? IntegrationScrollSpan(model, innerCols) : 0;
        model.NoteIntegrationScroll(scrollSpan, rowSlots);
        model.SyncListOffset(rowSlots);
        var offset = model.ListOffset;
        var itemRow = firstItemRow;
        var itemCols = innerCols;
        var rowEnd = firstItemRow + rowSlots;
        FitIntegrationRow(itemCols, out var boxCols, out _, out _, out _, out _);
        for (var i = offset; i < visibleList && itemRow < rowEnd; i++, itemRow++)
        {
            CellRect? box = null;
            if (integrations && boxCols >= 3)
            {
                var boxStart = boxCols >= 5 ? 1 : 0;
                box = new CellRect(panelCol + 1 + boxStart, itemRow, 3, 1);
            }

            items.Add(new SettingsItemHit(i, new CellRect(panelCol + 1, itemRow, itemCols, 1), box));
        }

        var innerStart = panelCol + 1;
        var innerEnd = panel.EndCol - 1;
        var closeWidth = SafeDisplayText.Width(FooterCloseLabel);
        var closeCol = Math.Max(innerStart, innerEnd - closeWidth);
        var apply = default(CellRect);
        if (model.ShowsApply)
        {
            var applyWidth = SafeDisplayText.Width(FooterApplyLabel);
            var applyCol = closeCol - 2 - applyWidth;
            apply = applyCol >= innerStart
                ? new CellRect(applyCol, footerRow, applyWidth, 1)
                : default;
        }

        var closeWidthFit = Math.Max(0, Math.Min(closeWidth, innerEnd - closeCol));
        var close = new CellRect(closeCol, footerRow, closeWidthFit, 1);
        return new SettingsLayout(panel, tabs, items, close, apply, offset);
    }

    public static string Paint(SettingsOverlayModel model, int cols, int rows, ThemePalette? theme = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        theme ??= ThemePalette.Catppuccin;
        var layout = Measure(model, cols, rows);
        model.Layout = layout;
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);

        var sb = new StringBuilder();
        sb.Append(SnapshotPainter.HideCursor);
        sb.Append(SnapshotPainter.EraseDisplay);
        sb.Append(SnapshotPainter.Home);
        for (var r = 0; r < rows; r++)
        {
            sb.Append(SnapshotPainter.CursorAddress(0, r));
            ThemeSgr.WriteStyled(sb, theme.Overlay0, theme.PanelBg, new string(' ', cols));
            if (r >= layout.Panel.Row && r < layout.Panel.EndRow)
            {
                sb.Append(SnapshotPainter.CursorAddress(layout.Panel.Col, r));
                PaintPanelRow(sb, model, layout, r, theme);
            }
        }

        sb.Append(SnapshotPainter.HideCursor);
        return sb.ToString();
    }

    /// <summary>
    /// into the same host <c>Frame</c>.
    /// </summary>
    internal static void Stamp(
        IHostCellSink sink,
        SettingsOverlayModel model,
        int cols,
        int rows,
        ThemePalette? theme = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(model);
        theme ??= ThemePalette.Catppuccin;
        var layout = Measure(model, cols, rows);
        model.Layout = layout;
        sink.DimAll();
        ChromeShadow.Stamp(sink, layout.Panel, theme, cols, rows);
        OverlayModalPanel.Stamp(sink, layout.Panel, theme);
        for (var r = layout.Panel.Row + 1; r < layout.Panel.EndRow - 1; r++)
            StampPanelRow(sink, model, layout, r, theme);
    }

    private static void StampPanelRow(
        IHostCellSink sink,
        SettingsOverlayModel model,
        SettingsLayout layout,
        int row,
        ThemePalette theme)
    {
        var panel = layout.Panel;
        var rel = row - panel.Row;
        var innerCol = panel.Col + 1;
        var innerCols = Math.Max(1, panel.Cols - 2);
        if (rel == 2)
        {
            StampTabRow(sink, model, layout, theme);
            return;
        }

        if (rel == panel.Rows - 2)
        {
            StampFooterRow(sink, model, layout, theme);
            return;
        }

        if (rel == 1)
        {
            sink.Write(
                innerCol,
                row,
                SafeDisplayText.PadRight(" " + model.PageTitle, innerCols),
                theme.Accent,
                theme.Surface0,
                innerCols);
            return;
        }

        if (model.ActivePage.Kind is SettingsPageKind.Integrations)
        {
            var raw = rel - 4;
            if (TryPinnedIntegrationLine(model, innerCols, panel.Rows - 7, raw, out var pinned))
            {
                sink.Write(innerCol, row, ClipLine(pinned, innerCols), theme.Overlay1, theme.Surface0, innerCols);
                return;
            }

            StampIntegrationLine(sink, model, theme, innerCol, row, innerCols, raw);
            return;
        }

        var text = ContentLine(model, layout.ListOffset + rel - 4);
        sink.Write(innerCol, row, SafeDisplayText.PadRight(text, innerCols), theme.Text, theme.Surface0, innerCols);
    }

    private static void StampTabRow(
        IHostCellSink sink,
        SettingsOverlayModel model,
        SettingsLayout layout,
        ThemePalette theme)
    {
        var panel = layout.Panel;
        var innerCol = panel.Col + 1;
        var innerCols = Math.Max(1, panel.Cols - 2);
        var tabRow = panel.Row + 2;
        sink.Write(innerCol, tabRow, new string(' ', innerCols), theme.Text, theme.Surface0, innerCols);
        sink.Write(innerCol, tabRow, " ", theme.Text, theme.Surface0, 1);
        var first = true;
        foreach (var tab in layout.Tabs)
        {
            if (!first)
                sink.Write(tab.Rect.Col - 2, tab.Rect.Row, "  ", theme.Overlay0, theme.Surface0, 2);
            first = false;
            var page = Page(model, tab.Id);
            var label = SafeDisplayText.Clip(page is null ? tab.Id : model.TabLabel(page), tab.Rect.Cols);
            var active = string.Equals(tab.Id, model.ActivePageId, StringComparison.Ordinal);
            var fg = active ? theme.ResolveAccentFg() : theme.Subtext0;
            var bg = active ? theme.Accent : theme.Surface0;
            if (!active && label.StartsWith("● ", StringComparison.Ordinal))
            {
                sink.Write(tab.Rect.Col, tab.Rect.Row, "●", theme.Accent, bg, 1, bold: true);
                var rest = SafeDisplayText.PadRight(label[1..], Math.Max(0, tab.Rect.Cols - 1));
                sink.Write(tab.Rect.Col + 1, tab.Rect.Row, rest, fg, bg, Math.Max(0, tab.Rect.Cols - 1));
            }
            else
            {
                sink.Write(tab.Rect.Col, tab.Rect.Row, SafeDisplayText.PadRight(label, tab.Rect.Cols), fg, bg, tab.Rect.Cols);
            }
        }
    }

    private static void StampFooterRow(
        IHostCellSink sink,
        SettingsOverlayModel model,
        SettingsLayout layout,
        ThemePalette theme)
    {
        var panel = layout.Panel;
        var innerCol = panel.Col + 1;
        var innerCols = Math.Max(1, panel.Cols - 2);
        var footerRow = panel.EndRow - 2;
        sink.Write(innerCol, footerRow, new string(' ', innerCols), theme.Overlay1, theme.Surface0, innerCols);
        sink.Write(innerCol, footerRow, " ", theme.Overlay1, theme.Surface0, 1);
        var hint = model.ActivePage.Kind is SettingsPageKind.Integrations
            or SettingsPageKind.ReleaseNotes
            ? model.IntegrationsHint
            : FooterHint;
        var hintBudget = layout.Apply.Cols > 0
            ? Math.Max(0, layout.Apply.Col - panel.Col - 1)
            : Math.Max(0, layout.Close.Col - panel.Col - 1);
        hint = SafeDisplayText.Clip(hint, hintBudget);
        if (SafeDisplayText.Width(hint) > 0)
            sink.Write(panel.Col + 1, footerRow, hint, theme.Overlay1, theme.Surface0);
        if (layout.Apply.Cols > 0)
            sink.Write(layout.Apply.Col, footerRow, FooterApplyLabel, theme.Accent, theme.Surface0, layout.Apply.Cols);
        if (layout.Close.Cols > 0)
        {
            var close = SafeDisplayText.Clip(FooterCloseLabel, layout.Close.Cols);
            sink.Write(
                layout.Close.Col,
                footerRow,
                SafeDisplayText.PadRight(close, layout.Close.Cols),
                theme.Overlay1,
                theme.Surface0,
                layout.Close.Cols);
        }
    }

    private static void PaintPanelRow(
        StringBuilder sb,
        SettingsOverlayModel model,
        SettingsLayout layout,
        int row,
        ThemePalette theme)
    {
        var panel = layout.Panel;
        var rel = row - panel.Row;
        string text;
        ThemeColor fg = theme.Text;
        ThemeColor bg = theme.Surface0;
        if (rel == 0)
        {
            text = " " + model.PageTitle;
            fg = theme.Accent;
        }
        else if (rel == 1)
        {
            PaintTabRow(sb, model, layout, theme, padRight: panel.Cols);
            return;
        }
        else if (rel == panel.Rows - 1)
        {
            PaintFooterRow(sb, model, layout, theme, padRight: panel.Cols);
            return;
        }
        else if (model.ActivePage.Kind is SettingsPageKind.Integrations)
        {
            var raw = rel - 3;
            var innerCols = Math.Max(1, panel.Cols - 2);
            if (TryPinnedIntegrationLine(model, innerCols, panel.Rows - 5, raw, out var pinned))
            {
                text = pinned;
                fg = theme.Overlay1;
            }
            else
            {
                PaintIntegrationLine(sb, model, theme, panel.Cols, raw);
                return;
            }
        }
        else
        {
            text = ContentLine(model, layout.ListOffset + rel - 3);
        }

        var line = SafeDisplayText.PadRight(text, panel.Cols);
        ThemeSgr.WriteStyled(sb, fg, bg, line);
    }

    private static void PaintTabRow(
        StringBuilder sb,
        SettingsOverlayModel model,
        SettingsLayout layout,
        ThemePalette theme,
        int padRight)
    {
        var panel = layout.Panel;
        var written = 0;
        ThemeSgr.WriteStyled(sb, theme.Text, theme.Surface0, " ");
        written++;
        var first = true;
        foreach (var tab in layout.Tabs)
        {
            var page = Page(model, tab.Id);
            var label = SafeDisplayText.Clip(page is null ? tab.Id : model.TabLabel(page), tab.Rect.Cols);
            if (!first)
            {
                ThemeSgr.WriteStyled(sb, theme.Overlay0, theme.Surface0, "  ");
                written += 2;
            }

            first = false;
            var active = string.Equals(tab.Id, model.ActivePageId, StringComparison.Ordinal);
            var fg = active ? theme.ResolveAccentFg() : theme.Subtext0;
            var bg = active ? theme.Accent : theme.Surface0;
            if (!active && label.StartsWith("● ", StringComparison.Ordinal))
            {
                ThemeSgr.WriteStyled(sb, theme.Accent, bg, "●", bold: true);
                ThemeSgr.WriteStyled(sb, fg, bg, SafeDisplayText.PadRight(label[1..], Math.Max(0, tab.Rect.Cols - 1)));
            }
            else
            {
                ThemeSgr.WriteStyled(sb, fg, bg, SafeDisplayText.PadRight(label, tab.Rect.Cols));
            }

            written += tab.Rect.Cols;
        }

        if (written < padRight)
            ThemeSgr.WriteStyled(sb, theme.Text, theme.Surface0, new string(' ', padRight - written));
    }

    private static void PaintFooterRow(
        StringBuilder sb,
        SettingsOverlayModel model,
        SettingsLayout layout,
        ThemePalette theme,
        int padRight)
    {
        var panel = layout.Panel;
        var written = 0;
        ThemeSgr.WriteStyled(sb, theme.Overlay1, theme.Surface0, " ");
        written++;
        var hint = model.ActivePage.Kind is SettingsPageKind.Integrations
            or SettingsPageKind.ReleaseNotes
            ? model.IntegrationsHint
            : FooterHint;
        var hintBudget = layout.Apply.Cols > 0
            ? Math.Max(0, layout.Apply.Col - panel.Col - written)
            : Math.Max(0, layout.Close.Col - panel.Col - written);
        hint = SafeDisplayText.Clip(hint, hintBudget);
        var hintWidth = SafeDisplayText.Width(hint);
        if (hintWidth > 0)
        {
            ThemeSgr.WriteStyled(sb, theme.Overlay1, theme.Surface0, hint);
            written += hintWidth;
        }

        written = PadTo(sb, theme, panel.Col, written, layout.Apply.Cols > 0 ? layout.Apply.Col : layout.Close.Col);
        if (layout.Apply.Cols > 0)
        {
            ThemeSgr.WriteStyled(sb, theme.Accent, theme.Surface0, FooterApplyLabel);
            written += layout.Apply.Cols;
            written = PadTo(sb, theme, panel.Col, written, layout.Close.Col);
        }

        if (layout.Close.Cols > 0)
        {
            var close = SafeDisplayText.Clip(FooterCloseLabel, layout.Close.Cols);
            ThemeSgr.WriteStyled(sb, theme.Overlay1, theme.Surface0, SafeDisplayText.PadRight(close, layout.Close.Cols));
            written += layout.Close.Cols;
        }

        if (written < padRight)
            ThemeSgr.WriteStyled(sb, theme.Overlay1, theme.Surface0, new string(' ', padRight - written));
    }

    private static int PadTo(
        StringBuilder sb,
        ThemePalette theme,
        int panelCol,
        int written,
        int targetCol)
    {
        var target = targetCol - panelCol;
        if (written >= target)
            return written;
        ThemeSgr.WriteStyled(sb, theme.Overlay1, theme.Surface0, new string(' ', target - written));
        return target;
    }

    private static string ContentLine(SettingsOverlayModel model, int index)
    {
        var page = model.ActivePage;
        if (page.Kind is SettingsPageKind.Integrations)
            return IntegrationContentLine(model, index);
        if (page.Kind is SettingsPageKind.ReleaseNotes)
            return ReleaseNotesContentLine(model, index);

        if (page.Kind is SettingsPageKind.Hosted)
        {
            var hostedItems = model.Items;
            if (index < 0 || index >= hostedItems.Count)
                return "";
            var hostedItem = hostedItems[index];
            var hostedMark = index == model.ItemIndex ? ">" : " ";
            return " " + hostedMark + "  " + hostedItem.Label;
        }

        var items = model.Items;
        if (index < 0 || index >= items.Count)
            return "";
        var item = items[index];
        var selected = index == model.ItemIndex;
        var current = IsCurrent(model, item);
        var mark = selected ? ">" : " ";
        var star = current ? "*" : " ";
        return " " + mark + star + " " + item.Label;
    }

    private static string ReleaseNotesContentLine(SettingsOverlayModel model, int index) =>
        index switch
        {
            0 => " " + SettingsOverlayModel.ReleaseNotesTitle,
            1 => " " + SettingsOverlayModel.ReleaseNotesDescription,
            2 => "> open pack notes",
            _ => "",
        };

    private static void StampIntegrationLine(
        IHostCellSink sink,
        SettingsOverlayModel model,
        ThemePalette theme,
        int col,
        int row,
        int cols,
        int rawIndex)
    {
        sink.Write(col, row, new string(' ', Math.Max(0, cols)), theme.Text, theme.Surface0, cols);
        var index = IntegrationContentIndex(model, rawIndex);
        if (index < 0)
            return;
        if (index == 0)
        {
            sink.Write(col, row, ClipLine(" " + SettingsOverlayModel.IntegrationsTitle, cols), theme.Text, theme.Surface0, cols, bold: true);
            return;
        }

        if (index == 1)
        {
            sink.Write(col, row, ClipLine(" " + SettingsOverlayModel.IntegrationsDescription, cols), theme.Overlay1, theme.Surface0, cols);
            return;
        }

        if (TryIntegrationStatus(model, theme, index, out var status, out var marker, out var color, out var state))
        {
            WriteIntegrationStatus(sink, model, theme, col, row, cols, status, marker, color, state);
            return;
        }

        var plain = IntegrationPlainLine(model, index, cols);
        if (plain.Length > 0)
            sink.Write(col, row, ClipLine(plain, cols), theme.Overlay1, theme.Surface0, cols);
    }

    private static void PaintIntegrationLine(
        StringBuilder sb,
        SettingsOverlayModel model,
        ThemePalette theme,
        int cols,
        int rawIndex)
    {
        var index = IntegrationContentIndex(model, rawIndex);
        if (index < 0)
        {
            ThemeSgr.WriteStyled(sb, theme.Text, theme.Surface0, new string(' ', Math.Max(0, cols)));
            return;
        }

        if (index == 0)
        {
            ThemeSgr.WriteStyled(sb, theme.Text, theme.Surface0, ClipLine(" " + SettingsOverlayModel.IntegrationsTitle, cols), bold: true);
            return;
        }

        if (index == 1)
        {
            ThemeSgr.WriteStyled(sb, theme.Overlay1, theme.Surface0, ClipLine(" " + SettingsOverlayModel.IntegrationsDescription, cols));
            return;
        }

        if (TryIntegrationStatus(model, theme, index, out var status, out var marker, out var color, out var state))
        {
            PaintIntegrationStatus(sb, model, theme, cols, status, marker, color, state);
            return;
        }

        ThemeSgr.WriteStyled(sb, theme.Overlay1, theme.Surface0, ClipLine(IntegrationPlainLine(model, index, cols), cols));
    }

    private static void WriteIntegrationStatus(
        IHostCellSink sink,
        SettingsOverlayModel model,
        ThemePalette theme,
        int col,
        int row,
        int cols,
        OfficialIntegrationStatus status,
        string marker,
        ThemeColor color,
        string state)
    {
        var cursor = col;
        FitIntegrationRow(cols, out var boxCols, out var markerCols, out var labelCols, out var gapCols, out var stateCols);
        var box = BoxFor(model, status, boxCols);
        if (boxCols > 0)
        {
            var text = SafeDisplayText.PadRight(SafeDisplayText.Clip(box, boxCols), boxCols);
            sink.Write(cursor, row, text, theme.Text, theme.Surface0, boxCols);
            cursor += boxCols;
        }

        if (markerCols > 0)
        {
            var text = SafeDisplayText.PadRight(SafeDisplayText.Clip(marker, markerCols), markerCols);
            sink.Write(cursor, row, text, color, theme.Surface0, markerCols);
            cursor += markerCols;
        }

        if (labelCols > 0)
        {
            var text = SafeDisplayText.PadRight(SafeDisplayText.Clip(status.Target.Label(), labelCols), labelCols);
            sink.Write(cursor, row, text, theme.Subtext0, theme.Surface0, labelCols);
            cursor += labelCols;
        }

        if (gapCols > 0)
        {
            sink.Write(cursor, row, new string(' ', gapCols), theme.Surface0, theme.Surface0, gapCols);
            cursor += gapCols;
        }

        if (stateCols > 0)
        {
            var text = SafeDisplayText.Clip(WithSkillState(status, state, stateCols), stateCols);
            sink.Write(cursor, row, text, theme.Overlay1, theme.Surface0, stateCols);
        }
    }

    private static void PaintIntegrationStatus(
        StringBuilder sb,
        SettingsOverlayModel model,
        ThemePalette theme,
        int cols,
        OfficialIntegrationStatus status,
        string marker,
        ThemeColor color,
        string state)
    {
        FitIntegrationRow(cols, out var boxCols, out var markerCols, out var labelCols, out var gapCols, out var stateCols);
        var box = BoxFor(model, status, boxCols);
        if (boxCols > 0)
        {
            var text = SafeDisplayText.PadRight(SafeDisplayText.Clip(box, boxCols), boxCols);
            ThemeSgr.WriteStyled(sb, theme.Text, theme.Surface0, text);
        }

        if (markerCols > 0)
        {
            var text = SafeDisplayText.PadRight(SafeDisplayText.Clip(marker, markerCols), markerCols);
            ThemeSgr.WriteStyled(sb, color, theme.Surface0, text);
        }

        if (labelCols > 0)
        {
            var text = SafeDisplayText.PadRight(SafeDisplayText.Clip(status.Target.Label(), labelCols), labelCols);
            ThemeSgr.WriteStyled(sb, theme.Subtext0, theme.Surface0, text);
        }

        if (gapCols > 0)
            ThemeSgr.WriteStyled(sb, theme.Surface0, theme.Surface0, new string(' ', gapCols));
        if (stateCols > 0)
        {
            var text = SafeDisplayText.PadRight(
                SafeDisplayText.Clip(WithSkillState(status, state, stateCols), stateCols),
                stateCols);
            ThemeSgr.WriteStyled(sb, theme.Overlay1, theme.Surface0, text);
        }
    }

    private static readonly int IntegrationLabelCols = Math.Max(
        11,
        Enum.GetValues<OfficialIntegrationTarget>().Max(target => target.Label().Length) + 1);

    private static void FitIntegrationRow(
        int cols,
        out int boxCols,
        out int markerCols,
        out int labelCols,
        out int gapCols,
        out int stateCols)
    {
        var width = Math.Max(0, cols);
        // "[x] " is 4 columns. A leading highlight glyph needs one more.
        // Narrow frames keep the target name and a 3-column box.
        boxCols = width >= 16 ? 5 : width >= 11 ? 4 : width >= 8 ? 3 : 0;
        markerCols = Math.Min(boxCols >= 5 ? 2 : 1, Math.Max(0, width - boxCols));
        labelCols = Math.Min(IntegrationLabelCols, Math.Max(0, width - boxCols - markerCols));
        gapCols = 0;
        stateCols = Math.Max(0, width - boxCols - markerCols - labelCols - gapCols);
    }

    private static string SelectionBox(bool selected, bool highlighted, int boxCols)
    {
        var mark = selected ? "x" : " ";
        if (boxCols >= 5)
            return (highlighted ? ">" : " ") + "[" + mark + "] ";
        if (boxCols == 4)
            return "[" + mark + "] ";
        if (boxCols >= 3)
            return "[" + mark + "]";
        return "";
    }

    private static int IntegrationContentIndex(SettingsOverlayModel model, int rawIndex)
    {
        // Title and description stay put. Scroll applies to integration rows
        // and to the install lines and skill lines under them.
        if (rawIndex < 3 || model.LoadingIntegrations || model.Integrations.Count == 0)
            return rawIndex;
        return rawIndex + model.ListOffset;
    }

    private static bool TryIntegrationStatus(
        SettingsOverlayModel model,
        ThemePalette theme,
        int index,
        out OfficialIntegrationStatus status,
        out string marker,
        out ThemeColor color,
        out string state)
    {
        status = null!;
        marker = "";
        color = default;
        state = "";
        if (model.LoadingIntegrations || model.Integrations.Count == 0)
            return false;
        var slot = index - 3;
        if (slot < 0 || slot >= model.Integrations.Count)
            return false;
        status = model.Integrations[slot];
        (marker, color, state) = status.State switch
        {
            OfficialIntegrationStatusKind.Current => ("✓ ", theme.Green, IntegrationStateWord(status)),
            OfficialIntegrationStatusKind.Outdated => ("↻ ", theme.Yellow, IntegrationStateWord(status)),
            OfficialIntegrationStatusKind.NotInstalled when status.Available => ("+ ", theme.Accent, IntegrationStateWord(status)),
            _ => ("– ", theme.Overlay0, IntegrationStateWord(status)),
        };
        if (status.SkillState is OfficialIntegrationSkillState.Outdated)
        {
            marker = "↻ ";
            color = theme.Yellow;
        }

        if (model.TryIntegrationResult(status.Target, out var result))
        {
            state = result.Kind switch
            {
                IntegrationRowResultKind.Installed => "✓ installed",
                IntegrationRowResultKind.Unchanged => "✓ unchanged",
                IntegrationRowResultKind.Failed => "✗ failed",
                _ => state,
            };
        }

        return true;
    }

    private static string BoxFor(SettingsOverlayModel model, OfficialIntegrationStatus status, int boxCols)
    {
        var highlighted = false;
        for (var i = 0; i < model.Integrations.Count; i++)
        {
            if (model.Integrations[i].Target == status.Target)
            {
                highlighted = i == model.ItemIndex;
                break;
            }
        }

        return SelectionBox(model.IsIntegrationSelected(status.Target), highlighted, boxCols);
    }

    private static string IntegrationStateWord(OfficialIntegrationStatus status) =>
        status.State switch
        {
            OfficialIntegrationStatusKind.Current => "installed",
            OfficialIntegrationStatusKind.Outdated => "update available",
            OfficialIntegrationStatusKind.NotInstalled when status.Available => "available",
            _ => "not found",
        };

    private static string? SkillStateLabel(OfficialIntegrationStatus status) =>
        status.SkillState switch
        {
            OfficialIntegrationSkillState.Installed => "skill installed",
            OfficialIntegrationSkillState.Outdated => "skill outdated",
            OfficialIntegrationSkillState.Missing => "skill missing",
            _ => null,
        };

    /// <summary>
    /// Puts the skill state on the row when it fits.
    /// A state that does not fit stays the integration state. The caller shows the skill in the messages.
    /// </summary>
    private static string WithSkillState(OfficialIntegrationStatus status, string integration, int stateCols)
    {
        var skill = SkillStateLabel(status);
        if (skill is null)
            return integration;
        var combined = integration + "  " + skill;
        return combined.Length <= stateCols ? combined : integration;
    }

    private static string IntegrationPlainLine(SettingsOverlayModel model, int index, int cols)
    {
        if (index < 0)
            return "";
        if (index == 0)
            return " " + SettingsOverlayModel.IntegrationsTitle;
        if (index == 1)
            return " " + SettingsOverlayModel.IntegrationsDescription;
        if (index == 2)
            return "";
        var slot = index - 3;
        if (model.LoadingIntegrations)
            return slot == 0 ? SettingsOverlayModel.IntegrationsLoading : "";
        if (model.Integrations.Count == 0)
        {
            if (slot != 0)
                return "";
            return model.IntegrationLoadError is { Length: > 0 } error
                ? " " + error
                : SettingsOverlayModel.IntegrationsEmpty;
        }

        if (slot < model.Integrations.Count)
        {
            var status = model.Integrations[slot];
            var marker = status.SkillState is OfficialIntegrationSkillState.Outdated
                ? "↻"
                : status.State switch
                {
                    OfficialIntegrationStatusKind.Current => "✓",
                    OfficialIntegrationStatusKind.Outdated => "↻",
                    OfficialIntegrationStatusKind.NotInstalled when status.Available => "+",
                    _ => "–",
                };
            FitIntegrationRow(cols, out var boxCols, out _, out _, out _, out var stateCols);
            var state = WithSkillState(status, ResultState(model, status, IntegrationStateWord(status)), stateCols);
            var box = SelectionBox(model.IsIntegrationSelected(status.Target), slot == model.ItemIndex, boxCols);
            return box + marker + " " + status.Target.Label() + "  " + state;
        }

        var after = slot - model.Integrations.Count;
        if (after <= 0)
            return "";
        var messageSlot = after - 1;
        if (model.InstallingIntegrations && messageSlot == 0)
            return SettingsOverlayModel.IntegrationsInstalling;
        var messages = IntegrationMessageLines(model, cols);
        if (messageSlot >= 0 && messageSlot < messages.Count)
            return " " + messages[messageSlot];
        if (model.IntegrationLoadError is { Length: > 0 } loadError
            && messageSlot == messages.Count)
            return " " + loadError;
        return "";
    }

    private static string ResultState(SettingsOverlayModel model, OfficialIntegrationStatus status, string state)
    {
        if (!model.TryIntegrationResult(status.Target, out var result))
            return state;
        return result.Kind switch
        {
            IntegrationRowResultKind.Installed => "✓ installed",
            IntegrationRowResultKind.Unchanged => "✓ unchanged",
            IntegrationRowResultKind.Failed => "✗ failed",
            _ => state,
        };
    }

    /// <summary>
    /// Detail stays above the footer so a long target list does not hide it.
    /// One row slot remains so the highlighted target stays on screen.
    /// </summary>
    private static int PinnedIntegrationLineCount(SettingsOverlayModel model, int cols, int visibleSlots)
    {
        if (visibleSlots <= 1 || model.LoadingIntegrations || model.Integrations.Count == 0)
            return 0;
        var extra = IntegrationExtraLines(model, cols).Count;
        return Math.Min(extra, visibleSlots - 1);
    }

    private static bool TryPinnedIntegrationLine(
        SettingsOverlayModel model,
        int cols,
        int lastRaw,
        int raw,
        out string line)
    {
        line = "";
        if (raw < 3)
            return false;
        var visibleSlots = lastRaw - 2;
        var pin = PinnedIntegrationLineCount(model, cols, visibleSlots);
        if (pin == 0)
            return false;
        var pinStart = lastRaw - pin + 1;
        if (raw < pinStart || raw > lastRaw)
            return false;
        var extra = IntegrationExtraLines(model, cols);
        var at = raw - pinStart;
        if (at < 0 || at >= extra.Count)
            return false;
        line = extra[at];
        return true;
    }

    private static IReadOnlyList<string> IntegrationExtraLines(SettingsOverlayModel model, int cols)
    {
        var width = Math.Max(1, cols);
        var lines = new List<string>();
        lines.AddRange(IntegrationConsentText.Wrap(model.HighlightedDetail(), width));
        if (model.IntegrationSummary is { Length: > 0 } summary)
            lines.AddRange(IntegrationConsentText.Wrap([summary], width));
        return lines;
    }

    /// <summary>
    /// Integration rows plus the lines under them.
    /// A skill line that fits on its row is not part of this count.
    /// </summary>
    private static int IntegrationScrollSpan(SettingsOverlayModel model, int cols)
    {
        var count = model.Integrations.Count;
        if (model.LoadingIntegrations || count == 0)
            return count;
        var messages = IntegrationMessageLines(model, cols).Count;
        var tail = messages;
        if (model.InstallingIntegrations)
            tail = Math.Max(tail, 1);
        if (model.IntegrationLoadError is { Length: > 0 }
            && !(model.InstallingIntegrations && messages == 0))
            tail++;
        if (tail == 0)
            return count;
        return count + 1 + tail;
    }

    private static IReadOnlyList<string> IntegrationMessageLines(SettingsOverlayModel model, int cols)
    {
        FitIntegrationRow(cols, out _, out _, out _, out _, out var stateCols);
        var list = new List<string>();
        foreach (var message in model.IntegrationMessages)
        {
            if (list.Count == InstallMessageLineCap)
                break;
            list.Add(message);
        }

        if (model.InstallingIntegrations)
            return list;

        foreach (var status in model.Integrations)
        {
            var skill = SkillStateLabel(status);
            if (skill is null)
                continue;
            if (WithSkillState(status, IntegrationStateWord(status), stateCols).Contains(skill, StringComparison.Ordinal))
                continue;
            list.Add(status.Target.Label() + "  " + skill);
        }

        return list;
    }

    private static string ClipLine(string text, int cols) =>
        SafeDisplayText.PadRight(SafeDisplayText.Clip(text, Math.Max(0, cols)), Math.Max(0, cols));

    private static string IntegrationContentLine(SettingsOverlayModel model, int index) =>
        IntegrationPlainLine(model, index, 74);

    private static bool IsCurrent(SettingsOverlayModel model, SettingsListItem item)
    {
        if (model.ActivePage.Kind is not SettingsPageKind.Theme)
            return false;
        return string.Equals(item.Id, model.Items.ElementAtOrDefault(model.ItemIndex)?.Id, StringComparison.Ordinal)
            && string.Equals(item.Id, item.Label, StringComparison.Ordinal);
    }

    private static SettingsPage? Page(SettingsOverlayModel model, string id)
    {
        foreach (var page in model.Pages)
        {
            if (string.Equals(page.Id, id, StringComparison.Ordinal))
                return page;
        }

        return null;
    }
}
