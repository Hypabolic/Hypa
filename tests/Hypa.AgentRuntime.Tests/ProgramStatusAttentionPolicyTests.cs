using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.ProgramStatus;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class ProgramStatusAttentionPolicyTests
{
    private readonly ClockStub _clock = new();
    private readonly ProgramStatusAttentionPolicy _policy;

    public ProgramStatusAttentionPolicyTests() => _policy = new ProgramStatusAttentionPolicy(_clock);

    private static ProgramStatusView View(
        ProgramStatusState state,
        string? msg = null,
        ProgramStatusKind? kind = null,
        string? app = null,
        string pane = "build") =>
        new()
        {
            State = state,
            Kind = kind,
            App = app,
            Msg = msg,
            RecordCount = 1,
            Source = new ProgramStatusSourceLabel("work", "main", pane),
        };

    private IReadOnlyList<ProgramStatusAttentionItem> Observe(
        string pane,
        ProgramStatusView? view,
        bool owns = true) =>
        _policy.Observe(pane, "t_1", "w_1", view, owns);

    [Fact]
    public void Blocked_change_gives_one_item_with_the_source_and_the_display_text()
    {
        var items = Observe(
            "p_1",
            View(ProgramStatusState.Blocked, "Allow rm -rf?", ProgramStatusKind.Permission, "claude"));

        var item = Assert.Single(items);
        Assert.Equal("p_1", item.PaneId);
        Assert.Equal("t_1", item.TabId);
        Assert.Equal("w_1", item.WorkspaceId);
        Assert.Equal(ProgramStatusState.Blocked, item.State);
        Assert.Equal(ProgramStatusKind.Permission, item.Kind);
        Assert.Equal("claude", item.App);
        Assert.Equal("Allow rm -rf?", item.Msg);
        Assert.Equal(new ProgramStatusSourceLabel("work", "main", "build"), item.Source);
        Assert.Equal(AttentionPriority.High, item.Priority);
    }

    [Theory]
    [InlineData(ProgramStatusState.Blocked, AttentionPriority.High)]
    [InlineData(ProgramStatusState.Error, AttentionPriority.High)]
    [InlineData(ProgramStatusState.Done, AttentionPriority.Normal)]
    public void Blocked_error_and_done_need_the_person(ProgramStatusState state, AttentionPriority priority)
    {
        var item = Assert.Single(Observe("p_1", View(state)));
        Assert.Equal(priority, item.Priority);
    }

    [Theory]
    [InlineData(ProgramStatusState.Working)]
    [InlineData(ProgramStatusState.Idle)]
    public void Working_and_idle_give_no_item(ProgramStatusState state)
    {
        Assert.Empty(Observe("p_1", View(state)));
        Assert.Null(_policy.NextReleaseAt);
    }

    [Fact]
    public void A_repeat_of_the_same_state_gives_no_new_item()
    {
        Assert.Single(Observe("p_1", View(ProgramStatusState.Done, "ok")));
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty(Observe("p_1", View(ProgramStatusState.Done, "changed text")));
    }

    [Fact]
    public void A_new_blocked_request_is_a_new_change()
    {
        Assert.Single(Observe("p_1", View(ProgramStatusState.Blocked, "first")));
        _clock.Advance(TimeSpan.FromSeconds(11));
        var item = Assert.Single(Observe("p_1", View(ProgramStatusState.Blocked, "second")));
        Assert.Equal("second", item.Msg);
    }

    [Fact]
    public void A_pane_that_does_not_own_its_status_gives_no_item()
    {
        Assert.Empty(Observe("p_1", View(ProgramStatusState.Blocked), owns: false));
        Assert.Null(_policy.NextReleaseAt);
    }

    [Fact]
    public void Fifty_changes_in_one_second_from_one_pane_give_one_item()
    {
        var delivered = new List<ProgramStatusAttentionItem>();
        for (var i = 0; i < 50; i++)
        {
            delivered.AddRange(Observe("p_1", View(ProgramStatusState.Blocked, "ask " + i)));
            _clock.Advance(TimeSpan.FromMilliseconds(20));
        }

        var item = Assert.Single(delivered);
        Assert.Equal("ask 0", item.Msg);
    }

    [Fact]
    public void A_change_inside_the_limit_replaces_the_pending_item_and_does_not_queue()
    {
        Observe("p_1", View(ProgramStatusState.Blocked, "ask 0"));
        for (var i = 1; i <= 5; i++)
            Assert.Empty(Observe("p_1", View(ProgramStatusState.Blocked, "ask " + i)));

        Assert.Equal(_clock.GetUtcNow().AddSeconds(10), _policy.NextReleaseAt);
        Assert.Empty(_policy.Release());

        _clock.Advance(TimeSpan.FromSeconds(10));
        var released = Assert.Single(_policy.Release());
        Assert.Equal("ask 5", released.Msg);
        Assert.Empty(_policy.Release());
        Assert.Null(_policy.NextReleaseAt);
    }

    [Fact]
    public void A_pending_item_goes_when_the_pane_no_longer_needs_the_person()
    {
        Observe("p_1", View(ProgramStatusState.Blocked, "ask 0"));
        Observe("p_1", View(ProgramStatusState.Done, "finished"));
        Observe("p_1", View(ProgramStatusState.Working));

        Assert.Null(_policy.NextReleaseAt);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(_policy.Release());
    }

    [Fact]
    public void A_pending_item_goes_when_the_pane_is_forgotten()
    {
        Observe("p_1", View(ProgramStatusState.Blocked, "ask 0"));
        Observe("p_1", View(ProgramStatusState.Blocked, "ask 1"));
        _policy.Forget("p_1");

        Assert.Null(_policy.NextReleaseAt);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(_policy.Release());
    }

    [Fact]
    public void Ten_panes_in_one_second_give_six_items_in_sixty_seconds()
    {
        var delivered = new List<ProgramStatusAttentionItem>();
        for (var i = 0; i < 10; i++)
        {
            delivered.AddRange(Observe("p_" + i, View(ProgramStatusState.Blocked, "ask", pane: "pane " + i)));
            _clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.Equal(6, delivered.Count);
        Assert.Equal(["p_0", "p_1", "p_2", "p_3", "p_4", "p_5"], delivered.Select(d => d.PaneId));

        // Nothing more goes out before the window opens.
        _clock.Advance(TimeSpan.FromSeconds(58));
        Assert.Empty(_policy.Release());
    }

    [Fact]
    public void The_held_panes_go_out_when_the_session_window_opens_without_going_over_the_limit()
    {
        var start = _clock.GetUtcNow();
        var all = new List<(DateTimeOffset At, ProgramStatusAttentionItem Item)>();
        for (var i = 0; i < 10; i++)
        {
            foreach (var item in Observe("p_" + i, View(ProgramStatusState.Blocked, "ask")))
                all.Add((_clock.GetUtcNow(), item));
        }

        Assert.Equal(6, all.Count);
        Assert.Equal(start.AddSeconds(60), _policy.NextReleaseAt);

        _clock.Advance(TimeSpan.FromSeconds(60));
        foreach (var item in _policy.Release())
            all.Add((_clock.GetUtcNow(), item));

        Assert.Equal(10, all.Count);
        // Any 60 s window holds at most 6 items.
        foreach (var anchor in all)
        {
            var inWindow = all.Count(a => a.At >= anchor.At && a.At < anchor.At.AddSeconds(60));
            Assert.True(inWindow <= 6);
        }
    }

    [Fact]
    public void The_window_is_per_pane()
    {
        Assert.Single(Observe("p_1", View(ProgramStatusState.Blocked)));
        Assert.Single(Observe("p_2", View(ProgramStatusState.Blocked)));
    }

    [Fact]
    public void A_pane_gets_a_new_item_after_ten_seconds()
    {
        Assert.Single(Observe("p_1", View(ProgramStatusState.Blocked)));
        Observe("p_1", View(ProgramStatusState.Working));
        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Single(Observe("p_1", View(ProgramStatusState.Done)));
    }

    [Fact]
    public void Observe_releases_an_item_that_is_due()
    {
        Observe("p_1", View(ProgramStatusState.Blocked, "ask 0"));
        Observe("p_1", View(ProgramStatusState.Blocked, "ask 1"));
        _clock.Advance(TimeSpan.FromSeconds(10));

        var items = Observe("p_2", View(ProgramStatusState.Working));
        Assert.Equal("ask 1", Assert.Single(items).Msg);
    }

    private sealed class ClockStub : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
