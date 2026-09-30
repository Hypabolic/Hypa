using Hypa.AgentRuntime.Application;

namespace Hypa.AgentRuntime.Application.Sidebar;

/// <summary>
// / <c>src/client/shell/sidebar.rs:7-24</c> compact split.
/// <c>src/client/shell/sidebar.rs:198-207</c> paints workspaces first,
/// then agents. Hypa stacks resource panes above that split.
/// <see cref="ResolvedSidebarSection.Order"/> does not swap spaces and
/// agents.
/// </summary>
public sealed record SidebarResourcePaneLayout(
    string SectionId,
    CellRect Pane,
    CellRect Body);

/// <summary>
// / One stacked sidebar band.
/// <c>src/client/shell/agent_sidebar.rs:90-107</c> paints a surface-dim
/// H line on the first row of every pane after the first, then the
/// heading on the next row. Hypa uses that for every neighbour pair.
/// </summary>
public sealed record SidebarStackedPane(
    string SectionId,
    SidebarPaneSlot Slot,
    CellRect Pane,
    CellRect Body,
    bool LeadingDivider);

public sealed record SidebarTwoPaneLayout(
    CellRect Spaces,
    CellRect Agents,
    CellRect Resource,
    int? DividerRow,
    int ContentCols,
    IReadOnlyList<SidebarResourcePaneLayout> ResourceSections = null!)
{
    public IReadOnlyList<SidebarResourcePaneLayout> ResourceSections { get; init; } =
        ResourceSections is { Count: > 0 } sections ? sections : [];

    public IReadOnlyList<SidebarStackedPane> Stack { get; init; } = [];

    public bool HasLeadingDivider(SidebarPaneSlot slot, string? sectionId = null)
    {
        foreach (var pane in Stack)
        {
            if (pane.Slot != slot)
                continue;
            if (!string.IsNullOrWhiteSpace(sectionId)
                && !string.Equals(pane.SectionId, sectionId, StringComparison.Ordinal))
            {
                continue;
            }

            return pane.LeadingDivider;
        }

        return false;
    }
}

public static class SidebarTwoPaneLayoutPolicy
{
    public const float DefaultSplitRatio = 0.5f;

    public const float MinSplitRatio = 0.1f;

    public const float MaxSplitRatio = 0.9f;

    public const int MinPaneRows = 3;

    public const int ExpandedSplitMinHeight = 6;

    public const int CompactTwoPaneMinHeight = 7;

    /// <summary>
    /// New/Menu moved to the footer. Row 0 is the heading. Row 1 is a spacer.
    /// </summary>
    public const int WorkspaceHeaderRows = 2;

    public const int AgentHeaderRows = 3;

    /// <summary>
    /// last spaces-pane row. Compact hides those actions.
    /// </summary>
    public const int SpacesFooterRows = 1;

    public const int NewButtonWidth = 5;

    public const int MenuButtonWidth = 6;

    /// <summary>
    /// attention badge is visible.
    /// </summary>
    public const int MenuAttentionBadgeWidth = 8;

    public const string MenuLabel = "menu";

    public const string MenuAttentionBadgePrefix = "● ";

    public const int EdgeCols = 1;

    /// <summary>
    /// <c>update_available.is_some() || integration_updates_available()</c>.
    /// </summary>
    public static bool GlobalMenuAttentionBadgeVisible(
        bool updateAvailable,
        bool integrationUpdatesAvailable) =>
        updateAvailable || integrationUpdatesAvailable;

    public static int MenuHitWidth(bool attentionBadgeVisible) =>
        attentionBadgeVisible ? MenuAttentionBadgeWidth : MenuButtonWidth;

    public static string MenuActionLabel(bool attentionBadgeVisible) =>
        attentionBadgeVisible
            ? MenuAttentionBadgePrefix + MenuLabel
            : MenuLabel;

    public static (int Spaces, int Agents) SectionHeights(int totalH, float splitRatio)
    {
        if (totalH <= 0)
            return (0, 0);

        if (totalH < ExpandedSplitMinHeight)
        {
            var spaces = (totalH + 1) / 2;
            return (spaces, totalH - spaces);
        }

        var ratio = ClampSplitRatio(splitRatio);
        var spacesH = (int)Math.Round(totalH * ratio, MidpointRounding.AwayFromZero);
        spacesH = Math.Clamp(spacesH, MinPaneRows, totalH - MinPaneRows);
        return (spacesH, totalH - spacesH);
    }

