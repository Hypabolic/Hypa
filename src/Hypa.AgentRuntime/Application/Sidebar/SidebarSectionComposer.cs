using Hypa.AgentRuntime.Domain.AttachConfig;
using SafeDisplayText = Hypa.AgentRuntime.Application.SafeDisplayText;

namespace Hypa.AgentRuntime.Application.Sidebar;

/// <summary>
/// Registry composer for sidebar panes. Compose order is spaces, then
/// agents, then Cubes. Visual order paints cubes first. Section
/// <c>order</c> does not swap spaces and agents.
/// </summary>
public static class SidebarSectionComposer
{
    public const int CompactWidth = 4;
    public const int DefaultWidth = 26;

    /// <summary>
    /// under the name after <c>state_icon</c>. Cubes have no icon, so Hypa
    /// keeps a two-column hang so reachability is not a second cube.
    /// </summary>
    public const int CardHangingIndentCols = 2;

    public static IReadOnlyList<ResolvedSidebarSection> ResolveSections(AttachUiConfig ui)
    {
        ArgumentNullException.ThrowIfNull(ui);
        var agents = ui.Sidebar.Agents;
        var spaces = ui.Sidebar.Spaces;
        var agentsOrder = agents.Order;
        var spacesOrder = spaces.Order;
        var agentsCollapsed = agents.Collapsed;
        var spacesCollapsed = spaces.Collapsed;
        var cubes = ui.Sidebar.Cubes;
        var agentsRows = agents.Rows;
        var spacesRows = spaces.Rows;
        var cubesOrder = cubes.Order;
        var cubesCollapsed = cubes.Collapsed;
        var cubesRows = cubes.Rows;

        foreach (var section in ui.Sidebar.Sections)
        {
            if (string.Equals(section.Id, SidebarTokenGrammar.AgentsId, StringComparison.Ordinal))
            {
                agentsOrder = section.Order;
                agentsCollapsed = section.Collapsed;
                if (section.RowsSpecified)
                    agentsRows = section.Rows;
            }
            else if (string.Equals(section.Id, SidebarTokenGrammar.SpacesId, StringComparison.Ordinal))
            {
                spacesOrder = section.Order;
                spacesCollapsed = section.Collapsed;
                if (section.RowsSpecified)
                    spacesRows = section.Rows;
            }
            else if (string.Equals(section.Id, SidebarTokenGrammar.CubesId, StringComparison.Ordinal))
            {
                cubesOrder = section.Order;
                cubesCollapsed = section.Collapsed;
                if (section.RowsSpecified)
                    cubesRows = section.Rows;
            }
        }

        var sections = new List<ResolvedSidebarSection>
        {
            new()
            {
                Id = SidebarTokenGrammar.AgentsId,
                Title = "Agents",
                Order = agentsOrder,
                Collapsed = agentsCollapsed,
                Config = agents with { Order = agentsOrder, Collapsed = agentsCollapsed, Rows = agentsRows },
            },
            new()
            {
                Id = SidebarTokenGrammar.SpacesId,
                Title = "Workspaces",
                Order = spacesOrder,
                Collapsed = spacesCollapsed,
                Config = spaces with { Order = spacesOrder, Collapsed = spacesCollapsed, Rows = spacesRows },
            },
            new()
            {
                Id = SidebarTokenGrammar.CubesId,
                Title = "Cubes",
                Order = cubesOrder,
                Collapsed = cubesCollapsed,
                Config = cubes with { Order = cubesOrder, Collapsed = cubesCollapsed, Rows = cubesRows },
            },
        };

        foreach (var binding in SidebarPluginSectionRegistry.Bindings(ui))
        {
            sections.Add(new ResolvedSidebarSection
            {
                Id = binding.Id,
                Title = binding.Title,
                Order = binding.Order,
                Collapsed = binding.Collapsed,
                Config = new AttachSidebarSectionConfig
                {
                    Order = binding.Order,
                    Collapsed = binding.Collapsed,
                    Rows = binding.Rows,
                    RowGap = binding.RowGap,
                },
            });
        }

        return sections;
    }

    public static SidebarFrame Compose(SidebarComposeInput input, ChromeSectionRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Ui);
        registry ??= ChromeSectionRegistry.Core();

        var display = ResolveDisplay(input);
        var width = display switch
        {
            SidebarCollapseDisplay.Hidden => 0,
            SidebarCollapseDisplay.Compact => CompactWidth,
            _ => ClampWidth(input.RequestedWidth > 0 ? input.RequestedWidth : input.Ui.SidebarWidth, input.Ui),
        };

