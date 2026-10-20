using Hypa.Cli.Attach;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The rate of the reanchor requests of one pane when the host refuses the
/// full frame of each request, and the tickets that keep a late reply or an
/// earlier timer from a later request.
/// </summary>
public sealed class PaneChainReanchorRequestsTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_refused_full_frame_asks_again_at_once_one_time_then_after_the_wait()
    {
        var requests = new PaneChainReanchorRequests();

        Assert.True(requests.Reopen("p1", Start));
        var first = Assert.Single(requests.TakeQueued(Start));
        Assert.Equal("p1", first.PaneId);
        Assert.Equal(ChainReanchorFinish.Done, requests.Finish(first, failed: false, Start, out _));

        // The full frame of that request is refused too. The pane does not
        // ask again at once; it gets a retry after the wait.
        var soon = Start + TimeSpan.FromMilliseconds(10);
        Assert.False(requests.Reopen("p1", soon));
        Assert.False(requests.HasQueued);
        Assert.True(requests.HasWork);
        var retry = Assert.Single(requests.TakeRetryTimers());
        Assert.Equal("p1", retry.PaneId);
        Assert.Empty(requests.TakeRetryTimers());

        // A third refusal in the wait records nothing more.
        Assert.False(requests.Reopen("p1", soon));
        Assert.Empty(requests.TakeRetryTimers());

        // After the wait the pane asks again.
        Assert.True(requests.TakeRetry(retry));
        Assert.Equal("p1", Assert.Single(requests.TakeQueued(soon + PaneChainReanchorRequests.ResendAfter)).PaneId);
        Assert.Equal(2, requests.RequestsSent);
    }

    [Fact]
    public void A_refusal_after_the_wait_asks_again_at_once()
    {
        var requests = new PaneChainReanchorRequests();
        Assert.True(requests.Reopen("p1", Start));
        var request = Assert.Single(requests.TakeQueued(Start));
        _ = requests.Finish(request, failed: false, Start, out _);

        Assert.True(requests.Reopen("p1", Start + PaneChainReanchorRequests.ResendAfter));
        Assert.True(requests.HasQueued);
    }

    [Fact]
    public void A_shown_full_frame_restores_the_ask_at_once()
    {
        var requests = new PaneChainReanchorRequests();
        Assert.True(requests.Reopen("p1", Start));
        var request = Assert.Single(requests.TakeQueued(Start));
        _ = requests.Finish(request, failed: false, Start, out _);

        requests.Clear("p1");

        Assert.True(requests.Reopen("p1", Start + TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public void A_refusal_in_flight_after_an_ask_at_once_waits_after_the_reply()
    {
        var requests = new PaneChainReanchorRequests();
        Assert.True(requests.Reopen("p1", Start));
        var request = Assert.Single(requests.TakeQueued(Start));

        // The full frame comes before the reply and is refused.
        Assert.False(requests.Reopen("p1", Start));

        Assert.Equal(ChainReanchorFinish.RetryAfterWait, requests.Finish(request, failed: false, Start, out var retry));
        Assert.False(requests.HasQueued);
        Assert.True(requests.TakeRetry(retry));
        Assert.True(requests.HasQueued);
    }

    [Fact]
    public void A_first_break_in_flight_still_asks_again_at_once_after_the_reply()
    {
        var requests = new PaneChainReanchorRequests();
        Assert.True(requests.Request("p1", Start));
        var request = Assert.Single(requests.TakeQueued(Start));

        // The full frame of the request is shown, then the chain breaks again.
        requests.Clear("p1");
        Assert.False(requests.Request("p1", Start));

        Assert.Equal(ChainReanchorFinish.Queued, requests.Finish(request, failed: false, Start, out _));
        Assert.True(requests.HasQueued);
    }

    [Fact]
    public void Reset_forgets_every_request()
    {
        var requests = new PaneChainReanchorRequests();
        Assert.True(requests.Reopen("p1", Start));
        var request = Assert.Single(requests.TakeQueued(Start));
        Assert.True(requests.Request("p2", Start));
        Assert.False(requests.Reopen("p1", Start));

        requests.Reset();

        Assert.False(requests.IsOpen("p1"));
        Assert.False(requests.IsOpen("p2"));
        Assert.False(requests.HasWork);
        Assert.Equal(0, requests.InFlight);
        Assert.Equal(ChainReanchorFinish.Stale, requests.Finish(request, failed: false, Start, out _));
        Assert.True(requests.Reopen("p1", Start));
    }

    [Fact]
    public void A_late_reply_from_before_a_reset_does_not_end_the_next_request_of_the_pane()
    {
        var requests = new PaneChainReanchorRequests();
        Assert.True(requests.Request("p1", Start));
        var before = Assert.Single(requests.TakeQueued(Start));

        // The attach shows another mux connection. The pane asks the new
        // mux for a full frame, and that request is in flight too.
        requests.Reset();
        Assert.True(requests.Request("p1", Start));
        var after = Assert.Single(requests.TakeQueued(Start));
        Assert.NotEqual(before.Id, after.Id);

        // The reply of the earlier connection comes now, failed or not.
        Assert.Equal(ChainReanchorFinish.Stale, requests.Finish(before, failed: false, Start, out _));
        Assert.Equal(ChainReanchorFinish.Stale, requests.Finish(before, failed: true, Start, out _));

        // The later request is still in flight: a refused full frame does not
        // queue a second request beside it.
        Assert.Equal(1, requests.InFlight);
        Assert.False(requests.Reopen("p1", Start));
        Assert.False(requests.HasQueued);
        Assert.Equal(ChainReanchorFinish.Queued, requests.Finish(after, failed: false, Start, out _));
    }

    [Fact]
    public void A_timer_of_an_earlier_retry_does_not_take_a_later_retry_of_the_pane()
    {
        var requests = new PaneChainReanchorRequests();
        var at = Start;

        // A retry after the wait, with its timer.
        Assert.True(requests.Reopen("p1", at));
        _ = requests.Finish(Assert.Single(requests.TakeQueued(at)), failed: false, at, out _);
        Assert.False(requests.Reopen("p1", at + TimeSpan.FromMilliseconds(10)));
        var earlier = Assert.Single(requests.TakeRetryTimers());

        // The host shows a full frame; later the chain breaks again and gets
        // its own retry after the wait.
        requests.Clear("p1");
        at += TimeSpan.FromMilliseconds(600);
        Assert.True(requests.Reopen("p1", at));
        _ = requests.Finish(Assert.Single(requests.TakeQueued(at)), failed: false, at, out _);
        Assert.False(requests.Reopen("p1", at + TimeSpan.FromMilliseconds(10)));
        var later = Assert.Single(requests.TakeRetryTimers());

        // The earlier timer ends first. It does not send the later retry early.
        Assert.False(requests.TakeRetry(earlier));
        Assert.False(requests.HasQueued);
        Assert.True(requests.TakeRetry(later));
        Assert.True(requests.HasQueued);
    }

    [Fact]
    public void A_timer_that_started_before_a_reset_does_nothing()
    {
        var requests = new PaneChainReanchorRequests();
        Assert.True(requests.Reopen("p1", Start));
        _ = requests.Finish(Assert.Single(requests.TakeQueued(Start)), failed: false, Start, out _);
        Assert.False(requests.Reopen("p1", Start));
        var retry = Assert.Single(requests.TakeRetryTimers());

        requests.Reset();

        Assert.False(requests.TakeRetry(retry));
        Assert.False(requests.HasQueued);
        Assert.False(requests.IsOpen("p1"));
    }
}
