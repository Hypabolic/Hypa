using Hypa.AgentRuntime.Application;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class ProgramStatusEmitCoalescerTests
{
    private readonly ClockStub _clock = new();
    private readonly ProgramStatusEmitCoalescer _coalescer;

    public ProgramStatusEmitCoalescerTests() => _coalescer = new ProgramStatusEmitCoalescer(_clock);

    [Fact]
    public void One_change_after_quiet_emits_at_once()
    {
        Assert.Equal(ProgramStatusEmitDecision.EmitNow, _coalescer.Request("p_1"));
        Assert.Null(_coalescer.NextDueAt);
    }

    [Fact]
    public void A_change_inside_the_interval_waits_for_its_end()
    {
        Assert.Equal(ProgramStatusEmitDecision.EmitNow, _coalescer.Request("p_1"));
        _coalescer.Complete("p_1");
        _clock.Advance(TimeSpan.FromMilliseconds(30));

        Assert.Equal(ProgramStatusEmitDecision.Deferred, _coalescer.Request("p_1"));
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromMilliseconds(70), _coalescer.NextDueAt);
        Assert.Empty(_coalescer.TakeDue());

        _clock.Advance(TimeSpan.FromMilliseconds(70));
        Assert.Equal(["p_1"], _coalescer.TakeDue());
        Assert.Empty(_coalescer.TakeDue());
    }

    [Fact]
    public void Many_changes_inside_the_interval_give_one_pending_emit()
    {
        _coalescer.Request("p_1");
        _coalescer.Complete("p_1");
        for (var i = 0; i < 50; i++)
            Assert.Equal(ProgramStatusEmitDecision.Deferred, _coalescer.Request("p_1"));

        _clock.Advance(ProgramStatusEmitCoalescer.DefaultMinInterval);
        Assert.Single(_coalescer.TakeDue());
    }

    [Fact]
    public void A_thousand_changes_in_one_second_give_at_most_eleven_emits_and_the_last_one_is_not_lost()
    {
        var emits = 0;
        for (var ms = 0; ms < 1000; ms++)
        {
            if (_coalescer.Request("p_1") == ProgramStatusEmitDecision.EmitNow)
            {
                emits++;
                _coalescer.Complete("p_1");
            }

            _clock.Advance(TimeSpan.FromMilliseconds(1));
            foreach (var _ in _coalescer.TakeDue())
            {
                emits++;
                _coalescer.Complete("p_1");
            }
        }

        Assert.InRange(emits, 2, 11);

        // The last change is pending or already sent. A wait of one interval drains it.
        _clock.Advance(ProgramStatusEmitCoalescer.DefaultMinInterval);
        emits += _coalescer.TakeDue().Count;
        Assert.False(_coalescer.IsBusy);
        Assert.Null(_coalescer.NextDueAt);
    }

    [Fact]
    public void A_change_during_an_emit_waits_for_the_emit_to_end()
    {
        Assert.Equal(ProgramStatusEmitDecision.EmitNow, _coalescer.Request("p_1"));
        _clock.Advance(TimeSpan.FromMilliseconds(500));

        // The interval has passed but the first emit still runs: one in flight at most.
        Assert.Equal(ProgramStatusEmitDecision.Deferred, _coalescer.Request("p_1"));
        Assert.Null(_coalescer.NextDueAt);
        Assert.Empty(_coalescer.TakeDue());

        _coalescer.Complete("p_1");
        Assert.NotNull(_coalescer.NextDueAt);
        Assert.Equal(["p_1"], _coalescer.TakeDue());
    }

    [Fact]
    public void Panes_do_not_share_an_interval()
    {
        Assert.Equal(ProgramStatusEmitDecision.EmitNow, _coalescer.Request("p_1"));
        Assert.Equal(ProgramStatusEmitDecision.EmitNow, _coalescer.Request("p_2"));
    }

    [Fact]
    public void A_forgotten_pane_drops_its_pending_emit()
    {
        _coalescer.Request("p_1");
        _coalescer.Complete("p_1");
        _coalescer.Request("p_1");
        Assert.True(_coalescer.IsBusy);

        _coalescer.Forget("p_1");
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(_coalescer.TakeDue());
        Assert.False(_coalescer.IsBusy);
        Assert.Null(_coalescer.NextDueAt);

        // The end of an emit that was in flight does nothing.
        _coalescer.Complete("p_1");
    }

    private sealed class ClockStub : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