        var resolvedById = new Dictionary<string, ResolvedSidebarSection>(StringComparer.Ordinal);
        foreach (var resolved in ResolveSections(input.Ui))
            resolvedById[resolved.Id] = resolved;

        var panes = new List<SidebarPaneView>();
        foreach (var strategy in registry.PaneOrder())
        {
            if (!resolvedById.TryGetValue(strategy.Id, out var resolved))
            {
                resolved = new ResolvedSidebarSection
                {
                    Id = strategy.Id,
                    Title = strategy.Id,
                    Order = 0,
                    Collapsed = false,
                    Config = new AttachSidebarSectionConfig(),
                };
            }

            var collapsed = SectionIsCollapsed(resolved, input.CollapsedSectionIds);
            var visible = display is not SidebarCollapseDisplay.Hidden;
            panes.Add(StampNavigation(
                strategy.Compose(input, resolved, collapsed, visible, width, display),
                input));
        }

        var sections = new List<SidebarSectionView>(panes.Count);
        foreach (var pane in panes)
            sections.Add(pane.ToSection());

        return new SidebarFrame
        {
            Width = width,
            Display = display,
            Panes = panes,
            Sections = sections,
        };
    }

    /// <summary>
    /// separately from focus. Strategies keep <c>Selected</c> as focus.
    /// </summary>
    internal static SidebarPaneView StampNavigation(SidebarPaneView pane, SidebarComposeInput input)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(input);
        var id = pane.Slot switch
        {
            SidebarPaneSlot.Spaces => input.NavigatedWorkspaceId,
            SidebarPaneSlot.Agents => input.NavigatedPaneId,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(id))
            return pane;

        var rows = new SidebarPaintedRow[pane.Rows.Count];
        for (var i = 0; i < pane.Rows.Count; i++)
        {
            var row = pane.Rows[i];
            var match = string.Equals(row.Id, id, StringComparison.Ordinal);
            rows[i] = row with
            {
                Navigated = match && row.CardRowIndex == 0,
                CardNavigated = match,
            };
        }

        return pane with { Rows = rows };
    }

    public static int ClampWidth(int width, AttachUiConfig ui)
    {
        ArgumentNullException.ThrowIfNull(ui);
        var min = ui.SidebarMinWidth > 0 ? ui.SidebarMinWidth : 18;
        var max = ui.SidebarMaxWidth > 0 ? ui.SidebarMaxWidth : 36;
        if (max < min)
            max = min;
        return Math.Clamp(width, min, max);
    }

    private static SidebarCollapseDisplay ResolveDisplay(SidebarComposeInput input)
    {
        if (input.Expanded)
            return SidebarCollapseDisplay.Expanded;
        return input.Ui.SidebarCollapsedMode is SidebarCollapsedMode.Hidden
            ? SidebarCollapseDisplay.Hidden
            : SidebarCollapseDisplay.Compact;
    }

    internal static bool SectionIsCollapsed(
        ResolvedSidebarSection resolved,
        IReadOnlySet<string>? collapsedSectionIds)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        var userOverride = collapsedSectionIds is not null && collapsedSectionIds.Contains(resolved.Id);
        return resolved.Collapsed ^ userOverride;
    }

    internal static SidebarTokenValues ValuesForPane(
        SidebarComposeInput input,
        SidebarPaneItem pane,
        SidebarWorkspaceItem? workspace,
        SidebarTabItem? tab)
    {
        var title = pane.TerminalTitle.Length > 0 ? pane.TerminalTitle : pane.Label;
        return new SidebarTokenValues
        {
            StateIcon = SidebarTokenGrammar.StateIcon(pane.State, input.Ui.StatusIndicators),
            StateText = SidebarTokenGrammar.StateText(pane.State),
            Workspace = DisplayName(workspace?.Label, pane.WorkspaceId),
            Tab = DisplayName(tab?.Label, pane.TabId),
            Pane = DisplayName(pane.Label, pane.Id),
            Agent = pane.Agent ?? "",
            TerminalTitle = title,
            TerminalTitleStripped = StripTitle(title),
            Custom = pane.Tokens,
        };
    }

    internal static void AppendTokenRows(
        List<SidebarPaintedRow> rows,
        IReadOnlyList<IReadOnlyList<SidebarTokenSpec>> tokenRows,
        SidebarTokenValues values,
        int width,
        bool compact,
        int rowGap,
        SidebarPaintedRow template)
    {
        var pad = 1 + Math.Max(0, template.IndentCols);
        var iconWidth = SafeDisplayText.Width(values.StateIcon);
        var nameColumn = WorkspaceNameColumn(pad, iconWidth);
        if (compact)
        {
            rows.Add(template with
            {
                NameColumn = nameColumn,
                IndentCols = 0,
                CardRowIndex = 0,
                CardFocused = template.Selected,
                CardNavigated = template.Navigated,
            });
            return;
        }

        var max = Math.Max(1, width - 1);
        var specs = tokenRows.Count == 0
            ? new List<(string Label, IReadOnlyList<SidebarRowToken> Tokens)> { ("", []) }
            : tokenRows
                .Select(row => (
                    Label: SidebarTokenGrammar.RenderRow(row, values, max),
                    Tokens: SidebarTokenGrammar.FitVisibleTokens(
                        SidebarTokenGrammar.TokensForRow(row, values),
                        max)))
                .ToList();
        for (var i = 0; i < specs.Count; i++)
        {
            if (i > 0 && rowGap > 0)
            {
                for (var g = 0; g < rowGap; g++)
                {
                    rows.Add(template with
                    {
                        Label = "",
                        Tokens = [],
                        Selected = false,
                        Navigated = false,
                        CardFocused = template.Selected,
                        CardNavigated = template.Navigated,
                        PrefixRole = WorktreeWorkspaceGrouping.PrefixRole(
                            template.IndentCols >= WorktreeWorkspaceGrouping.ChildIndentCols,
                            template.PrefixRole is SidebarPrefixRole.TreeLast,
                            i),
                        IndentCols = nameColumn,
                        CardRowIndex = i,
                        NameColumn = nameColumn,
                    });
                }
            }

            rows.Add(template with
            {
                Label = specs[i].Label,
                Tokens = specs[i].Tokens,
                Selected = template.Selected && i == 0,
                Navigated = template.Navigated && i == 0,
                CardFocused = template.Selected,
                CardNavigated = template.Navigated,
                PrefixRole = WorktreeWorkspaceGrouping.PrefixRole(
                    template.IndentCols >= WorktreeWorkspaceGrouping.ChildIndentCols,
                    template.PrefixRole is SidebarPrefixRole.TreeLast,
                    i),
                IndentCols = i == 0 ? pad : nameColumn,
                CardRowIndex = i,
                NameColumn = nameColumn,
            });
        }
    }

    /// <summary>
    /// (<c>src/ui/sidebar.rs:1350-1356</c>, <c>tokens.rs:144-148</c>).
    /// </summary>
    internal static int WorkspaceNameColumn(int pad, int iconWidth)
    {
        var column = pad + iconWidth;
        if (iconWidth > 0)
            column += SafeDisplayText.Width(" ");
        else
            column += CardHangingIndentCols;
        return column;
    }

    internal static string CompactLabel(int index, string icon)
    {
        var digit = index >= 1 && index <= 9 ? index.ToString() : "·";
        var mark = string.IsNullOrEmpty(icon) ? "·" : icon;
        return digit + mark;
    }

    internal static string DisplayName(string? label, string? id) =>
        string.IsNullOrWhiteSpace(label) ? id ?? "" : label;

    private static string StripTitle(string title)
    {
        if (string.IsNullOrEmpty(title))
            return "";
        var trimmed = title.Trim();
        var sep = trimmed.LastIndexOf(" — ", StringComparison.Ordinal);
        if (sep < 0)
            sep = trimmed.LastIndexOf(" - ", StringComparison.Ordinal);
        if (sep > 0)
            return trimmed[..sep].Trim();
        return trimmed;
    }

    internal static string AggregateState(IReadOnlyList<SidebarPaneItem> panes)
    {
        if (panes.Count == 0)
            return SidebarTokenGrammar.Unknown;
        var best = panes[0];
        var rank = SidebarTokenGrammar.PriorityRank(best.State);
        foreach (var pane in panes)
        {
            var next = SidebarTokenGrammar.PriorityRank(pane.State);
            if (next >= rank)
                continue;
            rank = next;
            best = pane;
        }

        return SidebarTokenGrammar.CanonicalState(best.State);
    }

    internal static SidebarGitInfo ResolveGit(ISidebarGitStatus? git, string cwd)
    {
        if (git is null || string.IsNullOrWhiteSpace(cwd))
            return new SidebarGitInfo();
        try
        {
            return git.Resolve(cwd);
        }
        catch (Exception)
        {
            return new SidebarGitInfo();
        }
    }
}
