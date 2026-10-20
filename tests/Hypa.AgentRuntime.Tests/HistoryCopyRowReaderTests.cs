using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.History;
using Hypa.Terminal;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;
using Xunit.Abstractions;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// The history copy reads each row with one page lookup. These tests compare
/// it with the per-cell copy, which looks up the page for each cell.
/// </summary>
[Trait("Category", "Ghostty")]
[Collection("GhosttyPtyTests")]
public sealed class HistoryCopyRowReaderTests(ITestOutputHelper output)
{
    private static readonly string? LibPath = GhosttyTestRequire.TryResolveNativeLibraryPath();

    [SkippableFact]
    public async Task Per_row_history_copy_matches_the_per_cell_copy()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        const int cols = 40;
        const int rows = 8;
        using var rowVt = new GhosttyVtEngine(cols, rows, maxScrollback: 1, libraryPathOverride: LibPath);
        using var cellVt = new GhosttyVtEngine(cols, rows, maxScrollback: 1, libraryPathOverride: LibPath);
        cellVt.UsePerCellHistoryCopy = true;
        var rowStore = NewStore();
        var cellStore = NewStore();
        await using var rowPane = NewPane(rowVt, cols, rows, rowStore);
        await using var cellPane = NewPane(cellVt, cols, rows, cellStore);

        // A 1-byte scrollback limit still keeps the newest Ghostty page
        // (about 1,800 rows at this width), so Ghostty prunes old pages
        // and the store holds rows that Ghostty no longer has. Feed in
        // batches smaller than that page, as PTY reads do, so each drain
        // copies every row before Ghostty prunes it.
        for (var batch = 0; batch < 12; batch++)
        {
            var text = MixedContent(batch, 400);
            rowPane.FeedVtForTests(text);
            cellPane.FeedVtForTests(text);
        }

        Assert.Equal(0, rowPane.HistoryGapCount);
        Assert.Equal(0, cellPane.HistoryGapCount);
        Assert.True(rowStore.RowCount > 2000, $"history rows {rowStore.RowCount}");
        Assert.Equal(cellStore.RowCount, rowStore.RowCount);
        Assert.Equal(cellStore.FirstRowIndex, rowStore.FirstRowIndex);

