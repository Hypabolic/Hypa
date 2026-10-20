using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests;

/// <summary>
/// The render coalescer spends its render tick and its echo budget only on a
/// paint that posts. Each key opens or extends the echo window and gets its
/// own echo budget. Inside the window a request is immediate while the budget
/// lasts. Output without input stays at one paint per render tick.
/// </summary>
public sealed class PaneRenderCoalescerTests
{
    private const string Pane = "pane_1";

    [Fact]
    public void Second_echo_chunk_inside_the_echo_window_paints_at_once()
    {
        using var rig = new Rig();
        Assert.True(rig.Feed(1));

        rig.Coalescer.MarkInputAdmitted(Pane);
        rig.Advance(1);
        Assert.True(rig.Feed(2));
        rig.Advance(2);
        Assert.True(rig.Feed(3));
    }

    [Fact]
    public async Task A_pane_with_an_open_echo_window_flushes_first_in_a_timer_batch()
    {
        using var rig = new Rig();
        const string bulk = "pane_bulk";
        Assert.True(rig.Feed(bulk, 1));
        Assert.True(rig.Feed(Pane, 1));

        // Both panes get output inside the render tick, so both wait for the
        // timer. The bulk pane asked first. Then a key opens the echo window
        // of the typing pane.
        rig.Advance(1);
        Assert.False(rig.Feed(bulk, 2));
        Assert.False(rig.Feed(Pane, 2));
        var before = rig.Flushes.Count;
        rig.Coalescer.MarkInputAdmitted(Pane);

        rig.Advance((int)PaneRenderCoalescer.RenderTick.TotalMilliseconds);
        await rig.Coalescer.LastTickFlushForTests;

        var batch = rig.Flushes.Skip(before).Select(f => f.PaneId).ToList();
        Assert.Equal([Pane, bulk], batch);
    }

    [Fact]
    public void Nothing_to_post_flush_keeps_the_window_open()
    {
        using var rig = new Rig();
        Assert.True(rig.Feed(1));

        rig.Coalescer.MarkInputAdmitted(Pane);
        rig.Outcome = PaintOutcome.NothingToPost;
        Assert.True(rig.Feed(2));
        rig.Outcome = PaintOutcome.Posted;
        Assert.True(rig.Feed(3));
    }

    [Fact]
    public void Skipped_flush_inside_the_echo_window_keeps_it_open()
    {
        using var rig = new Rig();
        Assert.True(rig.Feed(1));
        rig.Coalescer.MarkInputAdmitted(Pane);
        rig.Outcome = PaintOutcome.Skipped;
        Assert.True(rig.Feed(2));
        rig.Outcome = PaintOutcome.Posted;
        Assert.True(rig.Feed(3));
    }

