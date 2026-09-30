using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.Terminal;
using Hypa.Terminal.Vt;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
// / for one pane.
/// <c>content_write_lock</c> for one pane. Hypa matches with one
/// <c>_vtIoGate</c> for each pane.
/// </summary>
public sealed class PaneFeedIsolationTests
{
    [Fact]
    public async Task A_feed_on_one_pane_does_not_block_a_feed_on_another_pane()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        await using var slow = NewPane(new HoldingVtEngine(8, 4, entered, release));
        await using var fast = NewPane(TestVtEngines.Stub(8, 4));

        var slowFeed = Task.Run(() => slow.FeedVtForTests("slow"u8.ToArray()));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        var fastFeed = Task.Run(() => fast.FeedVtForTests("fast"u8.ToArray()));
        await fastFeed.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(slowFeed.IsCompleted);

        release.Set();
        await slowFeed.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, fast.FeedGeneration);
        Assert.Equal(1, slow.FeedGeneration);
    }

    [Fact]
    public async Task A_slow_pane_does_not_stall_the_paint_of_a_fast_pane()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        await using var slow = NewPane(new HoldingVtEngine(8, 4, entered, release));
        await using var fast = NewPane(TestVtEngines.Stub(8, 4));

        var slowFeed = Task.Run(() => slow.FeedVtForTests("slow"u8.ToArray()));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        var fastFeed = Task.Run(() => fast.FeedVtForTests("fast"u8.ToArray()));
        await fastFeed.WaitAsync(TimeSpan.FromSeconds(2));

        var decision = fast.LastFeedPaintDecision;
        Assert.True(decision.RequestPaint);
        Assert.Equal(1, decision.FeedGeneration);
        Assert.Equal(fast.FeedGeneration, decision.FeedGeneration);
        Assert.False(slowFeed.IsCompleted);

        release.Set();
        await slowFeed.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static PaneRuntime NewPane(IVtEngine vt) =>
        new(
            new PaneSpawnOptions
            {
                Id = PaneId.New(),
                Cwd = Path.GetTempPath(),
                Command = "/bin/true",
                Args = [],
                Cols = 8,
                Rows = 4,
            },
            spawnProcess: static (_, _) => throw new InvalidOperationException("not started"),
            vt: vt);

    private sealed class HoldingVtEngine : IVtEngine
    {
        private readonly StubVtEngine _inner;
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _release;

        public HoldingVtEngine(
            int cols,
            int rows,
            ManualResetEventSlim entered,
            ManualResetEventSlim release)
        {
            _inner = new StubVtEngine(cols, rows);
            _entered = entered;
            _release = release;
        }

        public int Cols => _inner.Cols;
        public int Rows => _inner.Rows;
        public int CursorCol => _inner.CursorCol;
        public int CursorRow => _inner.CursorRow;
        public bool IsSynchronizedOutputActive => _inner.IsSynchronizedOutputActive;

        public void Resize(int cols, int rows) => _inner.Resize(cols, rows);

        public void Feed(ReadOnlySpan<byte> data)
        {
            _entered.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("holding VT was not released");
            _inner.Feed(data);
        }

        public void Feed(ReadOnlySpan<char> text) => Feed(System.Text.Encoding.UTF8.GetBytes(text.ToString()));

        public int TakePendingBellCount() => _inner.TakePendingBellCount();

        public string GetVisibleText(bool trimTrailingWhitespace = true) =>
            _inner.GetVisibleText(trimTrailingWhitespace);

        public string GetRecentText(int maxLines, bool trimTrailingWhitespace = true) =>
            _inner.GetRecentText(maxLines, trimTrailingWhitespace);

        public string GetRecentUnwrappedText(int maxLines, bool trimTrailingWhitespace = true) =>
            _inner.GetRecentUnwrappedText(maxLines, trimTrailingWhitespace);

        public VtStructuredSnapshot CaptureSnapshot() => _inner.CaptureSnapshot();

        public void Reset() => _inner.Reset();

        public void Dispose() => _inner.Dispose();
    }
}
