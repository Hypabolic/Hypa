using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class SidebarTwoPaneLayoutPolicyTests
{
    [Fact]
    public void Expanded_DefaultSplit_IsHalfAndClampsManualRatio()
    {
        var sidebar = new CellRect(0, 0, 26, 24);
        var even = SidebarTwoPaneLayoutPolicy.Expanded(sidebar);
        Assert.Equal(12, even.Spaces.Rows);
        Assert.Equal(12, even.Agents.Rows);
        Assert.Equal(12, even.DividerRow);
        Assert.Equal(SidebarTwoPaneLayoutPolicy.DefaultSplitRatio, 0.5f);

        var tall = SidebarTwoPaneLayoutPolicy.Expanded(sidebar, 0.9f);
        Assert.Equal(21, tall.Spaces.Rows);
        Assert.Equal(3, tall.Agents.Rows);

        Assert.Equal(0.5f, SidebarTwoPaneLayoutPolicy.ClampSplitRatio(float.NaN));
        Assert.Equal(0.1f, SidebarTwoPaneLayoutPolicy.ClampSplitRatio(0.01f));
        Assert.Equal(0.9f, SidebarTwoPaneLayoutPolicy.ClampSplitRatio(1.2f));
    }

    [Fact]
    public void Expanded_SpacesBody_ReservesHeaderSpacerAndFooter()
    {
        var layout = SidebarTwoPaneLayoutPolicy.Expanded(new CellRect(0, 0, 26, 24));
        var body = layout.BodyRect(SidebarPaneSlot.Spaces, compact: false);
        var footer = layout.FooterRect(SidebarPaneSlot.Spaces, compact: false);
        Assert.Equal(layout.Spaces.Row + SidebarTwoPaneLayoutPolicy.WorkspaceHeaderRows, body.Row);
        Assert.Equal(
            layout.Spaces.Rows - SidebarTwoPaneLayoutPolicy.WorkspaceHeaderRows - SidebarTwoPaneLayoutPolicy.SpacesFooterRows,
            body.Rows);
        Assert.Equal(layout.Spaces.EndRow - 1, footer.Row);
        Assert.Equal(1, footer.Rows);
        Assert.Equal(body.EndRow, footer.Row);
        Assert.Equal(9, layout.VisibleBodyRows(SidebarPaneSlot.Spaces, compact: false));
    }

    [Fact]
    public void Expanded_AgentsBody_StartsAfterHeaderRows()
    {
        var layout = SidebarTwoPaneLayoutPolicy.Expanded(new CellRect(0, 0, 26, 24));
        var body = layout.BodyRect(SidebarPaneSlot.Agents, compact: false);
        Assert.Equal(layout.Agents.Row + SidebarTwoPaneLayoutPolicy.AgentHeaderRows, body.Row);
        Assert.Equal(
            layout.Agents.Rows - SidebarTwoPaneLayoutPolicy.AgentHeaderRows,
            body.Rows);
        Assert.Equal(0, layout.FooterRect(SidebarPaneSlot.Agents, compact: false).Rows);
    }

    [Fact]
    public void Compact_HidesFooterAndUsesFullPaneBody()
    {
        var layout = SidebarTwoPaneLayoutPolicy.Compact(new CellRect(0, 0, 4, 20));
        Assert.NotNull(layout.DividerRow);
        var spacesBody = layout.BodyRect(SidebarPaneSlot.Spaces, compact: true);
        Assert.Equal(layout.Spaces, spacesBody);
        Assert.Equal(0, layout.FooterRect(SidebarPaneSlot.Spaces, compact: true).Rows);
        Assert.Equal(layout.Spaces.Rows, layout.VisibleBodyRows(SidebarPaneSlot.Spaces, compact: true));
    }

    [Fact]
    public void Compact_TinyHeight_KeepsOnePane()
    {
        var layout = SidebarTwoPaneLayoutPolicy.Compact(new CellRect(0, 0, 4, 6));
        Assert.Null(layout.DividerRow);
        Assert.True(layout.Spaces.Rows > 0);
        Assert.Equal(0, layout.Agents.Rows);
    }

    [Fact]
    public void SplitRatioFromRow_UsesFullSidebarHeight()
    {
        var sidebar = new CellRect(0, 0, 26, 20);
        Assert.Equal(0.5f, SidebarTwoPaneLayoutPolicy.SplitRatioFromRow(sidebar, 10), 3);
        Assert.Equal(0.9f, SidebarTwoPaneLayoutPolicy.SplitRatioFromRow(sidebar, 18), 3);
        Assert.Equal(0.1f, SidebarTwoPaneLayoutPolicy.SplitRatioFromRow(sidebar, 1), 3);
    }

    [Fact]
    public void RectFor_MapsCubesOntoResource()
    {
        var layout = SidebarTwoPaneLayoutPolicy.Expanded(
            new CellRect(0, 0, 26, 24),
            resourceSectionIds: [SidebarTokenGrammar.CubesId]);
        Assert.Equal(layout.Resource, layout.RectFor(SidebarPaneSlot.Cubes));
        Assert.True(layout.Resource.Rows >= SidebarTwoPaneLayoutPolicy.MinPaneRows);
        Assert.Equal(0, layout.Resource.Row);
        Assert.Equal(layout.Resource.EndRow, layout.Spaces.Row);
        Assert.Equal(layout.Spaces.EndRow, layout.Agents.Row);
        Assert.False(layout.HasLeadingDivider(SidebarPaneSlot.Cubes, SidebarTokenGrammar.CubesId));
        Assert.True(layout.HasLeadingDivider(SidebarPaneSlot.Spaces));
        Assert.True(layout.HasLeadingDivider(SidebarPaneSlot.Agents));
    }

    [Fact]
    public void Cubes_section_grows_to_fit_catalog_rows()
    {
        var wanted = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [SidebarTokenGrammar.CubesId] = 8,
        };
        var layout = SidebarTwoPaneLayoutPolicy.Expanded(
            new CellRect(0, 0, 26, 24),
            resourceSectionIds: [SidebarTokenGrammar.CubesId],
            resourceWantedRows: wanted);
        Assert.Equal(8, layout.Resource.Rows);
        Assert.Equal(0, layout.Resource.Row);
        Assert.Equal(8, layout.Spaces.Row);
        Assert.True(layout.HasLeadingDivider(SidebarPaneSlot.Spaces));
        var body = layout.ResourceSections[0].Body;
        Assert.Equal(5, body.Rows);
    }

    [Fact]
    public void WantedResourceRows_counts_cube_cards()
    {
        var pane = new SidebarPaneView
        {
            Id = SidebarTokenGrammar.CubesId,
            Slot = SidebarPaneSlot.Cubes,
            Header = CubesChromeSectionStrategy.HeaderText,
            Visible = true,
            Rows =
            [
                new SidebarPaintedRow { Id = "a", Kind = SidebarRowKind.Cube, Label = "Intel Mac" },
                new SidebarPaintedRow { Id = "a", Kind = SidebarRowKind.Cube, Label = "local" },
                new SidebarPaintedRow { Id = "b", Kind = SidebarRowKind.Cube, Label = "docker" },
                new SidebarPaintedRow { Id = "b", Kind = SidebarRowKind.Cube, Label = "peer" },
            ],
        };
        var wanted = SidebarTwoPaneLayoutPolicy.WantedResourceRows([pane], compact: false);
        Assert.Equal(7, wanted[SidebarTokenGrammar.CubesId]);
    }

    [Fact]
    public void RectFor_OmitsResourceWhenNotRequested()
    {
        var layout = SidebarTwoPaneLayoutPolicy.Expanded(new CellRect(0, 0, 26, 24));
        Assert.Equal(0, layout.Resource.Rows);
        Assert.Equal(0, layout.Spaces.Row);
        Assert.False(layout.HasLeadingDivider(SidebarPaneSlot.Spaces));
        Assert.True(layout.HasLeadingDivider(SidebarPaneSlot.Agents));
    }

    [Fact]
    public void Adjacent_stacked_panes_each_get_a_leading_divider()
    {
        var layout = SidebarTwoPaneLayoutPolicy.Expanded(
            new CellRect(0, 0, 26, 24),
            resourceSectionIds: [SidebarTokenGrammar.CubesId, "atomic"]);
        Assert.Equal(2, layout.ResourceSections.Count);
        Assert.Equal(layout.ResourceSections[0].Pane.EndRow, layout.ResourceSections[1].Pane.Row);
        Assert.False(layout.HasLeadingDivider(SidebarPaneSlot.Cubes, SidebarTokenGrammar.CubesId));
        Assert.True(layout.HasLeadingDivider(SidebarPaneSlot.Resource, "atomic"));
        Assert.True(layout.HasLeadingDivider(SidebarPaneSlot.Spaces));
        Assert.Equal(layout.Resource.EndRow, layout.Spaces.Row);
        var plugin = layout.ResourceSections[1];
        Assert.Equal(plugin.Pane.Row + 3, plugin.Body.Row);
    }

    [Fact]
    public void VisibleCardRows_IndependentOfFooterPlacement()
    {
        var rows = new List<SidebarPaintedRow>();
        for (var i = 0; i < 6; i++)
        {
            rows.Add(new SidebarPaintedRow
            {
                Id = "ws-" + i,
                Kind = SidebarRowKind.Workspace,
                Label = "row-" + i,
            });
        }

        var visible = SidebarTwoPaneLayoutPolicy.VisibleCardRows(rows, cardScroll: 2, visibleRows: 3);
        Assert.Equal(["row-2", "row-3", "row-4"], visible.Select(r => r.Label).ToArray());
    }
}
