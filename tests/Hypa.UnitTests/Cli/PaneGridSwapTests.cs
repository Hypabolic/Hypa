using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// A full body and a row shift change every row of a pane. The client
/// unpacks such a body into a second grid of the pane, so the grid of the
/// frame before the batch does not change. A refused write puts that frame
/// back with its own cells and no undo copy of the whole grid. After an
/// accepted write, the two grids of the pane change places.
/// </summary>
public sealed class PaneGridSwapTests
{
    [Fact]
    public void Accepted_full_bodies_and_row_shifts_use_two_grids_in_turn()
    {
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
        var box = Box(live, "p2");
        Assert.True(AttachSession.TryWriteCells(tty, live, Full(box, 1, "first", reanchor: true), reanchorBeforePaint: true));
        var grids = new List<AssembledCell[]> { Stored(live).Storage! };

        for (var generation = 2L; generation <= 9; generation++)
        {
            var before = Stored(live);
            var beforeCells = Cells(before);
            var body = generation % 3 == 0
                ? Shift(box, generation - 1, generation, 2, "shift " + generation)
                : Full(box, generation, "full " + generation);
            Assert.True(AttachSession.TryWriteCells(tty, live, body, reanchorBeforePaint: true));

            var after = Stored(live);
            Assert.Equal(generation, after.Generation);
            Assert.NotSame(before.Storage, after.Storage);
            // The frame before the body kept its cells.
            AssertCells(beforeCells, before);
            if (!grids.Any(grid => ReferenceEquals(grid, after.Storage)))
                grids.Add(after.Storage!);
        }

        Assert.Equal(2, grids.Count);
        // The last body is a shift of 2 rows after the full body 8.
        Assert.Equal("full 8 2", RowText(Stored(live), 0));
    }

    public static TheoryData<string> RefusedBatches() =>
    [
        "full",
        "shift",
        "two fulls",
        "full then delta",
        "delta then full",
        "shift then full",
    ];

    [Theory]
    [MemberData(nameof(RefusedBatches))]
    public void Refused_write_puts_back_the_frame_before_the_batch_with_its_cells(string batch)
    {
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
        var box = Box(live, "p2");
        Assert.True(AttachSession.TryWriteCells(tty, live, Full(box, 1, "first", reanchor: true), reanchorBeforePaint: true));
        // An accepted full body: the pane has a spare grid after it.
        Assert.True(AttachSession.TryWriteCells(tty, live, Full(box, 2, "second"), reanchorBeforePaint: true));
        Assert.True(AttachSession.TryWriteCells(tty, live, Shift(box, 2, 3, 1, "kept"), reanchorBeforePaint: true));
        var before = Stored(live);
        var beforeCells = Cells(before);

        live.PresentationFrozen = true;
        TerminalRenderCellsPayload[] bodies = batch switch
        {
            "full" => [Full(box, 4, "refused")],
            "shift" => [Shift(box, 3, 4, 3, "refused")],
            "two fulls" => [Full(box, 4, "refused"), Full(box, 5, "refused again")],
            "full then delta" => [Full(box, 4, "refused"), Delta(box, 4, 5, 2, "refused delta")],
            "delta then full" => [Delta(box, 3, 4, 0, "refused delta"), Full(box, 5, "refused")],
            "shift then full" => [Shift(box, 3, 4, 2, "refused"), Full(box, 5, "refused again")],
            _ => throw new ArgumentOutOfRangeException(nameof(batch)),
        };
        var results = AttachSession.TryWriteCellsBatch(tty, live, bodies, reanchorBeforePaint: true);
        Assert.All(results, Assert.False);

        var restored = Stored(live);
        Assert.Same(before, restored);
        Assert.Equal(3, restored.Generation);
        AssertCells(beforeCells, restored);

        live.PresentationFrozen = false;
        if (!bodies.Any(body => body.Full))
        {
            // A delta that chains on the restored frame paints on its cells.
            // A refused full body resets the chain, so the mux sends a full
            // body next.
            Assert.True(AttachSession.TryWriteCells(tty, live, Delta(box, 3, 6, 4, "after"), reanchorBeforePaint: true));
            Assert.Equal("after", RowText(Stored(live), 4));
            Assert.Equal(RowText(before, 0), RowText(Stored(live), 0));
        }

        Assert.True(AttachSession.TryWriteCells(tty, live, Full(box, 7, "full after"), reanchorBeforePaint: true));
        Assert.Equal("full after", RowText(Stored(live), 0));
        Assert.Equal("full after 4", RowText(Stored(live), 4));
        Assert.Equal("", RowText(Stored(live), box.Rows - 1));
    }

    public static TheoryData<int> Shifts() => [2, -2, 23, -23];

    [Theory]
    [MemberData(nameof(Shifts))]
    public void Unpack_of_a_shift_into_a_separate_grid_equals_the_unpack_in_place(int shift)
    {
        var previous = VtCellUnpacker.Apply(Lines(8, 24, generation: 1), previous: null);
        var original = Cells(previous);
        var body = new TerminalRenderCellsPayload
        {
            Kind = TerminalRenderCellsPayload.KindCells,
            PaneId = "p2",
            GridCols = 8,
            GridRows = 24,
            BaseGeneration = 1,
            Generation = 2,
            Shift = shift,
            Rows = [new TerminalRenderCellRow { I = 5, T = "new" }],
        };

        var separate = VtCellUnpacker.Apply(body, previous, new AssembledCell[8 * 24]);
        // The previous grid does not change.
        AssertCells(original, previous);
        var inPlace = VtCellUnpacker.Apply(body, previous);
        Assert.Same(previous.Storage, inPlace.Storage);
        AssertCells(Cells(inPlace), separate);
    }