    [Fact]
    public void Failed_force_full_retry_waits_for_the_render_tick()
    {
        using var rig = new Rig();
        // The flush fails and queues a retry origin paint, as the service
        // does when a capture fails.
        rig.OnForce = () =>
        {
            rig.Coalescer.RequestOriginPaint(Pane);
            return PaintOutcome.Skipped;
        };
        rig.Coalescer.RequestOriginPaint(Pane);
        rig.Advance(0);
        Assert.Single(rig.Flushes);
        rig.Advance(15);
        Assert.Single(rig.Flushes);
        rig.Advance(1);
        Assert.Equal(2, rig.Flushes.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Output_without_input_stays_rate_capped_at_one_tick(bool flushPosts)
    {
        using var rig = new Rig { Outcome = flushPosts ? PaintOutcome.Posted : PaintOutcome.NothingToPost };
        // One feed for each millisecond for 160 ms: ten render ticks.
        for (var ms = 0; ms < 160; ms++)
        {
            rig.Feed(ms + 1);
            rig.Advance(1);
        }

        rig.Advance(16);
        Assert.InRange(rig.Flushes.Count, 10, 11);
        for (var i = 1; i < rig.Flushes.Count; i++)
        {
            Assert.True(
                rig.Flushes[i].At - rig.Flushes[i - 1].At >= PaneRenderCoalescer.RenderTick,
                $"flush {i} at {rig.Flushes[i].At:O} follows {rig.Flushes[i - 1].At:O}");
        }
    }

    [Fact]
    public void Echo_window_ends_at_the_key_time_plus_the_window()
    {
        var window = (int)PaneRenderCoalescer.EchoWindow.TotalMilliseconds;
        using var rig = new Rig();
        Assert.True(rig.Feed(1));
        rig.Coalescer.MarkInputAdmitted(Pane);
        // A post inside the window does not move its end.
        rig.Advance(window - 20);
        Assert.True(rig.Feed(2));

        // 1 ms before the end: immediate.
        rig.Advance(19);
        Assert.True(rig.Feed(3));

        // 1 ms after the end: the request waits for the render tick.
        rig.Advance(2);
        Assert.False(rig.Feed(4));
        var flushes = rig.Flushes.Count;
        rig.Advance(14);
        Assert.Equal(flushes + 1, rig.Flushes.Count);
        Assert.Equal(4, rig.Flushes[^1].FeedGeneration);
    }

    [Fact]
    public void Write_completion_does_not_open_or_move_the_window()
    {
        var window = (int)PaneRenderCoalescer.EchoWindow.TotalMilliseconds;
        using var rig = new Rig();
        Assert.True(rig.Feed(1));

        // A write signal with no admitted key opens no window.
        rig.Coalescer.MarkInputWritten(Pane);
        rig.Advance(1);
        Assert.False(rig.Feed(2));
        rig.Advance(15);
        Assert.Equal(2, rig.Flushes.Count);

        // A write that completes late does not move the end of the window.
        rig.Coalescer.MarkInputAdmitted(Pane);
        rig.Advance(window - 2);
        Assert.True(rig.Feed(3));
        rig.Coalescer.MarkInputWritten(Pane);
        rig.Advance(3);
        Assert.False(rig.Feed(4));
    }

    [Fact]
    public void Write_completion_runs_a_pending_paint_that_the_window_makes_due()
    {
        using var rig = new Rig();
        Assert.True(rig.Feed(1));
        Assert.False(rig.Feed(2));
        rig.Coalescer.MarkInputAdmitted(Pane);
        rig.Coalescer.MarkInputWritten(Pane);
        rig.Advance(0);
        Assert.Equal(2, rig.Flushes.Count);
    }

    [Fact]
    public void Tick_flush_of_many_panes_obeys_the_server_ceiling()
    {
        using var rig = new Rig();
        var panes = Enumerable.Range(0, PaneRenderCoalescer.ServerEchoCeiling + 4)
            .Select(i => $"pane_{i + 10}")
            .ToArray();
        foreach (var pane in panes)
            Assert.True(rig.Feed(pane, 1));
        rig.Advance(2);
        foreach (var pane in panes)
        {
            Assert.False(rig.Feed(pane, 2));
            rig.Coalescer.MarkInputAdmitted(pane);
        }

        // The admissions armed the timer. One tick flushes the panes that
        // the echo windows make due, up to the ceiling.
        var before = rig.Flushes.Count;
        rig.Advance(0);
        Assert.Equal(PaneRenderCoalescer.ServerEchoCeiling, rig.Flushes.Count - before);
    }

    [Fact]
    public void Later_key_moves_the_end_of_the_window()
    {
        var window = (int)PaneRenderCoalescer.EchoWindow.TotalMilliseconds;
        using var rig = new Rig();
        Assert.True(rig.Feed(1));
        rig.Coalescer.MarkInputAdmitted(Pane);
        rig.Advance(5);
        rig.Coalescer.MarkInputAdmitted(Pane);

        // The first key would have closed the window here.
        rig.Advance(window - 4);
        Assert.True(rig.Feed(2));

        // The second key ends it at its own time plus the window.
        rig.Advance(4);
        Assert.False(rig.Feed(3));
    }

    [Fact]
    public void Echo_window_caps_immediate_flushes_that_post()
    {
        var window = (int)PaneRenderCoalescer.EchoWindow.TotalMilliseconds;
        using var rig = new Rig();
        rig.Coalescer.MarkInputAdmitted(Pane);
        for (var i = 1; i <= PaneRenderCoalescer.EchoFlushCap; i++)
            Assert.True(rig.Feed(i), $"echo flush {i}");
        Assert.False(rig.Feed(PaneRenderCoalescer.EchoFlushCap + 1));

        // With no new key, the held request waits for the render tick.
        rig.Advance(15);
        Assert.Equal(PaneRenderCoalescer.EchoFlushCap, rig.Flushes.Count);
        rig.Advance(1);
        Assert.Equal(PaneRenderCoalescer.EchoFlushCap + 1, rig.Flushes.Count);

        // Input after the window closed opens a new window with a new budget.
        rig.Advance(window);
        rig.Coalescer.MarkInputAdmitted(Pane);
        Assert.True(rig.Feed(PaneRenderCoalescer.EchoFlushCap + 2));
    }

    [Fact]
    public void Each_key_inside_an_open_window_gets_its_own_echo_budget()
    {
        using var rig = new Rig();
        rig.Coalescer.MarkInputAdmitted(Pane);
        for (var i = 1; i <= PaneRenderCoalescer.EchoFlushCap; i++)
            Assert.True(rig.Feed(i), $"first key flush {i}");
        Assert.False(rig.Feed(PaneRenderCoalescer.EchoFlushCap + 1));

        // A second key inside the open window gives a new budget. The held
        // request is now due and the timer runs it at once.
        rig.Advance(3);
        rig.Coalescer.MarkInputAdmitted(Pane);
        var before = rig.Flushes.Count;
        rig.Advance(0);
        Assert.Equal(before + 1, rig.Flushes.Count);
        for (var i = 1; i < PaneRenderCoalescer.EchoFlushCap; i++)
            Assert.True(rig.Feed(100 + i), $"second key flush {i}");

        // The second key's budget is spent. The next request waits for the
        // render tick after the last post.
        Assert.False(rig.Feed(200));
        before = rig.Flushes.Count;
        rig.Advance(15);
        Assert.Equal(before, rig.Flushes.Count);
        rig.Advance(1);
        Assert.Equal(before + 1, rig.Flushes.Count);
    }

    [Fact]
    public void Keys_in_a_fast_burst_each_paint_a_split_echo_at_once()
    {
        // Keys 10 ms apart keep one echo window open for the whole burst.
        // Each key's echo comes in two reads (the character, then the line
        // redisplay), and each read posts.
        using var rig = new Rig();
        Assert.True(rig.Feed(1));
        long generation = 1;
        for (var key = 0; key < 12; key++)
        {
            rig.Advance(10);
            rig.Coalescer.MarkInputAdmitted(Pane);
            rig.Advance(1);
            Assert.True(rig.Feed(++generation), $"key {key} echo");
            rig.Advance(1);
            Assert.True(rig.Feed(++generation), $"key {key} redisplay");
        }
    }

    [Fact]
    public void Fast_keys_into_a_pane_with_bulk_output_paint_at_most_the_cap_for_each_key()
    {
        using var rig = new Rig();
        Assert.True(rig.Feed(1));
        long generation = 1;
        for (var key = 0; key < 4; key++)
        {
            rig.Advance(10);
            rig.Coalescer.MarkInputAdmitted(Pane);
            var immediate = 0;
            for (var read = 0; read < 10; read++)
            {
                if (rig.Feed(++generation))
                    immediate++;
            }

            Assert.Equal(PaneRenderCoalescer.EchoFlushCap, immediate);
        }
    }

    [Fact]
    public void Flush_dispatched_for_one_key_does_not_spend_the_budget_of_the_next_key()
    {
        using var rig = new Rig();
        rig.Coalescer.MarkInputAdmitted(Pane);
        var first = rig.Coalescer.Request(Pane, 1);
        Assert.NotNull(first);

        // The next key comes while the echo flush of the first key runs.
        rig.Advance(2);
        rig.Coalescer.MarkInputAdmitted(Pane);
        rig.Coalescer.CompleteFlush(first.Value, PaintOutcome.Posted);

        for (var i = 0; i < PaneRenderCoalescer.EchoFlushCap; i++)
            Assert.True(rig.Feed(10 + i), $"second key flush {i}");
        Assert.False(rig.Feed(20));
    }

    [Fact]
    public void Colour_only_reads_then_a_split_prompt_all_paint_at_once()
    {
        using var rig = new Rig();
        rig.Coalescer.MarkInputAdmitted(Pane);
        rig.Outcome = PaintOutcome.NothingToPost;
        for (var i = 1; i <= 12; i++)
            Assert.True(rig.Feed(i), $"colour-only read {i}");

        rig.Outcome = PaintOutcome.Posted;
        for (var i = 13; i < 13 + PaneRenderCoalescer.EchoFlushCap; i++)
        {
            rig.Advance(1);
            Assert.True(rig.Feed(i), $"prompt piece {i}");
        }

        Assert.Equal(12 + PaneRenderCoalescer.EchoFlushCap, rig.Flushes.Count);
    }

    [Fact]
    public void Panes_together_get_no_more_early_flushes_in_one_tick_than_the_ceiling()
    {
        using var rig = new Rig { Outcome = PaintOutcome.NothingToPost };
        const string other = "pane_2";
        rig.Coalescer.MarkInputAdmitted(Pane);
        rig.Coalescer.MarkInputAdmitted(other);

        // The first flush of each pane is due on the normal rhythm, so it is
        // not early. Every flush after it in this tick is early.
        Assert.True(rig.Feed(Pane, 0));
        Assert.True(rig.Feed(other, 0));

        var immediate = 0;
        for (var i = 1; i <= PaneRenderCoalescer.ServerEchoCeiling; i++)
        {
            if (rig.Feed(Pane, i))
                immediate++;
            if (rig.Feed(other, i))
                immediate++;
        }

        Assert.Equal(PaneRenderCoalescer.ServerEchoCeiling, immediate);

        // The next render tick gives a new ceiling.
        rig.Advance(16);
        Assert.True(rig.Feed(Pane, 100));
    }

    [Fact]
    public void Immediate_flush_for_one_pane_does_not_flush_another_pane()
    {
        using var rig = new Rig();
        const string other = "pane_2";
        Assert.True(rig.Feed(other, 1));
        rig.Advance(2);
        // Bulk output on pane B waits for its render tick.
        Assert.False(rig.Feed(other, 2));

        rig.Coalescer.MarkInputAdmitted(Pane);
        Assert.True(rig.Feed(Pane, 1));

        Assert.DoesNotContain(rig.Flushes, f => f.PaneId == other && f.FeedGeneration == 2);
        rig.Advance(13);
        Assert.DoesNotContain(rig.Flushes, f => f.PaneId == other && f.FeedGeneration == 2);
        rig.Advance(1);
        Assert.Contains(rig.Flushes, f => f.PaneId == other && f.FeedGeneration == 2);
    }

    [Fact]
    public async Task Flush_started_before_the_input_does_not_spend_its_echo_budget()
    {
        using var rig = new Rig();
        Assert.True(rig.Feed(1));
        var release = new TaskCompletionSource<PaintOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Hold = release;
        Assert.False(rig.Feed(2));
        rig.Advance(16);
        rig.Hold = null;

        // Input comes while the tick flush waits for the pane emit gate.
        rig.Coalescer.MarkInputAdmitted(Pane);
        Assert.False(rig.Feed(3));
        release.SetResult(PaintOutcome.Posted);
        await rig.Coalescer.LastTickFlushForTests;
        var before = rig.Flushes.Count;

        // The echo read held behind that flush posts at once, then three more.
        rig.Advance(0);
        for (var i = 4; i < 4 + PaneRenderCoalescer.EchoFlushCap - 1; i++)
            Assert.True(rig.Feed(i), $"echo flush {i}");
        Assert.Equal(before + PaneRenderCoalescer.EchoFlushCap, rig.Flushes.Count);
        Assert.False(rig.Feed(10));
    }

    [Fact]
    public void Nothing_to_post_flush_does_not_spend_the_echo_budget()
    {
        using var rig = new Rig { Outcome = PaintOutcome.NothingToPost };
        rig.Coalescer.MarkInputAdmitted(Pane);
        for (var i = 1; i <= PaneRenderCoalescer.EchoFlushCap * 2; i++)
            Assert.True(rig.Feed(i), $"echo flush {i}");
    }

    [Fact]
    public void Pending_force_full_flushes_promptly_after_keyboard_input()
    {
        using var rig = new Rig();
        Assert.True(rig.Feed(1));
        rig.Coalescer.RequestOriginPaint(Pane);
        Assert.False(rig.Feed(2));
        rig.Advance(1);
        Assert.Single(rig.Flushes);

        rig.Coalescer.MarkInputAdmitted(Pane);
        rig.Advance(0);
        Assert.Equal(2, rig.Flushes.Count);
        Assert.True(rig.Flushes[^1].Force);
        Assert.Equal(2, rig.Flushes[^1].FeedGeneration);
    }

    [Fact]
    public void Echo_request_does_not_turn_a_pending_force_full_into_a_live_flush()
    {
        using var rig = new Rig();
        Assert.True(rig.Feed(1));
        rig.Coalescer.MarkInputAdmitted(Pane);
        rig.Coalescer.RequestOriginPaint(Pane);
        Assert.False(rig.Feed(2));
        rig.Advance(0);
        var flush = Assert.Single(rig.Flushes, f => f.Force);
        Assert.Equal(2, flush.FeedGeneration);
    }

    [Fact]
    public void Admission_signal_opens_the_echo_window_without_a_write_completion()
    {
        using var rig = new Rig();
        Assert.True(rig.Feed(1));
        rig.Coalescer.MarkInputAdmitted(Pane);
        rig.Advance(1);
        Assert.True(rig.Feed(2));
    }

    [Fact]
    public async Task Tick_does_not_start_a_second_flush_while_one_is_in_progress()
    {
        using var rig = new Rig();
        Assert.True(rig.Feed(1));
        var release = new TaskCompletionSource<PaintOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Hold = release;
        Assert.False(rig.Feed(2));
        rig.Advance(16);
        Assert.Equal(2, rig.Flushes.Count);

        // The tick flush is still in progress. Neither a request nor the next
        // tick starts another flush for the pane.
        rig.Hold = null;
        Assert.False(rig.Feed(3));
        rig.Advance(32);
        Assert.Equal(2, rig.Flushes.Count);

        release.SetResult(PaintOutcome.Posted);
        await rig.Coalescer.LastTickFlushForTests;
        rig.Advance(16);
        Assert.Equal(3, rig.Flushes.Count);
        Assert.Equal(3, rig.Flushes[^1].FeedGeneration);
    }

    [Fact]
    public async Task Held_paint_after_a_key_runs_once_the_flush_in_progress_ends()
    {
        using var rig = new Rig();
        Assert.True(rig.Feed(1));
        var release = new TaskCompletionSource<PaintOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Hold = release;
        Assert.False(rig.Feed(2));
        rig.Advance(16);
        Assert.Equal(2, rig.Flushes.Count);

        // A key arrives, and its write ends while the tick flush is still in
        // progress. The held paint does not run beside it.
        rig.Hold = null;
        rig.Coalescer.MarkInputAdmitted(Pane);
        rig.Coalescer.RequestHeld(Pane, 3);
        await rig.Coalescer.RunHeldAsync(Pane);
        Assert.Equal(2, rig.Flushes.Count);

        // The flush in progress posts nothing. The echo window makes the held
        // paint due at once, not one render tick later.
        release.SetResult(PaintOutcome.Skipped);
        await rig.Coalescer.LastTickFlushForTests;
        rig.Advance(1);
        Assert.Equal(3, rig.Flushes.Count);
        Assert.Equal(3, rig.Flushes[^1].FeedGeneration);
    }

    [Fact]
    public async Task Held_paint_with_the_echo_budget_spent_waits_for_the_render_tick()
    {
        using var rig = new Rig();
        rig.Coalescer.MarkInputAdmitted(Pane);
        for (var i = 1; i <= PaneRenderCoalescer.EchoFlushCap; i++)
            Assert.True(rig.Feed(i));

        // The budget is spent, so the next request waits for the tick flush.
        var release = new TaskCompletionSource<PaintOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Hold = release;
        Assert.False(rig.Feed(PaneRenderCoalescer.EchoFlushCap + 1));
        rig.Advance(16);
        var flushes = rig.Flushes.Count;

        // A write ends while that flush is in progress. The window is still
        // open, but its budget is spent.
        rig.Hold = null;
        rig.Coalescer.RequestHeld(Pane, 10);
        await rig.Coalescer.RunHeldAsync(Pane);
        release.SetResult(PaintOutcome.Posted);
        await rig.Coalescer.LastTickFlushForTests;

        // The held paint keeps the normal rhythm: one render tick after the
        // post of the flush that was in progress.
        rig.Advance(15);
        Assert.Equal(flushes, rig.Flushes.Count);
        rig.Advance(1);
        Assert.Equal(flushes + 1, rig.Flushes.Count);
        Assert.Equal(10, rig.Flushes[^1].FeedGeneration);
    }

    [Fact]
    public void Flush_that_the_rhythm_already_allows_uses_no_unit_of_the_ceiling()
    {
        using var rig = new Rig { Outcome = PaintOutcome.NothingToPost };
        rig.Coalescer.MarkInputAdmitted(Pane);
        Assert.True(rig.Feed(Pane, 1));

        // Many panes with no recent flush. Each flush is due on the normal
        // rhythm, so none of them is early.
        for (var i = 0; i < PaneRenderCoalescer.ServerEchoCeiling + 4; i++)
        {
            var pane = $"pane_{i + 10}";
            rig.Coalescer.MarkInputAdmitted(pane);
            Assert.True(rig.Feed(pane, 1));
        }

        // The ceiling is not spent, so the early flush of the first pane is
        // still immediate.
        Assert.True(rig.Feed(Pane, 2));
    }

    [Fact]
    public async Task Held_paint_post_spends_the_render_tick()
    {
        using var rig = new Rig();
        Assert.True(rig.Feed(1));
        rig.Advance(20);

        rig.Coalescer.RequestHeld(Pane, 2);
        await rig.Coalescer.RunHeldAsync(Pane);
        Assert.Equal(2, rig.Flushes.Count);

        // The held paint posted, so the next request waits for the tick.
        rig.Advance(1);
        Assert.False(rig.Feed(3));
    }

    [Fact]
    public async Task Failed_tick_flush_ends_the_flush_in_progress()
    {
        using var rig = new Rig();
        Assert.True(rig.Feed(1));
        rig.Fail = true;
        Assert.False(rig.Feed(2));
        rig.Advance(16);
        await rig.Coalescer.LastTickFlushForTests;
        Assert.Equal(2, rig.Flushes.Count);

        // The failed attempt waits one render tick, then the pane flushes again.
        rig.Fail = false;
        Assert.False(rig.Feed(3));
        rig.Advance(16);
        Assert.Equal(3, rig.Flushes.Count);
    }

    [Fact]
    public async Task Tick_flush_that_waited_for_the_gate_spaces_the_next_post_from_its_end()
    {
        using var rig = new Rig();
        Assert.True(rig.Feed(1));
        var release = new TaskCompletionSource<PaintOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Hold = release;
        Assert.False(rig.Feed(2));
        rig.Advance(16);
        rig.Hold = null;

        // The flush posts 10 ms after the tick that started it.
        rig.Advance(10);
        Assert.False(rig.Feed(3));
        release.SetResult(PaintOutcome.Posted);
        await rig.Coalescer.LastTickFlushForTests;
        rig.Advance(15);
        Assert.Equal(2, rig.Flushes.Count);
        rig.Advance(1);
        Assert.Equal(3, rig.Flushes.Count);
    }

    [Fact]
    public void Hold_flushes_at_its_deadline_and_a_new_request_ends_it()
    {
        using var rig = new Rig();
        Assert.True(rig.Feed(1));
        rig.Coalescer.RequestNotBefore(Pane, 2, rig.Time.GetUtcNow() + TimeSpan.FromMilliseconds(100));
        rig.Advance(99);
        Assert.Single(rig.Flushes);
        rig.Advance(1);
        Assert.Equal(2, rig.Flushes.Count);

        rig.Coalescer.RequestNotBefore(Pane, 3, rig.Time.GetUtcNow() + TimeSpan.FromSeconds(1));
        rig.Advance(16);
        Assert.Equal(2, rig.Flushes.Count);
        Assert.True(rig.Feed(4));
        Assert.Equal(4, rig.Flushes[^1].FeedGeneration);
    }

    [Fact]
    public async Task Flush_that_started_before_forget_does_not_complete_a_newer_flush()
    {
        using var rig = new Rig();
        Assert.True(rig.Feed(1));
        var first = new TaskCompletionSource<PaintOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Hold = first;
        Assert.False(rig.Feed(2));
        rig.Advance(16);
        var firstFlush = rig.Coalescer.LastTickFlushForTests;
        rig.Coalescer.Forget(Pane);

        // The pane id comes back (a popup key is reused) and a new flush starts.
        var second = new TaskCompletionSource<PaintOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Hold = second;
        rig.Coalescer.RequestOriginPaint(Pane);
        rig.Advance(0);
        Assert.Equal(3, rig.Flushes.Count);
        rig.Hold = null;

        // The old completion does not end the new flush.
        first.SetResult(PaintOutcome.Posted);
        await firstFlush;
        Assert.False(rig.Feed(3));

        second.SetResult(PaintOutcome.Posted);
        await rig.Coalescer.LastTickFlushForTests;
        rig.Advance(16);
        Assert.Equal(4, rig.Flushes.Count);
        Assert.Equal(3, rig.Flushes[^1].FeedGeneration);
    }

    [Fact]
    public void Forget_clears_the_echo_window()
    {
        using var rig = new Rig();
        Assert.True(rig.Feed(1));
        rig.Coalescer.MarkInputAdmitted(Pane);
        rig.Coalescer.Forget(Pane);
        Assert.True(rig.Feed(2));
        Assert.False(rig.Feed(3));
    }

    /// <summary>
    /// A coalescer on a manual clock. <see cref="Feed"/> acts as the output
    /// emit path: it requests a paint, and runs and completes an immediate
    /// flush. Timer flushes run through the coalescer's own callback.
    /// </summary>
    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Coalescer = new PaneRenderCoalescer(Time, FlushAsync);
        }

        public ManualTimeProvider Time { get; } = new();

        public PaneRenderCoalescer Coalescer { get; }

        public List<(long FeedGeneration, bool Force, DateTimeOffset At, string PaneId)> Flushes { get; } = [];

        public PaintOutcome Outcome { get; set; } = PaintOutcome.Posted;

        public TaskCompletionSource<PaintOutcome>? Hold { get; set; }

        public bool Fail { get; set; }

        public Func<PaintOutcome>? OnForce { get; set; }

        /// <summary>Returns true when the request flushed at once.</summary>
        public bool Feed(long feedGeneration) => Feed(Pane, feedGeneration);

        /// <summary>Returns true when the request for <paramref name="paneId"/> flushed at once.</summary>
        public bool Feed(string paneId, long feedGeneration)
        {
            var due = Coalescer.Request(paneId, feedGeneration);
            if (due is not { } decision)
                return false;
            Flushes.Add((decision.FeedGeneration, false, Time.GetUtcNow(), paneId));
            Coalescer.CompleteFlush(decision, Outcome);
            return true;
        }

        public void Advance(int ms) => Time.Advance(TimeSpan.FromMilliseconds(ms));

        public void Dispose() => Coalescer.Dispose();

        private Task<PaintOutcome> FlushAsync(string paneId, long feedGeneration, bool force)
        {
            Flushes.Add((feedGeneration, force, Time.GetUtcNow(), paneId));
            if (Fail)
                throw new InvalidOperationException("flush failed");
            if (force && OnForce is { } onForce)
                return Task.FromResult(onForce());
            return Hold?.Task ?? Task.FromResult(Outcome);
        }
    }
}
