using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.Cli.Attach.Keys;

namespace Hypa.Cli.Attach.Chrome;

public readonly record struct TabBarTabSpec(
    string Id,
    string Label,
    bool Active,
    bool Zoomed = false,
    bool CustomLabel = false)
{
    public static implicit operator TabBarTabSpec((string Id, string Label, bool Active) tab) =>
        new(tab.Id, tab.Label, tab.Active);

    public static IReadOnlyList<TabBarTabSpec> From(
        IReadOnlyList<(string Id, string Label, bool Active)>? tabs)
    {
        if (tabs is null || tabs.Count == 0)
            return [];
        var list = new TabBarTabSpec[tabs.Count];
        for (var i = 0; i < tabs.Count; i++)
            list[i] = tabs[i];
        return list;
    }

    public static IReadOnlyList<(string Id, string Label, bool Active)> ToTuples(
        IReadOnlyList<TabBarTabSpec> tabs)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        if (tabs.Count == 0)
            return [];
        var list = new (string Id, string Label, bool Active)[tabs.Count];
        for (var i = 0; i < tabs.Count; i++)
            list[i] = (tabs[i].Id, tabs[i].Label, tabs[i].Active);
        return list;
    }
}

public sealed record TabBarTabHit(
    string TabId,
    string Label,
    bool Active,
    CellRect Rect,
    CellRect? Close,
    string Display = "",
    bool Zoomed = false,
    bool CustomLabel = false);

