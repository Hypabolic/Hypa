using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Mux;

/// <summary>
/// The render credit of one connection, on a test clock. The link model
/// sends the admitted bytes at a fixed rate in order, and each ack comes one
/// RTT after its frame left the link.
/// </summary>
public sealed class RenderCreditTests
{
    private const int Frame = 4096;

    [Fact]
    public void A_spent_budget_holds_other_panes_and_the_focused_pane_keeps_its_own_budget()
    {
        var time = new ManualTimeProvider();
        using var credit = new RenderCredit(time, () => { }, Fixed(8192, focused: 2048));
        credit.NoteFocus("focus");

        credit.Charge("bulk", 1, 8192);
        Assert.False(credit.MayAdmit("bulk"));
        Assert.False(credit.MayAdmit("other"));
        Assert.True(credit.MayAdmit("focus"));

        credit.Charge("focus", 1, 2048);
        Assert.False(credit.MayAdmit("focus"));
        Assert.Equal(8192, credit.State.UnackedBytes);
        Assert.Equal(2048, credit.State.FocusedUnackedBytes);
    }

    [Fact]
    public void Input_to_a_pane_moves_its_frames_in_flight_to_the_focused_budget()
    {
        var time = new ManualTimeProvider();
        using var credit = new RenderCredit(time, () => { }, Fixed(8192, focused: 4096));
        credit.NoteFocus("old");
        credit.Charge("old", 1, 1000);
        credit.Charge("new", 1, 3000);
        Assert.Equal(3000, credit.State.UnackedBytes);
        Assert.Equal(1000, credit.State.FocusedUnackedBytes);

        Assert.True(credit.NoteFocus("new"));
        Assert.False(credit.NoteFocus("new"));
        Assert.Equal("new", credit.State.FocusedPaneId);
        Assert.Equal(1000, credit.State.UnackedBytes);
        Assert.Equal(3000, credit.State.FocusedUnackedBytes);
        Assert.True(credit.Ack([new RenderAck("new", 1)]));
        Assert.Equal(0, credit.State.FocusedUnackedBytes);
    }

    [Fact]
    public void A_charge_takes_the_room_at_once_and_a_refund_gives_it_back()
    {
        var time = new ManualTimeProvider();
        using var credit = new RenderCredit(time, () => { }, Fixed(4096));
        Assert.True(credit.TryCharge("a", 1, 3000));
        Assert.False(credit.TryCharge("b", 1, 3000));
        credit.Refund("a", 1, 3000);
        Assert.Equal(0, credit.State.UnackedBytes);
        Assert.True(credit.TryCharge("b", 1, 3000));
        // An empty budget takes one frame larger than the budget, alone.
        credit.Refund("b", 1, 3000);
        Assert.True(credit.TryCharge("a", 2, 10_000));
        Assert.False(credit.TryCharge("a", 3, 1));
    }

    [Fact]
    public void The_slices_of_one_snapshot_go_while_they_fit_and_the_rest_waits_for_an_ack()
    {
        var time = new ManualTimeProvider();
        using var credit = new RenderCredit(time, () => { }, Fixed(4096));
        RenderLine[] slices = [new(1500, 10), new(1500, 20), new(1500, 30), new(1500, 40)];

        Assert.Equal((2, 3000), credit.TryChargeLines("a", 7, slices));
        Assert.Equal(3000, credit.State.UnackedBytes);
        Assert.Equal(2, credit.State.InFlightFrames);
        // The third slice does not fit next to the first two.
        Assert.Equal((0, 0), credit.TryChargeLines("a", 7, slices[2..]));

        // The ack of the first slice gives back only that slice. The second
        // slice can still be on the link.
        Assert.True(credit.Ack([new RenderAck("a", 7, 10)]));
        Assert.Equal(1500, credit.State.UnackedBytes);
        Assert.Equal((1, 1500), credit.TryChargeLines("a", 7, slices[2..]));

        // An ack of the whole generation gives back every slice of it.
        Assert.True(credit.Ack([new RenderAck("a", 7)]));
        Assert.Equal(0, credit.State.UnackedBytes);

        // An empty budget takes one slice larger than the budget, alone.
        Assert.Equal((1, 9000), credit.TryChargeLines("a", 8, [new(9000, 5), new(10, 6)]));
        Assert.Equal((0, 0), credit.TryChargeLines("a", 8, [new(10, 6)]));
        credit.RefundLines("a", 8, [new(9000, 5)], 1);
        Assert.Equal(0, credit.State.UnackedBytes);
    }