        var compared = 0;
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = rowStore.FirstRowIndex; index < rowStore.NextRowIndex; index += 256)
        {
            var count = (int)Math.Min(256, rowStore.NextRowIndex - index);
            var got = rowStore.TryReadRows(index, count);
            var want = cellStore.TryReadRows(index, count);
            Assert.True(got.IsOk);
            Assert.True(want.IsOk);
            for (var i = 0; i < count; i++)
            {
                var gotRow = got.Value.Rows[i];
                var wantRow = want.Value.Rows[i];
                Assert.Equal(wantRow.Cols, gotRow.Cols);
                for (var c = 0; c < wantRow.Cols; c++)
                {
                    var g = gotRow.Tables.Resolve(in gotRow.Cells[c]);
                    var w = wantRow.Tables.Resolve(in wantRow.Cells[c]);
                    Assert.True(
                        w.VisuallyEquals(g),
                        $"row {index + i} col {c}: per-cell '{w.Text}' w{w.Width} fg {w.Fg} mod {w.Modifier}, "
                        + $"per-row '{g.Text}' w{g.Width} fg {g.Fg} mod {g.Modifier}");
                    Classify(w, kinds);
                }

                compared++;
            }
        }

        output.WriteLine($"compared {compared} rows; cell kinds: {string.Join(", ", kinds.Order())}");
        foreach (var kind in new[] { "wide", "combining", "emoji", "joined-emoji", "colour", "bold", "underline", "blank" })
            Assert.Contains(kind, kinds);
    }

    [SkippableFact]
    public void Rows_at_the_top_and_the_bottom_of_the_Ghostty_window_are_correct()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        const int cols = 30;
        const int rows = 6;
        using var vt = new GhosttyVtEngine(cols, rows, libraryPathOverride: LibPath);
        vt.Feed("\u001b[1mfirst row 漢\u001b[0m\r\n"u8);
        for (var i = 0; i < 40; i++)
            vt.Feed(Encoding.UTF8.GetBytes($"middle {i}\r\n"));
        vt.Feed("\u001b[4mlast row é\u001b[0m"u8);
        Assert.True(vt.TryGetScrollbackExtent(out var total, out _));

        foreach (var screenRow in new[] { 0, total - 1 })
        {
            var perRow = new List<VtFrameCell[]>();
            var perCell = new List<VtFrameCell[]>();
            var rowTables = new VtStampTables();
            var cellTables = new VtStampTables();
            Assert.True(vt.TryStampScreenRows(screenRow, 1, rowTables, perRow, out _));
            vt.UsePerCellHistoryCopy = true;
            Assert.True(vt.TryStampScreenRows(screenRow, 1, cellTables, perCell, out _));
            vt.UsePerCellHistoryCopy = false;
            var rowFrozen = rowTables.Freeze(null);
            var cellFrozen = cellTables.Freeze(null);
            for (var c = 0; c < cols; c++)
                Assert.True(cellFrozen.Resolve(in perCell[0][c]).VisuallyEquals(rowFrozen.Resolve(in perRow[0][c])), $"row {screenRow} col {c}");

            var text = RowText(perRow[0], rowFrozen);
            if (screenRow == 0)
            {
                Assert.Equal("first row 漢", text);
                Assert.True(rowFrozen.Resolve(in perRow[0][0]).Bold);
            }
            else
            {
                Assert.Equal("last row é", text);
                Assert.True(rowFrozen.Resolve(in perRow[0][0]).Underline);
            }
        }
    }

    [SkippableFact]
    public void Failed_row_lookup_copies_no_row_of_the_batch()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        const int cols = 20;
        const int rows = 4;
        using var vt = new GhosttyVtEngine(cols, rows, libraryPathOverride: LibPath);
        for (var i = 0; i < 12; i++)
            vt.Feed(Encoding.UTF8.GetBytes($"line {i}\r\n"));

        var batch = new List<VtFrameCell[]>();
        vt.ForceHistoryRowFailure = 2;
        Assert.False(vt.TryStampScreenRows(0, 5, new VtStampTables(), batch, out var failed));
        Assert.Equal(0, failed);

        vt.ForceHistoryRowFailure = -1;
        var tables = new VtStampTables();
        Assert.True(vt.TryStampScreenRows(0, 5, tables, batch, out var stamped));
        Assert.Equal(5, stamped);
        Assert.Equal("line 2", RowText(batch[2], tables.Freeze(null)));
    }

    [SkippableFact]
    public void History_copy_allocates_no_closure_for_each_cell()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        const int cols = 200;
        const int rows = 10;
        const int copyRows = 100;
        using var vt = new GhosttyVtEngine(cols, rows, libraryPathOverride: LibPath);
        var line = new string('x', cols - 20);
        for (var i = 0; i < 300; i++)
            vt.Feed(Encoding.ASCII.GetBytes($"{i:D6} \u001b[32m{line}\u001b[0m\r\n"));

        var perRow = AllocatedBytesPerCell(vt, copyRows, cols, perCell: false);
        var perCell = AllocatedBytesPerCell(vt, copyRows, cols, perCell: true);
        output.WriteLine($"allocated bytes for each cell: per-row {perRow:0.0}, per-cell {perCell:0.0}");

        // A packed cell is 8 bytes in the row array. A closure for each cell
        // adds at least its object header.
        Assert.True(perRow < 9, $"per-row copy allocates {perRow:0.0} bytes for each cell");
        Assert.True(perCell > perRow + 16, $"per-cell copy allocates {perCell:0.0} bytes for each cell");
    }

    [SkippableFact]
    public void A_second_copy_into_the_same_rows_makes_no_row_arrays()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        const int cols = 80;
        const int rows = 4;
        const int copyRows = 200;
        using var vt = new GhosttyVtEngine(cols, rows, libraryPathOverride: LibPath);
        for (var i = 0; i < copyRows + rows; i++)
            vt.Feed(Encoding.UTF8.GetBytes($"line {i}\r\n"));
        var tables = new VtStampTables();
        var buffers = new List<VtFrameCell[]>();
        Assert.True(vt.TryStampScreenRows(0, copyRows, tables, buffers, out _));
        var first = buffers.ToArray();

        tables.BeginFrame();
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(vt.TryStampScreenRows(0, copyRows, tables, buffers, out var stamped));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(copyRows, stamped);
        Assert.Equal(first, buffers.Take(copyRows));
        // One row array is 640 bytes. The copy may allocate table entries, not rows.
        Assert.True(allocated < cols * 8 * 10, $"allocated {allocated} bytes for {copyRows} rows");
        Assert.Equal("line 3", RowText(buffers[3], tables.Freeze(null)));
    }

    private double AllocatedBytesPerCell(GhosttyVtEngine vt, int copyRows, int cols, bool perCell)
    {
        vt.UsePerCellHistoryCopy = perCell;
        var tables = new VtStampTables();
        var warm = new List<VtFrameCell[]>(copyRows);
        Assert.True(vt.TryStampScreenRows(0, copyRows, tables, warm, out _));
        var dest = new List<VtFrameCell[]>(copyRows);
        tables.BeginFrame();
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(vt.TryStampScreenRows(0, copyRows, tables, dest, out var stamped));
        var after = GC.GetAllocatedBytesForCurrentThread();
        vt.UsePerCellHistoryCopy = false;
        Assert.Equal(copyRows, stamped);
        return (after - before) / (double)(copyRows * cols);
    }

    private static string MixedContent(int batch, int count)
    {
        var text = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            var n = (batch * count) + i;
            switch (n % 8)
            {
                case 0:
                    text.Append("漢字かな ").Append(n).Append(" 交じり");
                    break;
                case 1:
                    text.Append("café ẍ n").Append(n);
                    break;
                case 2:
                    text.Append("👍🏽 👨‍👩‍👧 🇯🇵 ").Append(n);
                    break;
                case 3:
                    text.Append("\u001b[31mred\u001b[0m \u001b[1mbold\u001b[0m \u001b[4munder\u001b[0m ")
                        .Append("\u001b[38;2;10;200;30mrgb\u001b[0m \u001b[48;5;21mbg\u001b[0m ").Append(n);
                    break;
                case 4:
                    // A blank row.
                    break;
                case 5:
                    text.Append("trailing blanks ").Append(n).Append("      ");
                    break;
                case 6:
                    text.Append(new string('w', 39)).Append('漢');
                    break;
                default:
                    text.Append("\u001b[7minverse\u001b[0m \u001b[9mstrike\u001b[0m ").Append(n);
                    break;
            }

            text.Append("\r\n");
        }

        return text.ToString();
    }

    private static void Classify(VtCellView cell, HashSet<string> kinds)
    {
        if (cell.Width == 2)
            kinds.Add("wide");
        if (cell.Text.Length > 1 && cell.Text.Contains('́'))
            kinds.Add("combining");
        if (cell.Text.StartsWith("👍", StringComparison.Ordinal))
            kinds.Add("emoji");
        if (cell.Text.Contains('‍'))
            kinds.Add("joined-emoji");
        if (cell.FgPacked != 0)
            kinds.Add("colour");
        if (cell.Bold)
            kinds.Add("bold");
        if (cell.Underline)
            kinds.Add("underline");
        if (cell.Text == " " && cell.FgPacked == 0 && cell.BgPacked == 0 && cell.Modifier == 0)
            kinds.Add("blank");
    }

    private static string RowText(VtFrameCell[] row, VtFrameTables tables)
    {
        var text = new StringBuilder();
        foreach (var cell in row)
        {
            var view = tables.Resolve(in cell);
            if (!view.IsContinuation)
                text.Append(view.Text);
        }

        return text.ToString().TrimEnd();
    }

    internal static InMemoryPaneHistoryStore NewStore() =>
        new(new BrotliPaneHistoryCodec(), 1_000_000, 2_000_000_000, 256);

    internal static PaneRuntime NewPane(GhosttyVtEngine vt, int cols, int rows, IPaneHistoryStore store) =>
        new(
            new PaneSpawnOptions
            {
                Id = PaneId.New(),
                Cwd = Path.GetTempPath(),
                Command = "/bin/true",
                Args = [],
                Cols = cols,
                Rows = rows,
            },
            spawnProcess: static (_, _) => throw new InvalidOperationException("not started"),
            vt: vt,
            historyStore: store,
            nativeWindowBytes: 1,
            scrollbackLimitBytes: 2_000_000_000);
}