public sealed record TabBarModel(
    bool Visible,
    ModeBarSlot Slot,
    int Row,
    int Cols,
    int OverflowOffset,
    IReadOnlyList<TabBarTabHit> Tabs,
    CellRect? NewTab,
    CellRect? OverflowPrev,
    CellRect? OverflowNext,
    CellRect? Zoom,
    IReadOnlyList<TabBarRightItem> RightItems,
    string Text,
    int TabCount = 0,
    bool HiddenBefore = false,
    bool HiddenAfter = false,
    int MaxOverflowOffset = 0)
{
    public const int MinTabWidth = 8;
    public const int NewTabWidth = 3;
    public const int ScrollButtonWidth = 3;
    public const int MinTabStripWidth = MinTabWidth + NewTabWidth + ScrollButtonWidth * 2;

    public static TabBarModel Hidden(ModeBarSlot slot, int cols) =>
        new(false, slot, 0, cols, 0, [], null, null, null, null, [], "");

    public static TabBarModel Build(
        IReadOnlyList<(string Id, string Label, bool Active)> tabs,
        int cols,
        int row,
        ModeBarSlot slot,
        int overflowOffset,
        AttachUiConfig ui,
        TimeProvider? time = null,
        string? hostname = null,
        string? commandCache = null,
        IReadOnlyDictionary<string, string>? commandOutputs = null,
        int originCol = 0,
        bool revealFocused = false) =>
        Build(
            TabBarTabSpec.From(tabs),
            cols,
            row,
            slot,
            overflowOffset,
            ui,
            time,
            hostname,
            commandCache,
            commandOutputs,
            originCol,
            revealFocused);

    public static TabBarModel Build(
        IReadOnlyList<TabBarTabSpec> tabs,
        int cols,
        int row,
        ModeBarSlot slot,
        int overflowOffset,
        AttachUiConfig ui,
        TimeProvider? time = null,
        string? hostname = null,
        string? commandCache = null,
        IReadOnlyDictionary<string, string>? commandOutputs = null,
        int originCol = 0,
        bool revealFocused = false)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        ArgumentNullException.ThrowIfNull(ui);
        if (cols < 1)
            cols = 1;
        if (originCol < 0)
            originCol = 0;

        var rightItems = TabBarRightRenderer.Render(
            ui.TabBarRight, time, hostname, commandCache, commandOutputs);
        var rightText = TabBarRightRenderer.Join(rightItems, ui.TabBarRightSeparator);
        var rawRightWidth = SafeDisplayText.Width(rightText);
        // the strip would drop below MIN_TAB_STRIP_WIDTH.
        var reserved = 0;
        if (rawRightWidth > 0 && cols - (rawRightWidth + 1) >= MinTabStripWidth)
            reserved = rawRightWidth + 1;
        var rightWidth = reserved > 0 ? rawRightWidth : 0;
        var paintedRight = rightWidth > 0 ? rightItems : Array.Empty<TabBarRightItem>();
        var contentCols = Math.Max(0, cols - reserved);
        var mouseChrome = ui.MouseCapture;
        var newTabWidth = mouseChrome ? NewTabWidth : 0;

        var desired = new int[tabs.Count];
        var desiredTotal = newTabWidth;
        for (var i = 0; i < tabs.Count; i++)
        {
            var label = TabLabel(tabs[i].Label, i, tabs[i].Zoomed);
            desired[i] = Math.Max(MinTabWidth, SafeDisplayText.Width(label) + 4);
            desiredTotal += desired[i];
            if (i > 0)
                desiredTotal++;
        }

        var overflow = desiredTotal > contentCols
            && (!mouseChrome || contentCols >= MinTabStripWidth);
        var available = overflow && mouseChrome
            ? contentCols - NewTabWidth - ScrollButtonWidth * 2
            : contentCols - newTabWidth;
        if (available < 0)
            available = 0;

        var maxScroll = overflow ? MaxTabScroll(desired, available) : 0;
        int offset;
        if (!overflow)
            offset = 0;
        else if (revealFocused)
        {
            var focused = -1;
            for (var i = 0; i < tabs.Count; i++)
            {
                if (tabs[i].Active)
                {
                    focused = i;
                    break;
                }
            }

            offset = focused >= 0
                ? Math.Min(CenteredTabScroll(focused, desired, available), maxScroll)
                : Math.Clamp(overflowOffset, 0, maxScroll);
        }
        else
            offset = Math.Clamp(overflowOffset, 0, maxScroll);

        CellRect? prev = null;
        var tabRight = contentCols - newTabWidth;
        var x = 0;
        if (overflow && mouseChrome)
        {
            var scrollW = Math.Min(ScrollButtonWidth, contentCols);
            prev = scrollW > 0 ? new CellRect(0, row, scrollW, 1) : null;
            x = prev?.EndCol ?? 0;
            tabRight = contentCols - NewTabWidth - ScrollButtonWidth;
            if (tabRight < x)
                tabRight = x;
        }

        var visible = new List<TabBarTabHit>();
        var lastIndex = offset - 1;
        for (var i = offset; i < tabs.Count; i++)
        {
            var remaining = tabRight - x;
            if (remaining <= 0)
                break;
            var width = Math.Min(desired[i], remaining);
            if (width <= 0)
                break;
            var spec = tabs[i];
            var label = TabLabel(spec.Label, i, spec.Zoomed);
            var display = CenterLabel(label, width);
            visible.Add(new TabBarTabHit(
                spec.Id,
                spec.Label,
                spec.Active,
                new CellRect(x, row, width, 1),
                Close: null,
                display,
                spec.Zoomed,
                spec.CustomLabel));
            lastIndex = i;
            x += width;
            if (width < desired[i])
                break;
            x++;
        }

        CellRect? next = null;
        CellRect? newTab = null;
        if (overflow && mouseChrome)
        {
            var nextWidth = Math.Min(ScrollButtonWidth, Math.Max(0, contentCols - tabRight));
            if (nextWidth > 0)
                next = new CellRect(tabRight, row, nextWidth, 1);
            var plusOrigin = next?.EndCol ?? tabRight;
            var plusWidth = Math.Min(NewTabWidth, Math.Max(0, contentCols - plusOrigin));
            if (plusWidth > 0)
                newTab = new CellRect(plusOrigin, row, plusWidth, 1);
        }
        else if (mouseChrome)
        {
            var plusOrigin = Math.Min(x, contentCols);
            var plusWidth = Math.Min(NewTabWidth, Math.Max(0, contentCols - plusOrigin));
            if (plusWidth > 0)
                newTab = new CellRect(plusOrigin, row, plusWidth, 1);
        }

        var hiddenBefore = offset > 0;
        var hiddenAfter = lastIndex + 1 < tabs.Count;

        var line = new ChromeRowBuffer(cols);
        foreach (var tab in visible)
            line.Write(tab.Rect.Col, tab.Display);
        if (prev is { } p)
            line.Write(p.Col, SafeDisplayText.Clip(" < ", p.Cols));
        if (next is { } n)
            line.Write(n.Col, SafeDisplayText.Clip(" > ", n.Cols));
        if (newTab is { } plus)
            line.Write(plus.Col, SafeDisplayText.Clip(" + ", plus.Cols));
        if (hiddenBefore)
        {
            var ellipsisCol = prev is { } left ? left.EndCol : 0;
            if (ellipsisCol < contentCols)
                line.Write(ellipsisCol, "…");
        }

        if (hiddenAfter)
        {
            var ellipsisCol = next is { } right
                ? right.Col - 1
                : contentCols - 1;
            if (ellipsisCol >= 0 && ellipsisCol < contentCols)
                line.Write(ellipsisCol, "…");
        }

        if (rightWidth > 0)
            line.Write(cols - rightWidth, SafeDisplayText.Clip(rightText, rightWidth));

        CellRect? zoom = null;
        if (rightWidth > 0)
        {
            var cursor = cols - rightWidth;
            var sepLen = Math.Max(1, SafeDisplayText.Width(SafeDisplayText.Encode(ui.TabBarRightSeparator ?? " ")));
            for (var i = 0; i < paintedRight.Count; i++)
            {
                var item = paintedRight[i];
                if (item.Kind is TabBarRightKind.Zoom && item.Width > 0)
                    zoom = new CellRect(cursor, row, item.Width, 1);
                cursor += item.Width;
                if (i + 1 < paintedRight.Count)
                    cursor += sepLen;
            }
        }

        return new TabBarModel(
            true,
            slot,
            row,
            cols,
            offset,
            ShiftHits(visible, originCol),
            Shift(newTab, originCol),
            Shift(prev, originCol),
            Shift(next, originCol),
            Shift(zoom, originCol),
            paintedRight,
            line.ToText(),
            tabs.Count,
            hiddenBefore,
            hiddenAfter,
            maxScroll);
    }

    public static string TabLabel(string label, int index, bool zoomed = false)
    {
        var name = SafeDisplayText.Encode(label);
        if (string.IsNullOrWhiteSpace(name))
            name = (index + 1).ToString();
        return zoomed ? name + " Z" : name;
    }

    private static string CenterLabel(string name, int width)
    {
        if (width <= 0)
            return "";
        var nameWidth = SafeDisplayText.Width(name);
        if (nameWidth >= width)
            return SafeDisplayText.Clip(name, width);
        var padding = width - nameWidth;
        var left = padding / 2;
        return new string(' ', left) + name + new string(' ', padding - left);
    }

    public static int CenteredTabScroll(int focused, IReadOnlyList<int> widths, int available)
    {
        ArgumentNullException.ThrowIfNull(widths);
        if (focused < 0 || focused >= widths.Count || available <= 0)
            return 0;
        var best = focused;
        var bestDistance = int.MaxValue;
        for (var start = 0; start <= focused; start++)
        {
            var before = 0;
            for (var i = start; i < focused; i++)
                before += widths[i] + 1;
            if (before >= available)
                continue;
            var focusedWidth = Math.Min(widths[focused], available - before);
            var center = (before * 2) + focusedWidth;
            var distance = Math.Abs(center - available);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = start;
            }
        }

        return best;
    }

    public static int MaxTabScroll(IReadOnlyList<int> widths, int available)
    {
        if (widths.Count == 0 || available <= 0)
            return 0;
        for (var start = 0; start < widths.Count; start++)
        {
            if (LastVisibleTab(start, widths, available) == widths.Count - 1)
                return start;
        }

        return Math.Max(0, widths.Count - 1);
    }

    private static int? LastVisibleTab(int start, IReadOnlyList<int> widths, int available)
    {
        var remaining = available;
        int? last = null;
        for (var i = start; i < widths.Count; i++)
        {
            if (remaining <= 0)
                break;
            last = i;
            if (widths[i] >= remaining)
                break;
            remaining -= widths[i] + 1;
        }

        return last;
    }

    private static IReadOnlyList<TabBarTabHit> ShiftHits(List<TabBarTabHit> hits, int originCol)
    {
        if (originCol == 0)
            return hits;
        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            hits[i] = hit with { Rect = Shift(hit.Rect, originCol) };
        }

        return hits;
    }

    private static CellRect Shift(CellRect rect, int originCol) =>
        originCol == 0 ? rect : rect with { Col = rect.Col + originCol };

    private static CellRect? Shift(CellRect? rect, int originCol) =>
        rect is { } value ? Shift(value, originCol) : null;
}
