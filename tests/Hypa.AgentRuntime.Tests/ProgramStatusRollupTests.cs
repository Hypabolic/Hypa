using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.ProgramStatus;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class ProgramStatusRollupTests
{
    private static ProgramStatusStore Store(params (ProgramStatusState State, string? Id, string? Msg, string? App)[] records)
    {
        var store = ProgramStatusStore.Empty;
        foreach (var (state, id, msg, app) in records)
            store = store.Apply(new ProgramStatusReport { State = state, Id = id, Msg = msg, App = app });
        return store;
    }

    private static PaneState Pane() =>
        new() { Id = new PaneId("p"), TabId = new TabId("t"), WorkspaceId = new WorkspaceId("w") };

    [Fact]
    public void Empty_store_has_no_top() => Assert.Null(ProgramStatusRollup.Top(ProgramStatusStore.Empty));

    [Fact]
    public void Urgency_order_is_blocked_error_working_done_idle()
    {
        var order = new[]
        {
            ProgramStatusState.Blocked, ProgramStatusState.Error, ProgramStatusState.Working,
            ProgramStatusState.Done, ProgramStatusState.Idle,
        };
        for (var i = 0; i < order.Length; i++)
        {
            // Put the more urgent state first, so recency does not decide.
            var store = Store((order[i], "a", null, null), (order[^1], "b", null, null), (ProgramStatusState.Idle, "c", null, null));
            Assert.Equal(order[i], ProgramStatusRollup.Top(store)!.State);
        }

        var all = Store(order.Reverse().Select((s, n) => (s, (string?)("r" + n), (string?)null, (string?)null)).ToArray());
        Assert.Equal(ProgramStatusState.Blocked, ProgramStatusRollup.Top(all)!.State);
    }

    [Fact]
    public void Root_working_and_child_blocked_give_blocked_with_the_child_msg_and_resolved_app()
    {
        var store = Store(
            (ProgramStatusState.Working, null, "root", "deploy"),
            (ProgramStatusState.Blocked, "eu-west", "Approve?", null));
        var top = ProgramStatusRollup.Top(store)!;
        Assert.Equal("eu-west", top.Id);
        Assert.Equal("Approve?", top.Msg);
        Assert.Equal("deploy", top.App);
    }

    [Fact]
    public void Same_state_uses_the_most_recently_updated_record()
    {
        var store = Store((ProgramStatusState.Working, "a", "old", null), (ProgramStatusState.Working, "b", "new", null));
        Assert.Equal("new", ProgramStatusRollup.Top(store)!.Msg);
    }

    [Theory]
    [InlineData(ProgramStatusState.Blocked, AgentStatus.Blocked)]
    [InlineData(ProgramStatusState.Error, AgentStatus.Error)]
    [InlineData(ProgramStatusState.Working, AgentStatus.Working)]
    [InlineData(ProgramStatusState.Done, AgentStatus.Done)]
    [InlineData(ProgramStatusState.Idle, AgentStatus.Idle)]
    public void Record_state_maps_to_agent_status(ProgramStatusState state, AgentStatus expected) =>
        Assert.Equal(expected, ProgramStatusRollup.ToAgentStatus(state));

    // Source precedence

    [Fact]
    public void Precedence_is_report_then_program_status_then_detector()
    {
        Assert.True(AgentSourcePrecedence.Outranks("hook", AgentSourcePrecedence.ProgramStatusSource));
        Assert.True(AgentSourcePrecedence.Outranks(AgentSourcePrecedence.ProgramStatusSource, null));
        Assert.True(AgentSourcePrecedence.Outranks("hook", null));
        Assert.False(AgentSourcePrecedence.Outranks(AgentSourcePrecedence.ProgramStatusSource, "hook"));
        Assert.False(AgentSourcePrecedence.Outranks(null, AgentSourcePrecedence.ProgramStatusSource));
        Assert.False(AgentSourcePrecedence.Outranks("hook", "other"));
    }

    // Authority policy

    [Fact]
    public void Policy_sets_status_message_kind_and_a_runtime_only_authority()
    {
        var now = DateTimeOffset.UnixEpoch;
        var pane = ProgramStatusAuthorityPolicy.Apply(
            Pane(), Store((ProgramStatusState.Blocked, null, "Password?", "brew")), now);
        Assert.Equal(AgentStatus.Blocked, pane.AgentStatus);
        Assert.Equal("Password?", pane.AgentMessage);
        Assert.Equal("brew", pane.AgentKind);
        Assert.True(pane.HoldsSemanticAuthority);
        Assert.False(pane.HoldsReportedAuthority);
        Assert.True(pane.AgentAuthority!.IsProgramStatus);
        Assert.Equal(AgentSourcePrecedence.ProgramStatusSource, pane.AgentAuthority.Source);
    }

    [Fact]
    public void Policy_releases_the_authority_when_the_last_record_goes()
    {
        var held = ProgramStatusAuthorityPolicy.Apply(Pane(), Store((ProgramStatusState.Working, null, "x", null)), DateTimeOffset.UnixEpoch);
        var released = ProgramStatusAuthorityPolicy.Apply(held, ProgramStatusStore.Empty, DateTimeOffset.UnixEpoch);
        Assert.Null(released.AgentAuthority);
        Assert.False(released.HoldsSemanticAuthority);
        Assert.Equal(AgentStatus.Unknown, released.AgentStatus);
    }

    [Fact]
    public void Policy_uses_the_prior_kind_when_a_later_top_record_has_no_app()
    {
        var before = Pane() with { AgentKind = "claude" };
        var held = ProgramStatusAuthorityPolicy.Apply(
            before, Store((ProgramStatusState.Blocked, "a", "ask", "brew")), DateTimeOffset.UnixEpoch);
        Assert.Equal("brew", held.AgentKind);

        var next = ProgramStatusAuthorityPolicy.Apply(
            held, Store((ProgramStatusState.Working, "a", "go", null)), DateTimeOffset.UnixEpoch);

        Assert.Equal("claude", next.AgentKind);
    }

    [Fact]
    public void Policy_gives_back_the_kind_the_pane_had_before_the_records_and_drops_the_message()
    {
        var before = Pane() with { AgentKind = "claude", AgentStatus = AgentStatus.Working };
        var held = ProgramStatusAuthorityPolicy.Apply(
            before, Store((ProgramStatusState.Blocked, "a", "ask", "brew")), DateTimeOffset.UnixEpoch);
        var again = ProgramStatusAuthorityPolicy.Apply(
            held, Store((ProgramStatusState.Done, "a", "ok", "make")), DateTimeOffset.UnixEpoch);
        Assert.Equal("make", again.AgentKind);

        var released = ProgramStatusAuthorityPolicy.Apply(again, ProgramStatusStore.Empty, DateTimeOffset.UnixEpoch);

        Assert.Equal("claude", released.AgentKind);
        Assert.Null(released.AgentMessage);
        Assert.Equal(AgentStatus.Unknown, released.AgentStatus);
    }

    [Fact]
    public void Policy_gives_back_no_kind_when_the_pane_had_none_before_the_records()
    {
        var held = ProgramStatusAuthorityPolicy.Apply(
            Pane(), Store((ProgramStatusState.Blocked, "a", "ask", "brew")), DateTimeOffset.UnixEpoch);
        var released = ProgramStatusAuthorityPolicy.Apply(held, ProgramStatusStore.Empty, DateTimeOffset.UnixEpoch);
        Assert.Null(released.AgentKind);
        Assert.Null(released.AgentMessage);
    }

    [Fact]
    public void Policy_keeps_an_explicit_report_authority_and_still_stores_the_records()
    {
        var reported = Pane() with
        {
            AgentStatus = AgentStatus.Idle,
            AgentAuthority = new PaneAgentAuthority { Source = "hook", Agent = "claude", State = AgentStatus.Idle },
        };
        var store = Store((ProgramStatusState.Blocked, null, "x", null));
        var next = ProgramStatusAuthorityPolicy.Apply(reported, store, DateTimeOffset.UnixEpoch);
        Assert.Equal(AgentStatus.Idle, next.AgentStatus);
        Assert.Equal("hook", next.AgentAuthority!.Source);
        Assert.Same(store, next.ProgramStatusStore);
        Assert.Same(reported.AgentAuthority, ProgramStatusAuthorityPolicy.Apply(next, ProgramStatusStore.Empty, DateTimeOffset.UnixEpoch).AgentAuthority);
    }

    [Fact]
    public void Process_death_keeps_error_and_a_held_program_status_state()
    {
        Assert.Equal(AgentStatus.Error, (Pane() with { AgentStatus = AgentStatus.Error }).StatusAfterProcessDeath());
        Assert.Equal(AgentStatus.Done, (Pane() with { AgentStatus = AgentStatus.Working }).StatusAfterProcessDeath());
    }

    // Consumers of the new Error status

    [Fact]
    public void Error_status_has_an_attention_priority_below_blocked_and_above_working()
    {
        var error = AgentViewProjection.AttentionPriority(AgentStatus.Error, seen: true);
        Assert.True(error > AgentViewProjection.AttentionPriority(AgentStatus.Working, seen: true));
        Assert.True(error < AgentViewProjection.AttentionPriority(AgentStatus.Blocked, seen: true));
        Assert.Equal("error", AgentViewProjection.StatusName(AgentStatus.Error, seen: true));
    }

    [Fact]
    public void Sidebar_knows_the_error_state()
    {
        Assert.Equal(SidebarTokenGrammar.Error, SidebarTokenGrammar.CanonicalState("ERROR"));
        Assert.Equal("error", SidebarTokenGrammar.StateText("error"));
        Assert.NotEqual(SidebarTokenGrammar.StateIcon("unknown", Hypa.AgentRuntime.Domain.AttachConfig.StatusIndicatorStyle.Symbols),
            SidebarTokenGrammar.StateIcon("error", Hypa.AgentRuntime.Domain.AttachConfig.StatusIndicatorStyle.Symbols));
        Assert.True(SidebarTokenGrammar.PriorityRank("blocked") < SidebarTokenGrammar.PriorityRank("error"));
        Assert.True(SidebarTokenGrammar.PriorityRank("error") < SidebarTokenGrammar.PriorityRank("working"));
    }
}