    [Fact]
    public void An_ack_gives_back_each_frame_of_its_pane_up_to_its_generation()
    {
        var time = new ManualTimeProvider();
        using var credit = new RenderCredit(time, () => { }, Fixed(4096));
        credit.Charge("a", 1, 1000);
        credit.Charge("a", 2, 1000);
        credit.Charge("a", 3, 1000);
        credit.Charge("b", 9, 1000);
        Assert.Equal(4000, credit.State.UnackedBytes);

        Assert.True(credit.Ack([new RenderAck("a", 2)]));
        Assert.Equal(2000, credit.State.UnackedBytes);
        Assert.Equal(2, credit.State.InFlightFrames);

        // A repeated ack gives nothing again.
        Assert.False(credit.Ack([new RenderAck("a", 2)]));
        Assert.True(credit.Ack([new RenderAck("a", 3), new RenderAck("b", 9)]));
        Assert.Equal(0, credit.State.UnackedBytes);
    }

    [Fact]
    public async Task Credit_comes_back_after_the_ack_timeout_and_a_late_ack_gives_nothing_again()
    {
        var time = new ManualTimeProvider();
        var returned = 0;
        using var credit = new RenderCredit(time, () => Interlocked.Increment(ref returned), Fixed(4096));
        credit.Charge("a", 1, 4096);
        Assert.False(credit.MayAdmit("a"));
        await time.WaitForArmedTimerAsync();

        time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal(0, Volatile.Read(ref returned));
        Assert.False(credit.MayAdmit("a"));

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, Volatile.Read(ref returned));
        Assert.Equal(1, credit.State.FailOpens);
        Assert.Equal(0, credit.State.UnackedBytes);
        Assert.True(credit.MayAdmit("a"));