    public static float ClampSplitRatio(float splitRatio) =>
        float.IsFinite(splitRatio)
            ? Math.Clamp(splitRatio, MinSplitRatio, MaxSplitRatio)
            : DefaultSplitRatio;

    /// <summary>
    /// height, not the section-divider band.
    /// </summary>
    public static float SplitRatioFromRow(CellRect sidebar, int row)
    {
        if (sidebar.Rows <= 0)
            return DefaultSplitRatio;
        var ratio = (row - sidebar.Row) / (float)sidebar.Rows;
        return ClampSplitRatio(ratio);
    }

    public const int ResourceHeaderRows = 2;

    public const int ResourceMinRows = 3;

    /// <summary>
    /// first row of a following pane on the H line.
    /// </summary>
    public const int LeadingDividerRows = 1;

    public static SidebarTwoPaneLayout Expanded(
        CellRect sidebar,
        float splitRatio = DefaultSplitRatio,
        IReadOnlyList<string>? resourceSectionIds = null,
        IReadOnlyDictionary<string, int>? resourceWantedRows = null) =>
        Build(sidebar, splitRatio, compact: false, resourceSectionIds, resourceWantedRows);

    private static SidebarTwoPaneLayout Build(
        CellRect sidebar,
        float splitRatio,
        bool compact,
        IReadOnlyList<string>? resourceSectionIds,
        IReadOnlyDictionary<string, int>? resourceWantedRows = null)
    {
        var content = ContentRect(sidebar);
        if (content.Cols <= 0 || content.Rows <= 0)
            return Empty(content);

        var resourceIds = resourceSectionIds?.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray() ?? [];
        var resourceRows = ResourceRows(content.Rows, resourceIds, compact, resourceWantedRows);
        var coreRows = Math.Max(0, content.Rows - resourceRows);
        var (spacesH, agentsH) = compact
            ? CompactSectionHeights(coreRows)
            : SectionHeights(coreRows, splitRatio);
        var spaces = new CellRect(content.Col, content.Row + resourceRows, content.Cols, spacesH);
        // Compact keeps one divider row between the panes. Expanded draws the
        // divider on the first agents row.
        var compactDivider = compact && agentsH > 0;
        var agents = new CellRect(
            content.Col,
            compactDivider ? spaces.EndRow + 1 : spaces.EndRow,
            content.Cols,
            agentsH);
        int? divider = compactDivider
            ? spaces.EndRow
            : !compact && coreRows >= ExpandedSplitMinHeight ? agents.Row : null;
        var resourceSections = AllocateResourceSections(
            content,
            content.Row,
            resourceRows,
            resourceIds,
            compact,
            resourceWantedRows);
        var resource = resourceSections.Count > 0
            ? resourceSections[0].Pane with { Rows = resourceRows, Row = content.Row }
            : new CellRect(content.Col, content.Row, content.Cols, 0);
        if (resourceSections.Count > 1)
        {
            var first = resourceSections[0].Pane;
            var last = resourceSections[^1].Pane;
            resource = new CellRect(first.Col, first.Row, first.Cols, last.EndRow - first.Row);
        }

        return new SidebarTwoPaneLayout(
            spaces,
            agents,
            resource,
            divider,
            content.Cols,
            resourceSections)
        {
            Stack = BuildStack(resourceSections, spaces, agents, compact),
        };
    }

    public static SidebarTwoPaneLayout Compact(
        CellRect sidebar,
        IReadOnlyList<string>? resourceSectionIds = null,
        IReadOnlyDictionary<string, int>? resourceWantedRows = null) =>
        Build(sidebar, DefaultSplitRatio, compact: true, resourceSectionIds, resourceWantedRows);

    public static CellRect RectFor(
        this SidebarTwoPaneLayout layout,
        SidebarPaneSlot slot,
        string? sectionId = null)
    {
        if (slot is SidebarPaneSlot.Cubes or SidebarPaneSlot.Resource
            && !string.IsNullOrWhiteSpace(sectionId))
        {
            foreach (var section in layout.ResourceSections)
            {
                if (string.Equals(section.SectionId, sectionId, StringComparison.Ordinal))
                    return section.Pane;
            }
        }

        return slot switch
        {
            SidebarPaneSlot.Spaces => layout.Spaces,
            SidebarPaneSlot.Agents => layout.Agents,
            SidebarPaneSlot.Cubes or SidebarPaneSlot.Resource => layout.Resource,
            _ => default,
        };
    }

