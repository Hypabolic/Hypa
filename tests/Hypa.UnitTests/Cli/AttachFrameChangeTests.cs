using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The latency meter closes a key only on a frame that shows a change. These
/// tests pin which applied frames count as a change. The unpacker writes into
/// the storage of the previous frame, so each test marks before it applies.
/// </summary>
public sealed class AttachFrameChangeTests
{
    [Fact]
    public void A_reanchor_that_restates_the_same_grid_and_cursor_shows_no_change()
    {
        var previous = VtCellUnpacker.Apply(Full("ab", cursorCol: 2, generation: 1), previous: null);
        var again = Full("ab", cursorCol: 2, generation: 2);

        Assert.False(ShowsChange(previous, again));
    }

    [Fact]
    public void A_reanchor_with_a_new_cell_shows_a_change()
    {
        var previous = VtCellUnpacker.Apply(Full("ab", cursorCol: 2, generation: 1), previous: null);
        Assert.True(ShowsChange(previous, Full("abc", cursorCol: 2, generation: 2)));
    }

    [Fact]
    public void A_reanchor_with_a_new_cursor_shows_a_change()
    {
        var previous = VtCellUnpacker.Apply(Full("ab", cursorCol: 2, generation: 1), previous: null);
        Assert.True(ShowsChange(previous, Full("ab", cursorCol: 1, generation: 2)));
    }

    [Fact]
    public void A_delta_without_rows_or_a_cursor_move_shows_no_change()
    {
        var previous = VtCellUnpacker.Apply(Full("ab", cursorCol: 2, generation: 1), previous: null);
        Assert.False(ShowsChange(previous, Delta([], cursorCol: 2)));
    }

    [Fact]
    public void A_delta_with_a_cursor_move_shows_a_change()
    {
        var previous = VtCellUnpacker.Apply(Full("ab", cursorCol: 2, generation: 1), previous: null);
        Assert.True(ShowsChange(previous, Delta([], cursorCol: 1)));
    }

    [Fact]
    public void A_delta_with_a_row_shows_a_change()
    {
        var previous = VtCellUnpacker.Apply(Full("ab", cursorCol: 2, generation: 1), previous: null);
        Assert.True(ShowsChange(previous, Delta([new TerminalRenderCellRow { I = 0, T = "abx" }], cursorCol: 2)));
    }

    [Fact]
    public void The_first_frame_of_a_pane_shows_a_change()
    {
        var full = Full("ab", cursorCol: 2, generation: 1);
        Assert.True(AttachSession.FrameShowsChange(
            AttachSession.MarkFrame(null, full),
            VtCellUnpacker.Apply(full, previous: null),
            full));
    }

    private static bool ShowsChange(AssembledSnapshot previous, TerminalRenderCellsPayload body)
    {
        var before = AttachSession.MarkFrame(previous, body);
        var frame = VtCellUnpacker.Apply(body, previous);
        return AttachSession.FrameShowsChange(before, frame, body);
    }

    private static TerminalRenderCellsPayload Full(string text, int cursorCol, long generation) =>
        new()
        {
            PaneId = "p1",
            Kind = TerminalRenderCellsPayload.KindCells,
            Full = true,
            Reanchor = true,
            GridCols = 4,
            GridRows = 2,
            Generation = generation,
            Rows = [new TerminalRenderCellRow { I = 0, T = text }],
            Cursor = new TerminalRenderCursorPayload { Col = cursorCol, Row = 0, Visible = true },
        };

    private static TerminalRenderCellsPayload Delta(TerminalRenderCellRow[] rows, int cursorCol) =>
        new()
        {
            PaneId = "p1",
            Kind = TerminalRenderCellsPayload.KindCells,
            Full = false,
            GridCols = 4,
            GridRows = 2,
            Generation = 2,
            BaseGeneration = 1,
            Rows = rows,
            Cursor = new TerminalRenderCursorPayload { Col = cursorCol, Row = 0, Visible = true },
        };
}