        Assert.False(credit.Ack([new RenderAck("a", 1)]));
        Assert.Equal(0, credit.State.UnackedBytes);
        Assert.Equal(0, credit.State.InFlightFrames);
    }

    [Fact]
    public void A_frame_of_a_pane_that_acks_no_more_gives_its_credit_back_while_other_panes_ack()
    {
        var time = new ManualTimeProvider();
        using var credit = new RenderCredit(time, () => { }, Fixed(8192));
        credit.Charge("gone", 1, 4096);
        long generation = 0;
        for (var i = 0; i < 25; i++)
        {
            generation++;
            credit.Charge("live", generation, 100);
            time.Advance(TimeSpan.FromMilliseconds(100));
            Assert.True(credit.Ack([new RenderAck("live", generation)]));
            credit.MayAdmit("live");
        }

        var state = credit.State;
        Assert.Equal(0, state.FailOpens);
        Assert.Equal(1, state.ExpiredFrames);
        Assert.Equal(0, state.UnackedBytes);
    }

    [Fact]
    public void An_ack_that_comes_before_the_timeout_keeps_the_credit_closed_loop()
    {
        var time = new ManualTimeProvider();
        var returned = 0;
        using var credit = new RenderCredit(time, () => Interlocked.Increment(ref returned), Fixed(4096));
        credit.Charge("a", 1, 4096);
        Assert.False(credit.MayAdmit("a"));
        time.Advance(TimeSpan.FromMilliseconds(900));
        credit.Charge("a", 2, 10);
        Assert.True(credit.Ack([new RenderAck("a", 1)]));
        // The timer fires at the first due time. Progress came, so it gives nothing back.
        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(0, credit.State.FailOpens);
        Assert.Equal(0, Volatile.Read(ref returned));
    }

    [Fact]
    public void On_a_link_below_the_render_rate_the_budget_falls_near_its_bandwidth_delay_product()
    {
        // 1 Mbit/s and 40 ms RTT. The lowest RTT sample also holds the send
        // time of one frame (33 ms), so the product is 125 000 B/s x 73 ms.
        const double rate = 125_000;
        var rtt = TimeSpan.FromMilliseconds(40);
        var run = Simulate(bytesPerSecond: rate, rtt: rtt, seconds: 6);
        var product = rate * (rtt.TotalSeconds + Frame / rate);
        Assert.True(
            run.FinalBudget >= RenderCreditOptions.Default.MinBudgetBytes && run.FinalBudget <= 2 * product,
            $"budget {run.FinalBudget} product {product:0}");
        // The echo of each key goes on the same link behind the bulk frames.
        // With the start budget of 64 KiB and no credit it would wait 0.5 s.
        // With credit it waits for at most the budget and one frame.
        Assert.True(
            run.EchoP99 < rtt + TimeSpan.FromMilliseconds(150),
            $"echo p99 {run.EchoP99.TotalMilliseconds:0} ms");
        // The link stays busy: the bulk pane still gets most of the link.
        Assert.True(run.LateThroughput > 0.85 * rate, $"throughput {run.LateThroughput:0} B/s");
    }

    [Fact]
    public void On_a_fast_link_the_budget_grows_past_its_start_value()
    {
        var run = Simulate(bytesPerSecond: 50_000_000, rtt: TimeSpan.FromMilliseconds(20), seconds: 2);
        Assert.True(run.FinalBudget > RenderCreditOptions.Default.StartBudgetBytes, $"budget {run.FinalBudget}");
    }

    private static RenderCreditOptions Fixed(int budget, int? focused = null) =>
        new()
        {
            StartBudgetBytes = budget,
            MinBudgetBytes = budget,
            MaxBudgetBytes = budget,
            MaxFocusedBudgetBytes = focused ?? budget,
        };

    /// <summary>
    /// One bulk pane that always has a new 4 KiB frame, and one focused pane
    /// that echoes a 200-byte frame each 40 ms, on one ordered link.
    /// </summary>
    private static SimulationResult Simulate(double bytesPerSecond, TimeSpan rtt, int seconds)
    {
        const int echoBytes = 200;
        var time = new ManualTimeProvider();
        using var credit = new RenderCredit(time, () => { });
        credit.NoteFocus("focus");
        var start = time.GetUtcNow();
        var linkFree = start;
        var acks = new List<(DateTimeOffset At, string PaneId, long Generation)>();
        long bulkGeneration = 0;
        long echoGeneration = 0;
        long lateBytes = 0;
        var echoes = new List<TimeSpan>();
        var nextEcho = start + TimeSpan.FromSeconds(1);
        DateTimeOffset? echoWaiting = null;
        var step = TimeSpan.FromMilliseconds(1);
        var end = start + TimeSpan.FromSeconds(seconds);
        var lateFrom = start + TimeSpan.FromSeconds(seconds / 2.0);

        DateTimeOffset Send(DateTimeOffset now, int bytes)
        {
            var leave = (linkFree > now ? linkFree : now) + TimeSpan.FromSeconds(bytes / bytesPerSecond);
            linkFree = leave;
            return leave;
        }

        while (time.GetUtcNow() < end)
        {
            var now = time.GetUtcNow();
            foreach (var due in acks.Where(a => a.At <= now).ToList())
            {
                acks.Remove(due);
                credit.Ack([new RenderAck(due.PaneId, due.Generation)]);
            }

            if (now >= nextEcho)
            {
                echoWaiting = now;
                nextEcho = now + TimeSpan.FromMilliseconds(40);
            }

            if (echoWaiting is { } typed && credit.TryCharge("focus", echoGeneration + 1, echoBytes))
            {
                echoGeneration++;
                var arrive = Send(now, echoBytes) + rtt / 2;
                echoes.Add(arrive - typed);
                acks.Add((arrive + rtt / 2, "focus", echoGeneration));
                echoWaiting = null;
            }

            while (credit.MayAdmit("bulk") && credit.TryCharge("bulk", bulkGeneration + 1, Frame))
            {
                bulkGeneration++;
                acks.Add((Send(now, Frame) + rtt, "bulk", bulkGeneration));
                if (now >= lateFrom)
                    lateBytes += Frame;
            }

            time.Advance(step);
        }

        echoes.Sort();
        return new SimulationResult(
            credit.State.Budget,
            echoes[(int)(echoes.Count * 0.99)],
            lateBytes / (seconds / 2.0));
    }

    private readonly record struct SimulationResult(int FinalBudget, TimeSpan EchoP99, double LateThroughput);
}
