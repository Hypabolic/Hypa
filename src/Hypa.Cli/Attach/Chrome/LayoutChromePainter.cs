using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Application.Status;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Popup;

namespace Hypa.Cli.Attach.Chrome;

/// <summary>Overlays tab, status, borders, and gaps. Uses save/restore like the mode bar.</summary>
public static class LayoutChromePainter
{
    /// <summary>
    /// Tests-only ANSI overlay. Live attach must call <see cref="Stamp"/> into
    /// a host-frame cell sink and encode once with HostBlitEncoder.
    /// </summary>
    public static string Paint(
        LayoutChromeGeometry geometry,
        AttachUiConfig ui,
        string? statusText = null,
        StatusChipComposeInput? chips = null,
        ThemePalette? theme = null)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(ui);
        theme ??= ThemePalette.Catppuccin;
        var sb = new StringBuilder();
        sb.Append('\u001b').Append('7');
        Stamp(
            new AnsiHostCellSink(sb),
            geometry,
            ui,
            statusText,
            chips,
            theme,
            includePopup: false,
            includeToasts: true);
        if (geometry.MobileSwitcher is { Open: true } switcher)
        {
            sb.Clear();
            sb.Append('\u001b').Append('7');
            sb.Append(MobileSwitcherPainter.Paint(
                switcher,
                geometry.MobileHeader,
                geometry.Cols,
                geometry.Rows,
                theme));
            foreach (var toast in geometry.ToastHits)
            {
                var banner = string.IsNullOrWhiteSpace(toast.Text) ? toast.PaneId : toast.Text;
                WriteAt(new AnsiHostCellSink(sb), toast.Rect.Col, toast.Rect.Row, banner, toast.Rect.Cols, theme.Text, theme.PanelBg);
            }

            sb.Append('\u001b').Append('8');
            return sb.ToString();
        }

        if (geometry.PopupFrame is not null)
            sb.Append(PopupPainter.Paint(geometry, snapshot: null, theme, fillInner: false));

