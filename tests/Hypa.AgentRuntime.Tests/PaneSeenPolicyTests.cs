using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.ProgramStatus;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class PaneSeenPolicyTests
{
    private static PaneState Pane(ProgramStatusStore store, bool programOwns = true) =>
        new()
        {
            Id = new PaneId("p1"),
            TabId = new TabId("t1"),
            WorkspaceId = new WorkspaceId("w1"),
            ProgramStatusStore = store,
            AgentAuthority = programOwns
                ? new PaneAgentAuthority
                {
                    Source = AgentSourcePrecedence.ProgramStatusSource,
                    Agent = "",
                    State = AgentStatus.Done,
                    ReportedAt = DateTimeOffset.UnixEpoch,
                }
                : null,
        };

    private static ProgramStatusStore Store(ProgramStatusState state, string? msg = null, string? id = null) =>
        ProgramStatusStore.Empty.Apply(new ProgramStatusReport { State = state, Msg = msg, Id = id });

    [Fact]
    public void A_first_done_is_a_new_result() =>
        Assert.True(PaneSeenPolicy.IsNewProgramResult(Pane(ProgramStatusStore.Empty), Pane(Store(ProgramStatusState.Done, "a"))));

    [Fact]
    public void A_done_with_other_text_is_a_new_result() =>
        Assert.True(PaneSeenPolicy.IsNewProgramResult(
            Pane(Store(ProgramStatusState.Done, "a")), Pane(Store(ProgramStatusState.Done, "b"))));

    [Fact]
    public void A_done_with_another_id_is_a_new_result() =>
        Assert.True(PaneSeenPolicy.IsNewProgramResult(
            Pane(Store(ProgramStatusState.Done, "a", id: "x")), Pane(Store(ProgramStatusState.Done, "a", id: "y"))));

    [Fact]
    public void The_same_content_is_not_a_new_result() =>
        Assert.False(PaneSeenPolicy.IsNewProgramResult(
            Pane(Store(ProgramStatusState.Done, "a")), Pane(Store(ProgramStatusState.Done, "a"))));

    [Fact]
    public void An_error_after_a_done_is_a_new_result() =>
        Assert.True(PaneSeenPolicy.IsNewProgramResult(
            Pane(Store(ProgramStatusState.Done, "a")), Pane(Store(ProgramStatusState.Error, "a"))));

    [Theory]
    [InlineData(ProgramStatusState.Working)]
    [InlineData(ProgramStatusState.Blocked)]
    [InlineData(ProgramStatusState.Idle)]
    public void Other_states_are_not_results(ProgramStatusState state) =>
        Assert.False(PaneSeenPolicy.IsNewProgramResult(Pane(ProgramStatusStore.Empty), Pane(Store(state))));

    [Fact]
    public void A_pane_whose_status_another_source_owns_has_no_new_result() =>
        Assert.False(PaneSeenPolicy.IsNewProgramResult(
            Pane(ProgramStatusStore.Empty), Pane(Store(ProgramStatusState.Done, "a"), programOwns: false)));
}
