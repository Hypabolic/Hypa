using Hypa.AgentRuntime.Domain.ProgramStatus;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class ProgramStatusAttentionRuleTests
{
    private static ProgramStatusAttentionSignature Sig(
        ProgramStatusState state,
        string? msg = null,
        ProgramStatusKind? kind = null) =>
        new(state, kind, msg);

    [Theory]
    [InlineData(ProgramStatusState.Blocked, true)]
    [InlineData(ProgramStatusState.Error, true)]
    [InlineData(ProgramStatusState.Done, true)]
    [InlineData(ProgramStatusState.Working, false)]
    [InlineData(ProgramStatusState.Idle, false)]
    public void Only_blocked_error_and_done_need_the_person(ProgramStatusState state, bool needs) =>
        Assert.Equal(needs, ProgramStatusAttentionRule.NeedsPerson(state));

    [Theory]
    [InlineData(ProgramStatusState.Blocked, AttentionPriority.High)]
    [InlineData(ProgramStatusState.Error, AttentionPriority.High)]
    [InlineData(ProgramStatusState.Done, AttentionPriority.Normal)]
    public void Done_is_normal_and_the_others_are_high(ProgramStatusState state, AttentionPriority priority) =>
        Assert.Equal(priority, ProgramStatusAttentionRule.PriorityOf(state));

    [Fact]
    public void A_new_state_is_a_new_request() =>
        Assert.True(ProgramStatusAttentionRule.IsNewRequest(
            Sig(ProgramStatusState.Working), Sig(ProgramStatusState.Done)));

    [Theory]
    [InlineData(ProgramStatusState.Done)]
    [InlineData(ProgramStatusState.Error)]
    [InlineData(ProgramStatusState.Blocked)]
    public void The_same_state_with_the_same_text_is_not_new(ProgramStatusState state) =>
        Assert.False(ProgramStatusAttentionRule.IsNewRequest(Sig(state, "same"), Sig(state, "same")));

    [Fact]
    public void A_blocked_pane_with_another_message_is_a_new_request() =>
        Assert.True(ProgramStatusAttentionRule.IsNewRequest(
            Sig(ProgramStatusState.Blocked, "one"), Sig(ProgramStatusState.Blocked, "two")));

    [Fact]
    public void A_blocked_pane_with_another_kind_is_a_new_request() =>
        Assert.True(ProgramStatusAttentionRule.IsNewRequest(
            Sig(ProgramStatusState.Blocked, "x", ProgramStatusKind.Auth),
            Sig(ProgramStatusState.Blocked, "x", ProgramStatusKind.Permission)));

    [Theory]
    [InlineData(ProgramStatusState.Done)]
    [InlineData(ProgramStatusState.Error)]
    public void A_done_or_error_pane_with_another_message_is_not_new(ProgramStatusState state) =>
        Assert.False(ProgramStatusAttentionRule.IsNewRequest(Sig(state, "one"), Sig(state, "two")));
}
