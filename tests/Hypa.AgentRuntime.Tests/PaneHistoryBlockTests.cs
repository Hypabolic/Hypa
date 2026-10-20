using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.History;
using Hypa.Terminal;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// History rows fill a block before the store seals it, and each row keeps
/// the frame tables it was stamped with.
/// </summary>
[Trait("Category", "Ghostty")]
[Collection("GhosttyPtyTests")]
public sealed class PaneHistoryBlockTests
{
    private static readonly string? LibPath = GhosttyTestRequire.TryResolveNativeLibraryPath();

    [SkippableFact]
    public async Task Many_small_drains_of_plain_output_fill_whole_blocks()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        const int cols = 40;
        const int rows = 8;
        var codec = new CountingCodec();
        var store = new InMemoryPaneHistoryStore(codec, 1_000_000, 2_000_000_000, blockRows: 256);
        using var vt = new GhosttyVtEngine(cols, rows, maxScrollback: 1, libraryPathOverride: LibPath);
        await using var pane = NewPane(vt, cols, rows, store);

        // One line for each feed, as a slow PTY reader gives.
        for (var i = 0; i < 600; i++)
            pane.FeedVtForTests($"line {i:D4}\r\n");

        Assert.True(store.RowCount >= 590, $"history rows {store.RowCount}");
        Assert.True(codec.Compressions <= store.RowCount / 256, $"{codec.Compressions} blocks for {store.RowCount} rows");
        var read = store.TryReadRows(store.FirstRowIndex + 300, 1);
        Assert.True(read.IsOk);
        var row = read.Value.Rows[0];
        var text = string.Concat(row.Cells.Take(9).Select(c => row.Tables.Resolve(in c).Text));
        Assert.Equal($"line {300 + store.FirstRowIndex:D4}", text);
    }

    [SkippableFact]
    public async Task A_large_drain_keeps_a_bounded_set_of_row_buffers()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        const int cols = 40;
        const int rows = 8;
        var store = new InMemoryPaneHistoryStore(new BrotliPaneHistoryCodec(), 1_000_000, 2_000_000_000, blockRows: 256);
        // A large scrollback keeps every line until the one drain copies it.
        using var vt = new GhosttyVtEngine(cols, rows, maxScrollback: 64_000_000, libraryPathOverride: LibPath);
        await using var pane = NewPane(vt, cols, rows, store);

        var text = new System.Text.StringBuilder();
        for (var i = 0; i < 1500; i++)
            text.Append("line ").Append(i.ToString("D4")).Append("\r\n");
        pane.FeedVtForTests(text.ToString());

        Assert.True(store.RowCount > PaneRuntime.MaxKeptHistoryRowBuffers, $"history rows {store.RowCount}");
        Assert.Equal(PaneRuntime.MaxKeptHistoryRowBuffers, pane.HistoryRowBufferCount);
    }

    [Fact]
    public void Rows_after_a_seal_in_one_append_keep_their_tables()
    {
        const int cols = 3;
        var styled = new VtCellView("日", 2, false, "#112233", null, true, false, false, false, false, false, false);
        var stamp = new VtStampTables();
        var packed = styled.Pack(stamp);
        var tables = stamp.Freeze(null);
        var rows = Enumerable.Range(0, 10).Select(_ => new[] { packed, VtFrameCell.Blank, VtFrameCell.Blank }).ToArray();
        using var store = new InMemoryPaneHistoryStore(new BrotliPaneHistoryCodec(), 1_000, 1_000_000, blockRows: 4);

        store.AppendRows(rows, cols, tables);

        var read = store.TryReadRows(0, 10);
        Assert.True(read.IsOk);
        Assert.Equal(10, read.Value.Rows.Count);
        foreach (var row in read.Value.Rows)
            Assert.True(styled.VisuallyEquals(row.Tables.Resolve(in row.Cells[0])), $"row {row.Index}");
    }

    private static PaneRuntime NewPane(GhosttyVtEngine vt, int cols, int rows, IPaneHistoryStore store) =>
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

    private sealed class CountingCodec : IPaneHistoryCodec
    {
        private readonly BrotliPaneHistoryCodec _inner = new();

        public int Compressions { get; private set; }

        public PaneHistoryResult<byte[]> Compress(ReadOnlySpan<VtFrameCell> cells)
        {
            Compressions++;
            return _inner.Compress(cells);
        }

        public PaneHistoryResult<VtFrameCell[]> Decompress(ReadOnlySpan<byte> compressed, int cellCount) =>
            _inner.Decompress(compressed, cellCount);
    }
}