    public static CellRect BodyRectForSection(
        this SidebarTwoPaneLayout layout,
        SidebarPaneSlot slot,
        bool compact,
        string sectionId)
    {
        if (slot is SidebarPaneSlot.Cubes or SidebarPaneSlot.Resource)
        {
            foreach (var section in layout.ResourceSections)
            {
                if (string.Equals(section.SectionId, sectionId, StringComparison.Ordinal))
                    return section.Body;
            }
        }

        return layout.BodyRect(slot, compact);
    }

    /// <summary>
    /// <c>src/client/shell/agent_sidebar.rs:165-170</c>. Compact uses the
    /// full pane. Expanded spaces reserve header, spacer, and footer.
    /// </summary>
    public static CellRect BodyRect(this SidebarTwoPaneLayout layout, SidebarPaneSlot slot, bool compact)
    {
        var rect = layout.RectFor(slot);
        if (rect.Rows <= 0 || rect.Cols <= 0)
            return default;
        if (compact)
            return rect;

        var header = HeaderRows(slot, layout.HasLeadingDivider(slot));
        var footer = FooterRows(slot);
        var rows = Math.Max(0, rect.Rows - header - footer);
        if (rows <= 0)
            return new CellRect(rect.Col, rect.Row + header, rect.Cols, 0);
        return new CellRect(rect.Col, rect.Row + header, rect.Cols, rows);
    }

    public static int ResourceRows(int totalRows, int resourceCount, bool compact) =>
        ResourceRows(totalRows, new string[Math.Max(0, resourceCount)], compact, wantedRows: null);

    public static int ResourceRows(
        int totalRows,
        IReadOnlyList<string> resourceIds,
        bool compact,
        IReadOnlyDictionary<string, int>? wantedRows)
    {
        var resourceCount = resourceIds.Count;
        if (resourceCount <= 0 || totalRows <= 0)
            return 0;
        var perSection = compact ? Math.Max(1, ResourceMinRows - 1) : ResourceMinRows;
        var wanted = 0;
        var sawPane = false;
        foreach (var id in resourceIds)
        {
            var rows = wantedRows is not null && wantedRows.TryGetValue(id, out var specified) && specified > 0
                ? specified
                : perSection;
            if (!compact && sawPane && rows > 0)
                rows += LeadingDividerRows;
            wanted += rows;
            if (rows > 0)
                sawPane = true;
        }

        if (wanted == 0)
            wanted = resourceCount * perSection;
        var maxCore = compact ? CompactTwoPaneMinHeight : ExpandedSplitMinHeight;
        var maxResource = Math.Max(0, totalRows - maxCore);
        return Math.Min(wanted, maxResource);
    }

    public static Dictionary<string, int> WantedResourceRows(
        IEnumerable<SidebarPaneView>? panes,
        bool compact)
    {
        var wanted = new Dictionary<string, int>(StringComparer.Ordinal);
        if (panes is null)
            return wanted;
        foreach (var pane in panes)
        {
            if (!pane.Visible)
                continue;
            if (pane.Slot is not (SidebarPaneSlot.Cubes or SidebarPaneSlot.Resource))
                continue;
            var header = compact ? 0 : ResourceHeaderRows;
            var footer = compact ? 0 : FooterRows(pane.Slot);
            var body = pane.Collapsed ? 0 : pane.Rows.Count;
            if (!pane.Collapsed && body == 0)
                body = 1;
            var min = compact ? 1 : ResourceMinRows;
            wanted[pane.Id] = Math.Max(min, header + body + footer);
        }

        return wanted;
    }

    private static (int Spaces, int Agents) CompactSectionHeights(int totalH)
    {
        if (totalH <= 0)
            return (0, 0);
        // Too short for two panes and a divider: keep one pane.
        if (totalH < CompactTwoPaneMinHeight)
            return (totalH, 0);
        var spacesH = (totalH + 1) / 2;
        var agentsH = totalH - spacesH - 1;
        if (spacesH <= 0 || agentsH <= 0)
            return (totalH, 0);
        return (spacesH, agentsH);
    }

