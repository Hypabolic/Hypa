using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests;

/// <summary>
/// The PTY reader opens and closes a DEC 2026 block in feed order. A flush
/// that read the sync mode before a close must not start the next block.
/// </summary>
public sealed class SynchronizedOutputBlocksTests
{
    private const string Pane = "pane_1";
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Late_flush_after_a_close_does_not_start_the_next_block()
    {
        var blocks = new SynchronizedOutputBlocks();
        blocks.NoteFeed(Pane, insideBlock: true, observed: true, T0);

        // The flush saw the block open. The reader closes it before the flush
        // asks for the start.
        blocks.NoteFeed(Pane, insideBlock: false, observed: true, T0.AddMilliseconds(5));
        Assert.Equal(T0.AddMilliseconds(6), blocks.StartOf(Pane, T0.AddMilliseconds(6)));

        // A new block opens two seconds later. Its start is its own feed time,
        // so a flush half a second into it is inside the timeout.
        var open = T0.AddSeconds(2);
        blocks.NoteFeed(Pane, insideBlock: true, observed: true, open);
        Assert.Equal(open, blocks.StartOf(Pane, open.AddMilliseconds(500)));
    }

    [Fact]
    public void Block_start_stays_at_the_first_feed_inside_the_block()
    {
        var blocks = new SynchronizedOutputBlocks();
        blocks.NoteFeed(Pane, insideBlock: true, observed: true, T0);
        blocks.NoteFeed(Pane, insideBlock: true, observed: true, T0.AddMilliseconds(400));

        Assert.Equal(T0, blocks.StartOf(Pane, T0.AddMilliseconds(900)));
    }

    [Fact]
    public void Block_with_no_observer_starts_at_the_first_held_paint()
    {
        var blocks = new SynchronizedOutputBlocks();

        // No observer when the block opens. An observer attaches later, and
        // the block never closes.
        blocks.NoteFeed(Pane, insideBlock: true, observed: false, T0);
        var first = T0.AddSeconds(3);
        Assert.Equal(first, blocks.StartOf(Pane, first));

        // The next held paint sees the same start, so the block times out.
        Assert.Equal(first, blocks.StartOf(Pane, first.AddSeconds(1)));
    }

    [Fact]
    public void Block_that_the_reader_did_not_record_still_times_out()
    {
        var blocks = new SynchronizedOutputBlocks();
        blocks.NoteFeed(Pane, insideBlock: true, observed: true, T0);
        blocks.NoteFeed(Pane, insideBlock: false, observed: true, T0.AddMilliseconds(5));

        // A block opens on a path that gives the reader no feed. Each held
        // paint sees the start of the first one.
        var first = T0.AddSeconds(1);
        Assert.Equal(first, blocks.StartOf(Pane, first));
        Assert.Equal(first, blocks.StartOf(Pane, first.AddMilliseconds(999)));
        Assert.Equal(first, blocks.StartOf(Pane, first.AddSeconds(1)));
    }

    [Fact]
    public void Forget_drops_the_block()
    {
        var blocks = new SynchronizedOutputBlocks();
        blocks.NoteFeed(Pane, insideBlock: true, observed: true, T0);
        blocks.Forget(Pane);

        var later = T0.AddSeconds(5);
        Assert.Equal(later, blocks.StartOf(Pane, later));
    }
}
