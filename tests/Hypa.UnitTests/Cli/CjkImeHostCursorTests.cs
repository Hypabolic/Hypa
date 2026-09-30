using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class CjkImeHostCursorTests
{
    [Fact]
    public void Disabled_reveal_keeps_a_hidden_pane_cursor_hidden()
    {
        var live = Live("p1", agent: "claude");
        live.SetPaneFrame(Frame("p1", visible: false, hasCursor: true, col: 3, row: 2, shape: 4));
        AttachSession.StampHostCursor(live, new CellRect(2, 1, 20, 8), "p1");
        Assert.False(live.Host.Cursor.Visible);
        Assert.Equal(4, live.Host.Cursor.Shape);
        Assert.Equal(5, live.Host.Cursor.Col);
        Assert.Equal(3, live.Host.Cursor.Row);
    }

    [Fact]
    public void Reveal_forces_the_configured_shape_when_the_pane_hid_the_cursor()
    {
        var live = Live("p1", agent: "claude");
        live.CjkIme = CjkImeRevealFilter.From(new AttachExperimentalConfig
        {
            RevealHiddenCursorForCjkIme = true,
            CjkImeCursorShape = "bar",
        });
        live.SetPaneFrame(Frame("p1", visible: false, hasCursor: true, col: 3, row: 2, shape: 4));
        AttachSession.StampHostCursor(live, new CellRect(2, 1, 20, 8), "p1");
        Assert.True(live.Host.Cursor.Visible);
        Assert.Equal(5, live.Host.Cursor.Shape);
        Assert.Equal(5, live.Host.Cursor.Col);
        Assert.Equal(3, live.Host.Cursor.Row);
    }

    [Fact]
    public void Agent_filter_skips_unlisted_panes()
    {
        var live = Live("p1", agent: "pi");
        live.CjkIme = CjkImeRevealFilter.From(new AttachExperimentalConfig
        {
            RevealHiddenCursorForCjkIme = true,
            CjkImeAgents = ["claude"],
        });
        live.SetPaneFrame(Frame("p1", visible: false, hasCursor: true, col: 0, row: 0, shape: 1));
        AttachSession.StampHostCursor(live, new CellRect(0, 0, 20, 8), "p1");
        Assert.False(live.Host.Cursor.Visible);
        Assert.Equal(1, live.Host.Cursor.Shape);
    }

    [Fact]
    public void Reveal_falls_back_to_the_pane_origin_when_there_is_no_cursor()
    {
        var live = Live("p1", agent: "codex");
        live.CjkIme = CjkImeRevealFilter.From(new AttachExperimentalConfig
        {
            RevealHiddenCursorForCjkIme = true,
        });
        live.SetPaneFrame(Frame("p1", visible: false, hasCursor: false, col: 0, row: 0, shape: 0));
        AttachSession.StampHostCursor(live, new CellRect(4, 5, 20, 8), "p1");
        Assert.True(live.Host.Cursor.Visible);
        Assert.Equal(2, live.Host.Cursor.Shape);
        Assert.Equal(4, live.Host.Cursor.Col);
        Assert.Equal(5, live.Host.Cursor.Row);
    }

    private static AttachLiveState Live(string paneId, string? agent)
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        return new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = null!,
            PaneId = paneId,
            SidebarInput = new SidebarComposeInput
            {
                Ui = AttachUiConfig.Default,
                Panes =
                [
                    new SidebarPaneItem
                    {
                        Id = paneId,
                        TabId = "t1",
                        WorkspaceId = "w1",
                        Agent = agent,
                    },
                ],
            },
        };
    }

    private static AssembledSnapshot Frame(
        string paneId,
        bool visible,
        bool hasCursor,
        int col,
        int row,
        int shape)
    {
        var cells = new AssembledCell[1][];
        cells[0] = [AssembledCell.Blank];
        return new AssembledSnapshot(
            paneId,
            20,
            8,
            "ghostty",
            "main",
            cells,
            default,
            new AssembledCursor(col, row, visible, shape, hasCursor),
            OccupantGeneration: 1,
            Generation: 1,
            IngestFull: true);
    }
}