    private static List<SidebarResourcePaneLayout> AllocateResourceSections(
        CellRect content,
        int startRow,
        int totalRows,
        IReadOnlyList<string> sectionIds,
        bool compact,
        IReadOnlyDictionary<string, int>? wantedRows = null)
    {
        var sections = new List<SidebarResourcePaneLayout>();
        if (sectionIds.Count == 0 || totalRows <= 0)
            return sections;

        var perSection = compact ? Math.Max(1, ResourceMinRows - 1) : ResourceMinRows;
        var weights = new int[sectionIds.Count];
        var weightSum = 0;
        for (var i = 0; i < sectionIds.Count; i++)
        {
            var rows = wantedRows is not null
                && wantedRows.TryGetValue(sectionIds[i], out var wanted)
                && wanted > 0
                ? wanted
                : perSection;
            weights[i] = rows;
            weightSum += rows;
        }

        var baseRow = startRow;
        var used = 0;
        var sawPane = false;
        for (var i = 0; i < sectionIds.Count; i++)
        {
            var rows = i == sectionIds.Count - 1
                ? totalRows - used
                : weightSum <= 0
                    ? Math.Max(1, totalRows / sectionIds.Count)
                    : Math.Max(1, totalRows * weights[i] / weightSum);
            if (rows <= 0)
                break;
            var pane = new CellRect(content.Col, baseRow + used, content.Cols, rows);
            var leading = !compact && sawPane;
            var header = compact ? 0 : ResourceHeaderRows + (leading ? LeadingDividerRows : 0);
            var footer = compact ? 0 : FooterRowsForSection(sectionIds[i]);
            var body = compact
                ? pane
                : new CellRect(
                    pane.Col,
                    pane.Row + header,
                    pane.Cols,
                    Math.Max(0, pane.Rows - header - footer));
            sections.Add(new SidebarResourcePaneLayout(sectionIds[i], pane, body));
            used += rows;
            if (rows > 0)
                sawPane = true;
        }

        return sections;
    }

    public static CellRect FooterRect(
        this SidebarTwoPaneLayout layout,
        SidebarPaneSlot slot,
        bool compact,
        string? sectionId = null)
    {
        var rect = layout.RectFor(slot, sectionId);
        if (compact || FooterRows(slot) <= 0 || rect.Rows <= 0)
            return default;
        return new CellRect(rect.Col, rect.EndRow - SpacesFooterRows, rect.Cols, SpacesFooterRows);
    }

    public static int HeaderRow(CellRect pane, SidebarPaneSlot slot, bool compact, bool leadingDivider)
    {
        if (compact || pane.Rows <= 0)
            return pane.Row;
        return leadingDivider ? pane.Row + LeadingDividerRows : pane.Row;
    }

    public static int VisibleBodyRows(this SidebarTwoPaneLayout layout, SidebarPaneSlot slot, bool compact)
    {
        var body = layout.BodyRect(slot, compact);
        return Math.Max(0, body.Rows);
    }

    public static int HeaderRows(SidebarPaneSlot slot, bool leadingDivider = false)
    {
        var heading = slot switch
        {
            SidebarPaneSlot.Agents => AgentHeaderRows - LeadingDividerRows,
            SidebarPaneSlot.Cubes or SidebarPaneSlot.Resource => ResourceHeaderRows,
            _ => WorkspaceHeaderRows,
        };
        return heading + (leadingDivider ? LeadingDividerRows : 0);
    }

    private static IReadOnlyList<SidebarStackedPane> BuildStack(
        IReadOnlyList<SidebarResourcePaneLayout> resourceSections,
        CellRect spaces,
        CellRect agents,
        bool compact)
    {
        var stack = new List<SidebarStackedPane>();
        void Push(string id, SidebarPaneSlot slot, CellRect pane, CellRect body)
        {
            if (pane.Rows <= 0 || pane.Cols <= 0)
                return;
            var leading = !compact && stack.Count > 0;
            stack.Add(new SidebarStackedPane(id, slot, pane, body, leading));
        }

        foreach (var section in resourceSections)
        {
            var slot = string.Equals(section.SectionId, SidebarTokenGrammar.CubesId, StringComparison.Ordinal)
                ? SidebarPaneSlot.Cubes
                : SidebarPaneSlot.Resource;
            Push(section.SectionId, slot, section.Pane, section.Body);
        }

        var spacesLeading = !compact && stack.Count > 0;
        var spacesHeader = compact ? 0 : HeaderRows(SidebarPaneSlot.Spaces, spacesLeading);
        var spacesFooter = compact ? 0 : FooterRows(SidebarPaneSlot.Spaces);
        Push(
            SidebarTokenGrammar.SpacesId,
            SidebarPaneSlot.Spaces,
            spaces,
            ContentBody(spaces, spacesHeader, spacesFooter, compact));

        var agentsLeading = !compact && stack.Count > 0;
        var agentsHeader = compact ? 0 : HeaderRows(SidebarPaneSlot.Agents, agentsLeading);
        Push(
            SidebarTokenGrammar.AgentsId,
            SidebarPaneSlot.Agents,
            agents,
            ContentBody(agents, agentsHeader, 0, compact));
        return stack;
    }

