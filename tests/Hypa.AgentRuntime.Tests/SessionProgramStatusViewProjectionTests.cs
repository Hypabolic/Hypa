using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.ProgramStatus;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class SessionProgramStatusViewProjectionTests
{
    private static (string PaneId, ProgramStatusView View) Pane(
        string id,
        ProgramStatusState state,
        string label,
        string? msg = null,
        ProgramStatusKind? kind = null,
        int? progress = null,
        string? app = null,
        DateTimeOffset? changedAt = null,
        bool seen = false) =>
        (id, new ProgramStatusView
        {
            State = state,
            Kind = kind,
            Progress = progress,
            App = app,
            Msg = msg,
            RecordCount = 1,
            Source = new ProgramStatusSourceLabel("work", "build", label),
            Seen = seen,
            ChangedAt = changedAt,
        });

    [Fact]
    public void No_panes_give_no_summary() =>
        Assert.Null(ProgramStatusViewProjection.ForSession([]));

    [Fact]
    public void The_most_urgent_pane_wins()
    {
        var summary = ProgramStatusViewProjection.ForSession(
        [
            Pane("p1", ProgramStatusState.Working, "first", "Building"),
            Pane("p2", ProgramStatusState.Blocked, "second", "Approve deploy?", ProgramStatusKind.Permission),
            Pane("p3", ProgramStatusState.Error, "third", "Failed"),
        ])!;

        Assert.Equal(ProgramStatusState.Blocked, summary.State);
        Assert.Equal("p2", summary.PaneId);
        Assert.Equal(ProgramStatusKind.Permission, summary.Kind);
        Assert.Equal("second: Approve deploy?", summary.Msg);
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_most_recent_change_wins_a_tie_of_urgency()
    {
        var summary = ProgramStatusViewProjection.ForSession(
        [
            Pane("p1", ProgramStatusState.Blocked, "older", "Old?", changedAt: T0),
            Pane("p2", ProgramStatusState.Blocked, "newer", "New?", changedAt: T0.AddSeconds(5)),
        ])!;

        Assert.Equal("p2", summary.PaneId);
        Assert.Equal("newer: New?", summary.Msg);
    }

    [Fact]
    public void The_most_recent_change_wins_even_when_it_comes_first()
    {
        var summary = ProgramStatusViewProjection.ForSession(
        [
            Pane("p1", ProgramStatusState.Done, "newer", changedAt: T0.AddSeconds(5)),
            Pane("p2", ProgramStatusState.Done, "older", changedAt: T0),
        ])!;

        Assert.Equal("p1", summary.PaneId);
    }

    [Fact]
    public void Urgency_beats_recency()
    {
        var summary = ProgramStatusViewProjection.ForSession(
        [
            Pane("p1", ProgramStatusState.Blocked, "blocked", changedAt: T0),
            Pane("p2", ProgramStatusState.Error, "error", changedAt: T0.AddHours(1)),
        ])!;

        Assert.Equal("p1", summary.PaneId);
    }

    [Fact]
    public void A_pane_with_an_unknown_change_time_is_older_than_a_known_one()
    {
        var summary = ProgramStatusViewProjection.ForSession(
        [
            Pane("p1", ProgramStatusState.Done, "restored"),
            Pane("p2", ProgramStatusState.Done, "fresh", changedAt: T0),
        ])!;

        Assert.Equal("p2", summary.PaneId);
    }

    [Fact]
    public void The_first_pane_wins_a_tie()
    {
        var summary = ProgramStatusViewProjection.ForSession(
        [
            Pane("p1", ProgramStatusState.Done, "first"),
            Pane("p2", ProgramStatusState.Done, "second"),
        ])!;

        Assert.Equal("p1", summary.PaneId);
    }

    [Fact]
    public void Working_carries_progress_and_no_kind()
    {
        var summary = ProgramStatusViewProjection.ForSession(
            [Pane("p1", ProgramStatusState.Working, "build", "Compiling", ProgramStatusKind.Question, 40, "cargo")])!;

        Assert.Equal(40, summary.Progress);
        Assert.Null(summary.Kind);
        Assert.Equal("cargo", summary.App);
    }

    [Fact]
    public void Blocked_carries_kind_and_no_progress()
    {
        var summary = ProgramStatusViewProjection.ForSession(
            [Pane("p1", ProgramStatusState.Blocked, "build", "Password?", ProgramStatusKind.Auth, 40)])!;

        Assert.Equal(ProgramStatusKind.Auth, summary.Kind);
        Assert.Null(summary.Progress);
    }

    [Fact]
    public void A_pane_without_a_message_gives_its_label_as_the_message()
    {
        var summary = ProgramStatusViewProjection.ForSession([Pane("p1", ProgramStatusState.Done, "build")])!;

        Assert.Equal("build", summary.Msg);
    }

    [Fact]
    public void An_empty_label_falls_back_to_the_pane_id()
    {
        var summary = ProgramStatusViewProjection.ForSession([Pane("p_9", ProgramStatusState.Done, "", "Ready")])!;

        Assert.Equal("p_9: Ready", summary.Msg);
    }

    [Fact]
    public void The_message_is_safe_display_text_and_short()
    {
        var summary = ProgramStatusViewProjection.ForSession(
            [Pane("p1", ProgramStatusState.Blocked, "pa‮ne\u001b", new string('x', 500))])!;

        Assert.DoesNotContain('‮', summary.Msg);
        Assert.DoesNotContain('\u001b', summary.Msg);
        Assert.StartsWith("pane: xxx", summary.Msg, StringComparison.Ordinal);
        Assert.True(summary.Msg.Length <= ProgramStatusViewProjection.SessionMsgMaxCols);
    }

    [Fact]
    public void The_wire_shape_round_trips()
    {
        var summary = ProgramStatusViewProjection.ForSession(
            [Pane("p2", ProgramStatusState.Blocked, "second", "Approve?", ProgramStatusKind.Question, app: "deploy")])!;

        var node = ProgramStatusViewWire.ToNode(summary)!;
        using var doc = System.Text.Json.JsonDocument.Parse(node.ToJsonString());

        Assert.Equal(summary, ProgramStatusViewWire.TryReadSession(doc.RootElement));
    }

    [Fact]
    public void A_seen_done_or_error_gives_no_summary()
    {
        Assert.Null(ProgramStatusViewProjection.ForSession(
        [
            Pane("p1", ProgramStatusState.Done, "first", "Finished", seen: true),
            Pane("p2", ProgramStatusState.Error, "second", "Failed", seen: true),
        ]));
    }

    [Fact]
    public void An_unseen_done_gives_a_summary_and_a_seen_one_does_not_hide_a_less_urgent_pane()
    {
        var summary = ProgramStatusViewProjection.ForSession(
        [
            Pane("p1", ProgramStatusState.Error, "first", "Failed", seen: true),
            Pane("p2", ProgramStatusState.Done, "second", "Finished"),
        ])!;

        Assert.Equal("p2", summary.PaneId);
        Assert.Equal(ProgramStatusState.Done, summary.State);
    }

    [Fact]
    public void Blocked_is_never_hidden_by_seen()
    {
        var summary = ProgramStatusViewProjection.ForSession(
            [Pane("p1", ProgramStatusState.Blocked, "first", "Approve?", ProgramStatusKind.Permission, seen: true)])!;

        Assert.Equal(ProgramStatusState.Blocked, summary.State);
    }
}
