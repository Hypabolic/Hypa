using System.Text;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;
using Xunit.Abstractions;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// The history copy benchmark. It runs only when asked, so it has no
/// Ghostty category: the Ghostty gate fails on a skip.
/// </summary>
[Collection("GhosttyPtyTests")]
public sealed class HistoryCopyBenchmarkTests(ITestOutputHelper output)
{
    private static readonly string? LibPath = GhosttyTestRequire.TryResolveNativeLibraryPath();

    /// <summary>
    /// The history copy benchmark. Set HYPA_HISTORY_COPY_BENCH=1 to run it.
    /// HYPA_HISTORY_COPY_BENCH_LINES sets the line count (default 100000).
    /// </summary>
    [SkippableFact]
    public async Task History_copy_benchmark_reports_the_drain_time()
    {
        Skip.IfNot(
            Environment.GetEnvironmentVariable("HYPA_HISTORY_COPY_BENCH") == "1",
            "Set HYPA_HISTORY_COPY_BENCH=1 to run the history copy benchmark.");
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        var lines = int.TryParse(Environment.GetEnvironmentVariable("HYPA_HISTORY_COPY_BENCH_LINES"), out var n) && n > 0
            ? n
            : 100_000;
        const int cols = 200;
        const int rows = 50;
        const int linesPerRead = 200;
        var body = new string('y', cols - 30);
        foreach (var perCell in new[] { true, false })
        {
            using var vt = new GhosttyVtEngine(cols, rows, maxScrollback: 1, libraryPathOverride: LibPath);
            vt.UsePerCellHistoryCopy = perCell;
            var store = HistoryCopyRowReaderTests.NewStore();
            await using var pane = HistoryCopyRowReaderTests.NewPane(vt, cols, rows, store);
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var text = new StringBuilder();
            for (var i = 0; i < lines; i += linesPerRead)
            {
                text.Clear();
                for (var j = 0; j < linesPerRead && i + j < lines; j++)
                    text.Append("\u001b[36m").Append((i + j).ToString("D8")).Append("\u001b[0m ").Append(body).Append("\r\n");
                pane.FeedVtForTests(text.ToString());
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            output.WriteLine(
                $"{(perCell ? "per-cell" : "per-row ")} copy: {lines} lines at {cols} cols, "
                + $"history copy under the VT lock {pane.HistoryDrainTimeForTests.TotalMilliseconds:0.0} ms, "
                + $"stored rows {store.RowCount}, gaps {pane.HistoryGapCount}, "
                + $"allocated {allocated / 1_048_576.0:0.0} MiB");

            // The copy saw nearly every line. A gap is a pin that Ghostty
            // pruned between two reads; the copy then starts again at the
            // oldest row that Ghostty still holds.
            Assert.True(store.RowCount > lines - 2500, $"stored rows {store.RowCount}");
        }
    }
}