    private static CellRect ContentBody(CellRect pane, int header, int footer, bool compact)
    {
        if (pane.Rows <= 0 || pane.Cols <= 0)
            return default;
        if (compact)
            return pane;
        var rows = Math.Max(0, pane.Rows - header - footer);
        if (rows <= 0)
            return new CellRect(pane.Col, pane.Row + header, pane.Cols, 0);
        return new CellRect(pane.Col, pane.Row + header, pane.Cols, rows);
    }

    public static int FooterRows(SidebarPaneSlot slot) =>
        slot is SidebarPaneSlot.Spaces or SidebarPaneSlot.Cubes ? SpacesFooterRows : 0;

    private static int FooterRowsForSection(string sectionId) =>
        string.Equals(sectionId, SidebarTokenGrammar.CubesId, StringComparison.Ordinal)
            ? SpacesFooterRows
            : 0;

    public static int ClampScroll(int scroll, int bodyRows, int visibleRows) =>
        Math.Clamp(scroll, 0, Math.Max(0, bodyRows - visibleRows));

    /// <summary>
    /// entries, not painted token rows.
    /// </summary>
    public static int CardCount(IReadOnlyList<SidebarPaintedRow> rows) =>
        CardSpans(rows).Count;

    public static int MaxCardScroll(IReadOnlyList<SidebarPaintedRow> rows, int visibleRows)
    {
        var cards = CardSpans(rows);
        if (cards.Count == 0 || visibleRows <= 0)
            return 0;

        var used = 0;
        var start = cards.Count;
        for (var i = cards.Count - 1; i >= 0; i--)
        {
            var height = Math.Min(cards[i].Count, visibleRows);
            if (used + height > visibleRows)
                break;
            used += height;
            start = i;
        }

        return Math.Min(start, cards.Count - 1);
    }

    public static int ClampCardScroll(int scroll, IReadOnlyList<SidebarPaintedRow> rows, int visibleRows) =>
        Math.Clamp(scroll, 0, MaxCardScroll(rows, visibleRows));

    public static IReadOnlyList<SidebarPaintedRow> VisibleCardRows(
        IReadOnlyList<SidebarPaintedRow> rows,
        int cardScroll,
        int visibleRows)
    {
        var cards = CardSpans(rows);
        if (cards.Count == 0 || visibleRows <= 0)
            return [];

        var startCard = Math.Clamp(cardScroll, 0, cards.Count - 1);
        var visible = new List<SidebarPaintedRow>(Math.Min(visibleRows, rows.Count));
        var used = 0;
        for (var c = startCard; c < cards.Count; c++)
        {
            var (start, count) = cards[c];
            var height = Math.Min(count, visibleRows);
            if (used + height > visibleRows)
                break;
            for (var i = 0; i < height; i++)
                visible.Add(rows[start + i]);
            used += height;
        }

        return visible;
    }

    private static List<(int Start, int Count)> CardSpans(IReadOnlyList<SidebarPaintedRow> rows)
    {
        var cards = new List<(int Start, int Count)>();
        if (rows is null || rows.Count == 0)
            return cards;

        var i = 0;
        while (i < rows.Count)
        {
            var start = i;
            var id = rows[i].Id;
            i++;
            while (i < rows.Count && string.Equals(rows[i].Id, id, StringComparison.Ordinal))
                i++;
            cards.Add((start, i - start));
        }

        return cards;
    }

    private static CellRect ContentRect(CellRect sidebar) =>
        new(sidebar.Col, sidebar.Row, Math.Max(0, sidebar.Cols - EdgeCols), sidebar.Rows);

    private static SidebarTwoPaneLayout Empty(CellRect content) =>
        new(default, default, new CellRect(content.Col, content.Row, content.Cols, 0), null, content.Cols);
}