    [Fact]
    public void Unpack_of_a_full_body_into_a_separate_grid_equals_the_unpack_in_place()
    {
        var previous = VtCellUnpacker.Apply(Lines(8, 6, generation: 1), previous: null);
        var original = Cells(previous);
        var body = new TerminalRenderCellsPayload
        {
            Kind = TerminalRenderCellsPayload.KindCells,
            PaneId = "p2",
            Full = true,
            GridCols = 8,
            GridRows = 6,
            Generation = 2,
            // Out of order and one row twice: each row starts blank.
            Rows =
            [
                new TerminalRenderCellRow { I = 3, T = "three" },
                new TerminalRenderCellRow { I = 1, T = "one" },
                new TerminalRenderCellRow { I = 3, T = "3" },
                new TerminalRenderCellRow { I = 4, T = "partial", C = 2 },
            ],
        };

        var separate = VtCellUnpacker.Apply(body, previous, new AssembledCell[8 * 6]);
        AssertCells(original, previous);
        var inPlace = VtCellUnpacker.Apply(body, previous);
        AssertCells(Cells(inPlace), separate);
        Assert.Equal("3", RowText(separate, 3));
        Assert.Equal("one", RowText(separate, 1));
        Assert.Equal("  partia", RowText(separate, 4));
        Assert.Equal("", RowText(separate, 0));
    }

    private static TerminalRenderCellsPayload Lines(int cols, int rows, long generation) => new()
    {
        Kind = TerminalRenderCellsPayload.KindCells,
        PaneId = "p2",
        Full = true,
        Reanchor = true,
        GridCols = cols,
        GridRows = rows,
        Generation = generation,
        Rows = Enumerable.Range(0, rows).Select(r => new TerminalRenderCellRow { I = r, T = "row " + r }).ToList(),
    };

    private static CellRect Box(AttachLiveState live, string paneId) =>
        Assert.Single(live.Chrome!.Panes, pane => pane.PaneId == paneId).Content;

    private static AssembledSnapshot Stored(AttachLiveState live)
    {
        Assert.True(live.TryGetPaneFrame("p2", out var frame));
        return frame!;
    }

    private static AssembledCell[] Cells(AssembledSnapshot frame)
    {
        var cells = new AssembledCell[frame.Cols * frame.Rows];
        for (var r = 0; r < frame.Rows; r++)
        {
            for (var c = 0; c < frame.Cols; c++)
                cells[r * frame.Cols + c] = frame.CellAt(c, r);
        }

        return cells;
    }

    private static void AssertCells(AssembledCell[] want, AssembledSnapshot frame)
    {
        Assert.Equal(want.Length, frame.Cols * frame.Rows);
        for (var r = 0; r < frame.Rows; r++)
        {
            for (var c = 0; c < frame.Cols; c++)
            {
                var got = frame.CellAt(c, r);
                Assert.True(
                    HostFrame.CellsEqual(want[r * frame.Cols + c], got),
                    $"cell {c},{r}: want '{want[r * frame.Cols + c].Text}' got '{got.Text}'");
            }
        }
    }

    private static string RowText(AssembledSnapshot frame, int row) =>
        VtCellUnpacker.RowText(frame, row).TrimEnd();

    /// <summary>A full body with no reanchor: every row has text.</summary>
    private static TerminalRenderCellsPayload Full(CellRect box, long generation, string text, bool reanchor = false) => new()
    {
        Kind = TerminalRenderCellsPayload.KindCells,
        PaneId = "p2",
        Full = true,
        Reanchor = reanchor,
        GridCols = box.Cols,
        GridRows = box.Rows,
        Generation = generation,
        Rows = Enumerable.Range(0, box.Rows - 1)
            .Select(r => new TerminalRenderCellRow { I = r, T = r == 0 ? text : text + " " + r })
            .ToList(),
        Cursor = new TerminalRenderCursorPayload { Col = 0, Row = box.Rows - 1, Visible = true },
    };

    /// <summary>A row shift up by <paramref name="shift"/> rows with one new row at the bottom.</summary>
    private static TerminalRenderCellsPayload Shift(CellRect box, long baseGeneration, long generation, int shift, string text) => new()
    {
        Kind = TerminalRenderCellsPayload.KindCells,
        PaneId = "p2",
        GridCols = box.Cols,
        GridRows = box.Rows,
        BaseGeneration = baseGeneration,
        Generation = generation,
        Shift = shift,
        Rows = [new TerminalRenderCellRow { I = box.Rows - 2, T = text }],
        Cursor = new TerminalRenderCursorPayload { Col = 0, Row = box.Rows - 1, Visible = true },
    };

    private static TerminalRenderCellsPayload Delta(CellRect box, long baseGeneration, long generation, int row, string text) => new()
    {
        Kind = TerminalRenderCellsPayload.KindCells,
        PaneId = "p2",
        GridCols = box.Cols,
        GridRows = box.Rows,
        BaseGeneration = baseGeneration,
        Generation = generation,
        Rows = [new TerminalRenderCellRow { I = row, T = text }],
        Cursor = new TerminalRenderCursorPayload { Col = text.Length, Row = row, Visible = true },
    };
}