        sb.Append('\u001b').Append('8');
        return sb.ToString();
    }

    internal static void Stamp(
        IHostCellSink sink,
        LayoutChromeGeometry geometry,
        AttachUiConfig ui,
        string? statusText = null,
        StatusChipComposeInput? chips = null,
        ThemePalette? theme = null,
        bool includePopup = false,
        bool includeToasts = true)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(ui);
        theme ??= ThemePalette.Catppuccin;

        if (geometry.WindowTooSmall)
        {
            WriteAt(sink, 0, 0, "window too small", geometry.Cols, theme.Text, theme.PanelBg);
            return;
        }

        if (geometry.MobileSwitcher is { Open: true })
            return;

        // Turbo Vision desktop: fill the whole host with PanelBg before chrome.
        Fill(
            sink,
            new CellRect(0, 0, geometry.Cols, geometry.Rows),
            ' ',
            theme.Text,
            theme.PanelBg);

        if (geometry.MobileHeader is { } header)
        {
            header = HeaderForPaint(header, geometry, ui, statusText, chips);
            WriteAt(sink, header.Status.Col, header.Status.Row, header.Line1, header.Status.Cols, theme.Text, theme.PanelBg);
            if (header.Rect.Rows > 1)
                WriteAt(sink, header.Status.Col, header.Status.Row + 1, header.Line2, header.Status.Cols, theme.Subtext0, theme.PanelBg);
            Fill(sink, header.Switch, ' ', theme.Accent, theme.PanelBg);
            WriteAt(
                sink,
                header.Switch.Col,
                header.Switch.Row + (header.Switch.Rows > 1 ? 1 : 0),
                Center("switch", header.Switch.Cols),
                header.Switch.Cols,
                theme.Accent,
                theme.PanelBg);
        }

        var glyphs = theme.ResolveGlyphs(ui.Glyphs);
        if (geometry.Sidebar is { } sidebar)
        {
            Fill(sink, sidebar, ' ', theme.Text, theme.SidebarBg);
            if (geometry.SidebarEdge is { } edge)
            {
                var edgeFg = geometry.PaintMode is AttachClientMode.Navigate
                    ? theme.Accent
                    : theme.Overlay0;
                Fill(sink, edge, glyphs.SidebarEdge, edgeFg, theme.SidebarBg);
            }

            if (geometry.SidebarFrame is { } frame)
                StampSidebarPanes(sink, geometry, frame, glyphs, theme);
            else
            {
                foreach (var hit in geometry.SidebarRows)
                {
                    var bg = hit.Selected ? theme.ActiveRowBg : theme.SidebarBg;
                    var fg = hit.Selected ? theme.ResolveSelectionFg() : theme.Subtext0;
                    var label = hit.Label;
                    if (hit.Selected && glyphs.SelectionMarker.Length > 0 && label.Length > 0)
                        label = glyphs.SelectionMarker + label;
                    WriteAt(sink, hit.Rect.Col, hit.Rect.Row, label, hit.Rect.Cols, fg, bg);
                }
            }
        }

        if (geometry.TabBarVisible)
        {
            var tabRow = geometry.TabRow
                ?? new CellRect(
                    geometry.NamedSurfaces.Main.Col,
                    geometry.TabBar.Row,
                    geometry.NamedSurfaces.Main.Cols,
                    1);
            TabBarPainter.Stamp(sink, geometry.TabBar, tabRow, theme, ui);
        }

        if (!geometry.Zoomed)
        {
            if (ui.PaneGaps)
            {
                foreach (var gap in geometry.Gaps)
                    Fill(sink, gap, ' ', theme.Text, theme.SurfaceDim);
            }
        }

        if (ui.PaneBorders)
        {
            PaneLineCells.Stamp(
                sink,
                geometry.Panes,
                geometry.SplitBorders,
                ui.PaneGaps,
                glyphs,
                theme,
                geometry.Cols,
                geometry.Rows);
        }

        foreach (var pane in geometry.Panes)
            PaneChromeGutter.Stamp(sink, pane, theme);

        // Paint after pane borders/shadows so the strip stays solid gray edge-to-edge.
        if (ShouldPaintStatusRow(geometry, statusText, chips, theme) && geometry.StatusRow is { } status)
        {
            var input = StatusInput(geometry, ui, status.Cols, statusText, chips);
            WriteStatusRow(sink, status.Col, status.Row, status.Cols, input, theme);
        }

        if (includeToasts)
            StampToasts(sink, geometry, theme);

        if (includePopup && geometry.PopupFrame is { } popup)
            StampPopupChrome(sink, popup, theme);
    }

    /// <summary>
    /// above them, then popup. Toasts sit in the content rect, so they must
    /// stamp after pane cells.
    /// </summary>
    internal static void StampToasts(
        IHostCellSink sink,
        LayoutChromeGeometry geometry,
        ThemePalette theme)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(theme);
        foreach (var toast in geometry.ToastHits)
        {
            var label = string.IsNullOrWhiteSpace(toast.Text) ? toast.PaneId : toast.Text;
            WriteAt(sink, toast.Rect.Col, toast.Rect.Row, label, toast.Rect.Cols, theme.Text, theme.PanelBg);
        }
    }

    internal static void StampPopupChrome(IHostCellSink sink, PopupChromeFrame frame, ThemePalette theme)
    {
        var outer = frame.Outer;
        if (outer.Cols < 1 || outer.Rows < 1)
            return;
        var popupGlyphs = theme.ResolveGlyphs(ChromeGlyphSet.Unicode);
        var popupBorder = theme.ResolveFocusBorder();
        var popupBg = theme.Chrome.MenuBarBg ?? theme.Surface0;
        ChromeShadow.Stamp(sink, outer, theme, hostCols: outer.EndCol + ChromeShadow.Dx + 2, hostRows: outer.EndRow + ChromeShadow.Dy + 2);
        Fill(sink, outer, ' ', theme.ResolveMenuBarFg(), popupBg);
        Fill(sink, new CellRect(outer.Col, outer.Row, outer.Cols, 1), popupGlyphs.H, popupBorder, popupBg);
        Fill(sink, new CellRect(outer.Col, outer.EndRow - 1, outer.Cols, 1), popupGlyphs.H, popupBorder, popupBg);
        Fill(sink, new CellRect(outer.Col, outer.Row, 1, outer.Rows), popupGlyphs.V, popupBorder, popupBg);
        Fill(sink, new CellRect(outer.EndCol - 1, outer.Row, 1, outer.Rows), popupGlyphs.V, popupBorder, popupBg);
        // corners
        sink.Write(outer.Col, outer.Row, char.ToString(popupGlyphs.Tl), popupBorder, popupBg, 1);
        sink.Write(outer.EndCol - 1, outer.Row, char.ToString(popupGlyphs.Tr), popupBorder, popupBg, 1);
        sink.Write(outer.Col, outer.EndRow - 1, char.ToString(popupGlyphs.Bl), popupBorder, popupBg, 1);
        sink.Write(outer.EndCol - 1, outer.EndRow - 1, char.ToString(popupGlyphs.Br), popupBorder, popupBg, 1);
        var titleWidth = Math.Max(0, outer.Cols - 2);
        var title = SafeDisplayText.Clip(" " + PopupPainter.Title + " ", titleWidth);
        if (titleWidth > 0)
            WriteAt(sink, outer.Col + 1, outer.Row, title, titleWidth, theme.ResolveAccentFg(), theme.Accent);
    }

    internal static string StatusRowText(
        LayoutChromeGeometry geometry,
        AttachUiConfig ui,
        int cols,
        string? statusText,
        StatusChipComposeInput? chips) =>
        StatusChipComposer.Compose(StatusInput(geometry, ui, cols, statusText, chips)).Text;

    internal static StatusChipComposeInput StatusInput(
        LayoutChromeGeometry geometry,
        AttachUiConfig ui,
        int cols,
        string? statusText,
        StatusChipComposeInput? chips)
    {
        var input = chips ?? ComposeInputFromGeometry(geometry, ui, cols, statusText);
        if (chips is not null && !string.IsNullOrWhiteSpace(statusText) && string.IsNullOrWhiteSpace(chips.Error))
            input = chips with { Error = statusText, MaxCols = cols };
        else if (chips is not null)
            input = chips with { MaxCols = cols > 0 ? cols : chips.MaxCols };
        return input;
    }

    internal static StatusChipComposeInput ComposeInputFromGeometry(
        LayoutChromeGeometry geometry,
        AttachUiConfig ui,
        int cols,
        string? error)
    {
        string? paneLabel = null;
        string? paneId = geometry.FocusedPaneId;
        foreach (var pane in geometry.Panes)
        {
            if (pane.PaneId != paneId)
                continue;
            paneLabel = pane.Label;
            paneId = pane.PaneId;
            break;
        }

        return new StatusChipComposeInput
        {
            SessionName = geometry.SessionName,
            WorkspaceLabel = geometry.WorkspaceLabel,
            PaneLabel = paneLabel,
            PaneId = paneId,
            AgentName = OccupantName(geometry.AgentName),
            AgentState = geometry.AgentState,
            StatusIndicators = ui.StatusIndicators,
            Extras = [],
            Error = error,
            MaxCols = cols,
        };
    }

    private static MobileHeaderModel HeaderForPaint(
        MobileHeaderModel header,
        LayoutChromeGeometry geometry,
        AttachUiConfig ui,
        string? statusText,
        StatusChipComposeInput? chips)
    {
        if (geometry.StatusRow is { Rows: > 0 })
            return header;

        var cols = header.Status.Cols > 0 ? header.Status.Cols : geometry.Cols;
        var input = chips ?? ComposeInputFromGeometry(geometry, ui, cols, statusText);
        if (chips is not null && !string.IsNullOrWhiteSpace(statusText) && string.IsNullOrWhiteSpace(chips.Error))
            input = chips with { Error = statusText, MaxCols = cols };
        else if (chips is not null)
            input = chips with { MaxCols = cols };
        else
            input = input with { MaxCols = cols };
        return header.WithChips(input);
    }

    private static bool ShouldPaintStatusRow(
        LayoutChromeGeometry geometry,
        string? statusText,
        StatusChipComposeInput? chips,
        ThemePalette theme)
    {
        if (geometry.StatusRow is not { Rows: > 0, Cols: > 0 })
            return false;
        if (geometry.IsNarrow)
            return false;
        if (theme.Chrome.DesktopStatusBar)
            return true;
        // Idle desktop must not keep a diagnostic footer. Transient errors
        // stay on the mode bar. Mobile uses the header chips.
        if (geometry.PaintMode is AttachClientMode.Terminal
            && string.IsNullOrWhiteSpace(statusText)
            && (chips is null || string.IsNullOrWhiteSpace(chips.Error)))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(statusText)
            || (chips is not null && !string.IsNullOrWhiteSpace(chips.Error));
    }

    private static string? OccupantName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    private static void WriteStatusRow(
        IHostCellSink sink,
        int originCol,
        int row,
        int cols,
        StatusChipComposeInput input,
        ThemePalette theme)
    {
        if (cols < 1)
            return;

        var rowBg = theme.Chrome.DesktopStatusBar ? theme.ResolveMenuBarBg() : theme.PanelBg;
        var rowFg = theme.Chrome.DesktopStatusBar ? theme.ResolveMenuBarFg() : theme.Text;
        // Always paint the full strip first so unused cells stay gray, not host/black.
        sink.Write(originCol, row, new string(' ', cols), rowFg, rowBg, cols);

        var line = StatusChipComposer.Compose(input);
        var written = 0;
        for (var i = 0; i < line.Chips.Count; i++)
        {
            var chip = line.Chips[i];
            if (chip.Text.Length == 0)
                continue;
            if (written > 0)
            {
                sink.Write(originCol + written, row, " ", rowFg, rowBg, 1);
                written++;
            }

            var fg = theme.Chrome.DesktopStatusBar
                ? rowFg
                : ChipForeground(chip, input.AgentState, theme);
            var chipCols = chip.DisplayWidth > 0 ? chip.DisplayWidth : SafeDisplayText.Width(chip.Text);
            if (written + chipCols > cols)
                chipCols = Math.Max(0, cols - written);
            if (chipCols <= 0)
                break;
            sink.Write(originCol + written, row, chip.Text, fg, rowBg, chipCols);
            written += chipCols;
        }

        if (written == 0 && theme.Chrome.DesktopStatusBar)
        {
            var help = SafeDisplayText.PadRight(
                SafeDisplayText.Clip(" F1 Help  Alt-X Exit  F10 Menu", cols),
                cols);
            sink.Write(originCol, row, help, rowFg, rowBg, cols);
        }
    }

    private static ThemeColor ChipForeground(StatusChip chip, string? agentState, ThemePalette theme)
    {
        if (chip.Kind is StatusChipKind.Error)
            return theme.Red;
        if (chip.Kind is not StatusChipKind.Agent)
            return theme.Text;

        return SidebarTokenGrammar.CanonicalState(agentState) switch
        {
            SidebarTokenGrammar.Working => theme.Yellow,
            SidebarTokenGrammar.Blocked => theme.Red,
            SidebarTokenGrammar.Done => theme.Teal,
            SidebarTokenGrammar.Idle => theme.Green,
            _ => theme.Overlay0,
        };
    }

    private static void StampSidebarPanes(
        IHostCellSink sink,
        LayoutChromeGeometry geometry,
        SidebarFrame frame,
        ChromeGlyphSet glyphs,
        ThemePalette theme)
    {
        // on the first row of every pane after the first.
        foreach (var divider in geometry.PaneDividerRects)
        {
            Fill(
                sink,
                divider,
                glyphs.H,
                theme.SurfaceDim,
                theme.SidebarBg);
        }

        var compact = frame.Display is SidebarCollapseDisplay.Compact;
        foreach (var pane in frame.Panes)
        {
            var rect = pane.Slot switch
            {
                SidebarPaneSlot.Spaces => geometry.SpacesPane,
                SidebarPaneSlot.Agents => geometry.AgentsPane,
                SidebarPaneSlot.Cubes or SidebarPaneSlot.Resource
                    => FindResourcePane(geometry, pane.Id),
                _ => null,
            };
            if (rect is not { Cols: > 0, Rows: > 0 } paneRect)
                continue;

            if (!compact)
            {
                var headerRow = SidebarTwoPaneLayoutPolicy.HeaderRow(
                    paneRect,
                    pane.Slot,
                    compact,
                    !compact && geometry.HasLeadingDivider(paneRect.Row));
                if (headerRow >= paneRect.Row && headerRow < paneRect.EndRow)
                {
                    WriteAt(
                        sink,
                        paneRect.Col,
                        headerRow,
                        pane.Header,
                        paneRect.Cols,
                        theme.Overlay0,
                        theme.SidebarBg,
                        bold: true);
                }

                var footerActions = pane.Slot is SidebarPaneSlot.Spaces or SidebarPaneSlot.Cubes;
                var actionRect = footerActions
                    ? new CellRect(
                        paneRect.Col,
                        paneRect.EndRow - SidebarTwoPaneLayoutPolicy.SpacesFooterRows,
                        paneRect.Cols,
                        SidebarTwoPaneLayoutPolicy.SpacesFooterRows)
                    : paneRect;
                var actionRow = footerActions
                    ? actionRect.Row
                    : headerRow;
                if (actionRow >= paneRect.Row && actionRow < paneRect.EndRow)
                {
                    foreach (var action in pane.Actions)
                        StampSidebarAction(sink, action, actionRect, actionRow, theme);
                }
            }

            var body = pane.Slot switch
            {
                SidebarPaneSlot.Spaces => geometry.SpacesBody,
                SidebarPaneSlot.Agents => geometry.AgentsBody,
                SidebarPaneSlot.Cubes or SidebarPaneSlot.Resource
                    => FindResourceBody(geometry, pane.Id),
                _ => geometry.ResourceBody,
            };
            if (body is not { Cols: > 0, Rows: > 0 } bodyRect)
                continue;
            var visible = bodyRect.Rows;
            if (pane.Collapsed || visible <= 0)
                continue;

            if (pane.Rows.Count == 0)
            {
                if (!compact && pane.EmptyText.Length > 0)
                {
                    WriteAt(
                        sink,
                        bodyRect.Col,
                        bodyRect.Row,
                        pane.EmptyText,
                        bodyRect.Cols,
                        theme.Overlay0,
                        theme.SidebarBg,
                        dim: true);
                }

                continue;
            }

            var scroll = geometry.ScrollFor(pane.Slot, pane.Id);
            var visibleRows = SidebarTwoPaneLayoutPolicy.VisibleCardRows(pane.Rows, scroll, visible);
            for (var i = 0; i < visibleRows.Count; i++)
            {
                var row = visibleRows[i];
                var y = bodyRect.Row + i;
                // fill the whole card rect. Selected/Navigated stay on
                // the name row so markers are not repeated.
                var bg = row.CardNavigated
                    ? NavigatedCardBackground(theme)
                    : row.CardFocused
                        ? FocusedCardBackground(row, theme)
                        : theme.SidebarBg;
                var fg = row.Navigated || row.Selected ? theme.ResolveSelectionFg() : theme.Subtext0;
                if (compact)
                {
                    var label = row.CompactLabel;
                    var marker = SelectionMarker(row, glyphs);
                    if ((row.Navigated || row.Selected) && marker.Length > 0 && label.Length > 0)
                        label = marker + label;
                    WriteAt(sink, bodyRect.Col, y, label, bodyRect.Cols, fg, bg);
                    continue;
                }

                StampStructuredRow(sink, glyphs, theme, bodyRect, y, row, fg, bg);
            }
        }
    }

    /// <summary>
    /// overlay0 and Menu right-aligned. Attention uses accent on the badge.
    /// </summary>
    private static void StampSidebarAction(
        IHostCellSink sink,
        SidebarActionHit action,
        CellRect pane,
        int row,
        ThemePalette theme)
    {
        var width = Math.Min(action.Width, Math.Max(1, pane.Cols));
        var col = action.Align is SidebarActionAlign.Right
            ? pane.Col + Math.Max(0, pane.Cols - width)
            : pane.Col;
        if (action.AttentionBadgeVisible
            && action.Align is SidebarActionAlign.Right
            && width >= SidebarTwoPaneLayoutPolicy.MenuAttentionBadgeWidth)
        {
            var start = pane.EndCol - 6;
            WriteAt(
                sink,
                start,
                row,
                SidebarTwoPaneLayoutPolicy.MenuAttentionBadgePrefix,
                2,
                theme.Accent,
                theme.SidebarBg,
                bold: true);
            WriteAt(sink, start + 2, row, SidebarTwoPaneLayoutPolicy.MenuLabel, 4, theme.Overlay0, theme.SidebarBg);
            return;
        }

        var label = action.Align is SidebarActionAlign.Right
            ? PadLeft(action.Label, width)
            : action.Label;
        WriteAt(sink, col, row, label, width, theme.Overlay0, theme.SidebarBg);
    }

    private static void StampStructuredRow(
        IHostCellSink sink,
        ChromeGlyphSet glyphs,
        ThemePalette theme,
        CellRect pane,
        int y,
        SidebarPaintedRow row,
        ThemeColor fg,
        ThemeColor bg)
    {
        Fill(sink, new CellRect(pane.Col, y, pane.Cols, 1), ' ', fg, bg);
        var x = pane.Col;
        var end = pane.EndCol;
        if (row.CardRowIndex == 0 && !string.IsNullOrEmpty(row.TrailingStatus))
        {
            var statusWidth = SafeDisplayText.Width(row.TrailingStatus);
            if (statusWidth > 0 && statusWidth < pane.Cols)
                end = Math.Max(x + 1, end - statusWidth);
        }

        var marker = SelectionMarker(row, glyphs);
        if (row.CardRowIndex == 0 && row.Selected && marker.Length > 0)
        {
            var markerWidth = SafeDisplayText.Width(marker);
            WriteAt(sink, x, y, marker, markerWidth, fg, bg);
            x += markerWidth;
        }

        var prefix = PrefixGlyph(glyphs, row.PrefixRole);
        if (prefix.Length > 0)
        {
            var prefixX = pane.Col + Math.Max(0, row.IndentCols);
            var prefixWidth = SafeDisplayText.Width(prefix);
            if (prefixX < end)
                WriteAt(sink, prefixX, y, prefix, Math.Min(prefixWidth, end - prefixX), theme.Overlay0, bg);
            x = Math.Max(x, prefixX + prefixWidth);
        }
        else if (row.IndentCols > 0)
            x = Math.Max(x, pane.Col + row.IndentCols);

        if (row.CardRowIndex > 0 && row.NameColumn > 0)
            x = Math.Max(x, pane.Col + row.NameColumn);

        SidebarRowToken? previous = null;
        foreach (var token in row.Tokens)
        {
            if (x >= end)
                break;
            var width = SafeDisplayText.Width(token.Text);
            if (width <= 0)
                continue;
            if (previous is not null)
            {
                var sep = SidebarTokenGrammar.Separator(previous.Id, token.Id);
                var sepWidth = SafeDisplayText.Width(sep);
                if (sepWidth > 0)
                {
                    var (sepFg, _, sepDim) = MetaToken(theme, row);
                    WriteAt(sink, x, y, sep, Math.Min(sepWidth, end - x), sepFg, bg, dim: sepDim);
                    x += sepWidth;
                    if (x >= end)
                        break;
                }
            }

            x = StampTokenSpan(sink, theme, x, y, end, token, row, fg, bg);
            previous = token;
        }

        if (row.CardRowIndex == 0 && !string.IsNullOrEmpty(row.TrailingStatus))
        {
            var statusWidth = SafeDisplayText.Width(row.TrailingStatus);
            var statusCol = pane.EndCol - statusWidth;
            if (statusCol >= pane.Col)
            {
                WriteAt(
                    sink,
                    statusCol,
                    y,
                    row.TrailingStatus,
                    statusWidth,
                    TrailingStatusColor(row, theme),
                    bg);
            }
        }

        if (!WorktreeWorkspaceGrouping.IsParentToggleRow(row) || pane.Cols < 1)
            return;
        var glyph = row.GroupCollapsed ? glyphs.ToggleExpand : glyphs.ToggleCollapse;
        WriteAt(sink, pane.EndCol - 1, y, glyph.ToString(), 1, theme.Accent, bg);
    }

    /// <summary>
    /// <c>src/ui/sidebar.rs:279-298</c> <c>apply_token_style</c> overlays
    /// optional config style.
    /// </summary>
    private static int StampTokenSpan(
        IHostCellSink sink,
        ThemePalette theme,
        int x,
        int y,
        int end,
        SidebarRowToken token,
        SidebarPaintedRow row,
        ThemeColor fallbackFg,
        ThemeColor bg)
    {
        var budget = end - x;
        if (budget <= 0)
            return x;
        if (token.Id == "git_status")
            return StampGitStatus(sink, theme, x, y, budget, token.Text, bg, token.Style);

        var (fg, bold, dim) = TokenStyle(token.Id, row, theme, fallbackFg);
        (fg, bold, dim) = ApplyTokenStyle(fg, bold, dim, token.Style);
        var width = Math.Min(SafeDisplayText.Width(token.Text), budget);
        WriteAt(sink, x, y, token.Text, width, fg, bg, bold: bold, dim: dim);
        return x + width;
    }

    private static (ThemeColor Fg, bool Bold, bool Dim) TokenStyle(
        string id,
        SidebarPaintedRow row,
        ThemePalette theme,
        ThemeColor fallbackFg)
    {
        return id switch
        {
            "state_icon" => (SidebarStateForeground(row.State, theme), false, false),
            "state_text" => (SidebarStateForeground(row.State, theme), false, !row.Selected && !row.Navigated),
            "workspace" or "name" => row.Navigated || row.Selected
                ? (theme.Text, true, false)
                : (theme.Subtext0, false, false),
            "agent" or "tab" or "pane" or "branch" or "kind" or "machine"
                or "reachability" or "work_title" => MetaToken(theme, row),
            "terminal_title" or "terminal_title_stripped" => (theme.Overlay1, false, false),
            _ => (fallbackFg, false, false),
        };
    }

    private static (ThemeColor Fg, bool Bold, bool Dim) ApplyTokenStyle(
        ThemeColor fg,
        bool bold,
        bool dim,
        SidebarTokenStyle? patch)
    {
        if (patch is null || patch.IsEmpty)
            return (fg, bold, dim);
        if (patch.Fg is { } color)
            fg = color;
        if (patch.Bold is { } boldPatch)
            bold = boldPatch;
        if (patch.Dim is { } dimPatch)
            dim = dimPatch;
        return (fg, bold, dim);
    }

    private static (ThemeColor Fg, bool Bold, bool Dim) MetaToken(ThemePalette theme, SidebarPaintedRow row)
    {
        if (theme.Chrome.MetaFg is not { } meta)
            return (theme.Overlay0, false, true);
        var fg = row.Selected || row.Navigated || row.CardFocused || row.CardNavigated
            ? theme.Chrome.MetaFgOnSelection ?? meta
            : meta;
        return (fg, false, false);
    }

    private static ThemeColor SidebarStateForeground(string? state, ThemePalette theme) =>
        SidebarTokenGrammar.CanonicalState(state) switch
        {
            SidebarTokenGrammar.Working => theme.Yellow,
            SidebarTokenGrammar.Blocked => theme.Red,
            SidebarTokenGrammar.Done => theme.Teal,
            SidebarTokenGrammar.Idle => theme.Green,
            _ => theme.Overlay0,
        };

    private static int StampGitStatus(
        IHostCellSink sink,
        ThemePalette theme,
        int x,
        int y,
        int budget,
        string text,
        ThemeColor bg,
        SidebarTokenStyle? patch)
    {
        var remaining = budget;
        foreach (var part in SplitGitStatus(text))
        {
            if (remaining <= 0)
                break;
            var width = Math.Min(SafeDisplayText.Width(part.Text), remaining);
            var fg = part.Kind switch
            {
                GitStatusPartKind.Ahead => theme.Green,
                GitStatusPartKind.Behind => theme.Red,
                _ => theme.Overlay0,
            };
            var (styledFg, bold, dim) = ApplyTokenStyle(fg, false, false, patch);
            WriteAt(sink, x, y, part.Text, width, styledFg, bg, bold: bold, dim: dim);
            x += width;
            remaining -= width;
        }

        return x;
    }

    private enum GitStatusPartKind
    {
        Neutral,
        Ahead,
        Behind,
    }

    private static IEnumerable<(string Text, GitStatusPartKind Kind)> SplitGitStatus(string text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('↑' or '↓'))
                continue;
            if (i > start)
                yield return (text[start..i], GitStatusPartKind.Neutral);
            var end = i + 1;
            while (end < text.Length && char.IsDigit(text[end]))
                end++;
            var kind = text[i] == '↑' ? GitStatusPartKind.Ahead : GitStatusPartKind.Behind;
            yield return (text[i..end], kind);
            start = end;
            i = end - 1;
        }

        if (start < text.Length)
            yield return (text[start..], GitStatusPartKind.Neutral);
    }

    private static string PrefixGlyph(ChromeGlyphSet glyphs, SidebarPrefixRole role) =>
        role switch
        {
            SidebarPrefixRole.TreeBranch => glyphs.TreeBranch,
            SidebarPrefixRole.TreeLast => glyphs.TreeLast,
            SidebarPrefixRole.TreeBar => glyphs.TreeBar,
            _ => "",
        };

    private static CellRect? FindResourcePane(LayoutChromeGeometry geometry, string sectionId)
    {
        foreach (var section in geometry.ResourceSectionBodies)
        {
            if (string.Equals(section.SectionId, sectionId, StringComparison.Ordinal))
                return section.Pane;
        }

        return geometry.ResourcePane;
    }

    private static CellRect? FindResourceBody(LayoutChromeGeometry geometry, string sectionId)
    {
        foreach (var section in geometry.ResourceSectionBodies)
        {
            if (string.Equals(section.SectionId, sectionId, StringComparison.Ordinal))
                return section.Body;
        }

        return geometry.ResourceBody;
    }

    private static void WriteAt(
        IHostCellSink sink,
        int col,
        int row,
        string text,
        int cols,
        ThemeColor? fg = null,
        ThemeColor? bg = null,
        bool bold = false,
        bool dim = false) =>
        sink.Write(col, row, SafeDisplayText.PadRight(text, cols), fg, bg, cols, bold: bold, dim: dim);

    private static string PadLeft(string text, int cols)
    {
        var clipped = SafeDisplayText.Clip(text, Math.Max(1, cols));
        var width = SafeDisplayText.Width(clipped);
        if (width >= cols)
            return SafeDisplayText.PadRight(clipped, cols);
        return new string(' ', cols - width) + clipped;
    }

    private static string Center(string text, int cols)
    {
        var clipped = SafeDisplayText.Clip(text, Math.Max(1, cols));
        var width = SafeDisplayText.Width(clipped);
        if (width >= cols)
            return SafeDisplayText.PadRight(clipped, cols);
        var pad = (cols - width) / 2;
        return new string(' ', pad) + SafeDisplayText.PadRight(clipped, cols - pad);
    }

    private static void Fill(
        IHostCellSink sink,
        CellRect rect,
        char ch,
        ThemeColor? fg = null,
        ThemeColor? bg = null)
    {
        var fill = new string(ch, Math.Max(0, rect.Cols));
        for (var r = rect.Row; r < rect.EndRow; r++)
            sink.Write(rect.Col, r, fill, fg, bg, rect.Cols);
    }

    /// <summary>
    /// prefixes the endpoint label. Unicode <c>SelectionMarker</c> is
    /// empty, so selected cubes fall back to the ASCII marker.
    /// </summary>
    internal static string SelectionMarker(SidebarPaintedRow row, ChromeGlyphSet glyphs)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(glyphs);
        if (glyphs.SelectionMarker.Length > 0)
            return glyphs.SelectionMarker;
        return row.Kind is SidebarRowKind.Cube ? ">" : "";
    }

    /// <summary>
    /// <c>selection_bg</c> is Reset, use <c>active_row_bg</c>.
    /// </summary>
    internal static ThemeColor NavigatedCardBackground(ThemePalette theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return theme.SelectionBg.IsReset ? theme.ActiveRowBg : theme.SelectionBg;
    }

    /// <summary>
    /// endpoint uses <c>active_row_bg</c>. When sidebar bg is Reset, that
    /// role matches the terminal, so cubes use <c>selection_bg</c>.
    /// </summary>
    internal static ThemeColor FocusedCardBackground(SidebarPaintedRow row, ThemePalette theme)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(theme);
        if (row.Kind is SidebarRowKind.Cube && theme.SidebarBg.IsReset && !theme.SelectionBg.IsReset)
            return theme.SelectionBg;
        return theme.ActiveRowBg;
    }

    /// <summary>
    /// <c>endpoint_status_presentation</c>.
    /// </summary>
    internal static ThemeColor TrailingStatusColor(SidebarPaintedRow row, ThemePalette theme)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(theme);
        return row.TrailingStatusKind switch
        {
            CubeTrailingStatusKind.Online => theme.Green,
            CubeTrailingStatusKind.Connecting => theme.Yellow,
            CubeTrailingStatusKind.Disabled => theme.Overlay0,
            _ => theme.Overlay0,
        };
    }

}
