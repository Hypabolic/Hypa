using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.ProgramStatus;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class ProgramStatusViewProjectionTests
{
    private static readonly ProgramStatusSourceLabel Source = new("work", "build", "pane 1");

    private static ProgramStatusStore Store(params ProgramStatusReport[] reports)
    {
        var store = ProgramStatusStore.Empty;
        foreach (var report in reports)
            store = store.Apply(report);
        return store;
    }

    private static ProgramStatusView? View(ProgramStatusStore store, ProgramStatusSourceLabel? source = null) =>
        ProgramStatusViewProjection.ForPane(store.Resolved(), source ?? Source);

    [Fact]
    public void No_records_give_no_view() => Assert.Null(View(ProgramStatusStore.Empty));

    [Fact]
    public void Blocked_permission_with_progress_and_override_gives_the_expected_view()
    {
        var store = Store(new ProgramStatusReport
        {
            State = ProgramStatusState.Blocked,
            Kind = ProgramStatusKind.Permission,
            Progress = 40,
            App = "deploy",
            Title = "Release",
            Msg = "Approve ‮exe.txt?",
        });

        var view = View(store)!;

        Assert.Equal(ProgramStatusState.Blocked, view.State);
        Assert.Equal(ProgramStatusKind.Permission, view.Kind);
        Assert.Equal(40, view.Progress);
        Assert.Equal("deploy", view.App);
        Assert.Equal("Release", view.Title);
        Assert.Equal("Approve exe.txt?", view.Msg);
        Assert.Equal(1, view.RecordCount);
        Assert.Equal(Source, view.Source);
        // The record keeps the decoded text as the program sent it.
        Assert.Equal("Approve ‮exe.txt?", store.Records[0].Msg);
    }

    [Theory]
    [InlineData('‪')]
    [InlineData('‫')]
    [InlineData('‬')]
    [InlineData('‭')]
    [InlineData('‮')]
    [InlineData('⁦')]
    [InlineData('⁧')]
    [InlineData('⁨')]
    [InlineData('⁩')]
    [InlineData('​')]
    [InlineData('‎')]
    [InlineData('﻿')]
    public void Display_text_drops_direction_controls_and_other_format_characters(char format)
    {
        var view = View(Store(new ProgramStatusReport
        {
            State = ProgramStatusState.Working,
            Title = $"a{format}b",
            Msg = $"c{format}d",
        }))!;

        Assert.Equal("ab", view.Title);
        Assert.Equal("cd", view.Msg);
    }

    [Fact]
    public void Text_made_only_of_format_characters_becomes_absent()
    {
        var view = View(Store(new ProgramStatusReport
        {
            State = ProgramStatusState.Working,
            Msg = "‮⁦",
            Title = "​",
        }))!;

        Assert.Null(view.Msg);
        Assert.Null(view.Title);
    }

    [Fact]
    public void Sanitize_keeps_plain_text_and_emoji_without_format_characters()
    {
        Assert.Equal("Ünïcode 日本語 ok", ProgramStatusViewProjection.Sanitize("Ünïcode 日本語 ok"));
        Assert.Null(ProgramStatusViewProjection.Sanitize(null));
        Assert.Null(ProgramStatusViewProjection.Sanitize(""));
    }

    [Fact]
    public void Sanitize_removes_terminal_control_characters()
    {
        Assert.Equal("ab", ProgramStatusViewProjection.Sanitize("a\u001bb"));
        Assert.Equal("ab", ProgramStatusViewProjection.Sanitize("a\u0007b"));
    }

    [Fact]
    public void Source_labels_are_safe_display_text()
    {
        var view = View(
            Store(new ProgramStatusReport { State = ProgramStatusState.Idle }),
            new ProgramStatusSourceLabel("w‮s", "t⁦s", "p​s"))!;

        Assert.Equal(new ProgramStatusSourceLabel("ws", "ts", "ps"), view.Source);
    }

    [Fact]
    public void Root_working_and_child_blocked_give_blocked_with_the_child_text_and_resolved_app()
    {
        var store = Store(
            new ProgramStatusReport { State = ProgramStatusState.Working, App = "deploy", Progress = 10 },
            new ProgramStatusReport
            {
                State = ProgramStatusState.Blocked,
                Id = "eu",
                Kind = ProgramStatusKind.Question,
                Msg = "Which region?",
            });

        var view = View(store)!;

        Assert.Equal(ProgramStatusState.Blocked, view.State);
        Assert.Equal(ProgramStatusKind.Question, view.Kind);
        Assert.Equal("deploy", view.App);
        Assert.Equal("Which region?", view.Msg);
        Assert.Null(view.Progress);
        Assert.Equal(2, view.RecordCount);
    }

    [Fact]
    public void Text_that_looks_like_a_sidebar_token_stays_plain_text()
    {
        var view = View(Store(new ProgramStatusReport
        {
            State = ProgramStatusState.Working,
            Msg = "{agent} $x state_icon <b>hi</b>",
        }))!;

        Assert.Equal("{agent} $x state_icon <b>hi</b>", view.Msg);
    }

    [Theory]
    [InlineData(ProgramStatusState.Blocked, ProgramStatusState.Error, ProgramStatusState.Blocked)]
    [InlineData(ProgramStatusState.Error, ProgramStatusState.Working, ProgramStatusState.Error)]
    [InlineData(ProgramStatusState.Working, ProgramStatusState.Done, ProgramStatusState.Working)]
    [InlineData(ProgramStatusState.Done, ProgramStatusState.Idle, ProgramStatusState.Done)]
    public void Tab_view_shows_the_most_urgent_pane_state(
        ProgramStatusState first, ProgramStatusState second, ProgramStatusState expected)
    {
        var views = new[] { PaneView(second, null), PaneView(first, null) };

        var tab = ProgramStatusViewProjection.ForTab("t1", views)!;

        Assert.Equal("t1", tab.TabId);
        Assert.Equal(expected, tab.State);
    }

    [Fact]
    public void Tab_view_carries_the_progress_of_a_working_pane()
    {
        var tab = ProgramStatusViewProjection.ForTab(
            "t1", [PaneView(ProgramStatusState.Idle, null), PaneView(ProgramStatusState.Working, 40)])!;

        Assert.Equal(ProgramStatusState.Working, tab.State);
        Assert.Equal(40, tab.Progress);
    }

    [Fact]
    public void Tab_view_has_no_progress_when_the_top_state_is_not_working()
    {
        var tab = ProgramStatusViewProjection.ForTab(
            "t1", [PaneView(ProgramStatusState.Blocked, 70), PaneView(ProgramStatusState.Working, 40)])!;

        Assert.Equal(ProgramStatusState.Blocked, tab.State);
        Assert.Null(tab.Progress);
    }

    [Fact]
    public void Tab_without_pane_views_has_no_tab_view() =>
        Assert.Null(ProgramStatusViewProjection.ForTab("t1", []));

    [Fact]
    public void Tab_with_equal_urgency_shows_the_pane_that_changed_last()
    {
        var t0 = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
        var tab = ProgramStatusViewProjection.ForTab(
            "t1",
            [
                PaneView(ProgramStatusState.Working, 10, t0.AddSeconds(5)),
                PaneView(ProgramStatusState.Working, 20, t0),
            ])!;
        Assert.Equal(10, tab.Progress);

        var reversed = ProgramStatusViewProjection.ForTab(
            "t1",
            [
                PaneView(ProgramStatusState.Working, 10, t0),
                PaneView(ProgramStatusState.Working, 20, t0.AddSeconds(5)),
            ])!;
        Assert.Equal(20, reversed.Progress);
    }

    [Fact]
    public void Pane_view_carries_the_change_time_it_is_given()
    {
        var at = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
        var records = ProgramStatusStore.Empty
            .Apply(new ProgramStatusReport { State = ProgramStatusState.Done })
            .Resolved();
        Assert.Equal(at, ProgramStatusViewProjection.ForPane(records, Source, at)!.ChangedAt);
        Assert.Null(ProgramStatusViewProjection.ForPane(records, Source)!.ChangedAt);
    }

    private static ProgramStatusView PaneView(ProgramStatusState state, int? progress, DateTimeOffset? changedAt = null) =>
        new()
        {
            State = state,
            Progress = progress,
            RecordCount = 1,
            Source = Source,
            ChangedAt = changedAt,
        };

    private static ProgramStatusView PaneView(ProgramStatusState state, bool seen) =>
        ProgramStatusViewProjection.ForPane(
            Store(new ProgramStatusReport { State = state }).Resolved(), Source, seen: seen)!;

    [Theory]
    [InlineData(ProgramStatusState.Done)]
    [InlineData(ProgramStatusState.Error)]
    public void A_seen_done_or_error_is_marked_seen_and_the_tab_view_does_not_count_it(ProgramStatusState state)
    {
        var view = PaneView(state, seen: true);

        Assert.True(view.Seen);
        Assert.Equal(state, view.State);
        Assert.Null(ProgramStatusViewProjection.ForTab("t1", [view]));
    }

    [Theory]
    [InlineData(ProgramStatusState.Done)]
    [InlineData(ProgramStatusState.Error)]
    public void An_unseen_done_or_error_gives_a_tab_view(ProgramStatusState state)
    {
        var view = PaneView(state, seen: false);

        Assert.False(view.Seen);
        Assert.Equal(state, ProgramStatusViewProjection.ForTab("t1", [view])!.State);
    }

    [Theory]
    [InlineData(ProgramStatusState.Blocked)]
    [InlineData(ProgramStatusState.Working)]
    [InlineData(ProgramStatusState.Idle)]
    public void Seen_never_hides_the_other_states(ProgramStatusState state)
    {
        var view = PaneView(state, seen: true);

        Assert.False(view.Seen);
        Assert.Equal(state, ProgramStatusViewProjection.ForTab("t1", [view])!.State);
    }

    [Fact]
    public void A_seen_done_does_not_hide_a_less_urgent_pane_of_the_tab()
    {
        var tab = ProgramStatusViewProjection.ForTab(
            "t1",
            [PaneView(ProgramStatusState.Error, seen: true), PaneView(ProgramStatusState.Idle, seen: false)])!;

        Assert.Equal(ProgramStatusState.Idle, tab.State);
    }
}
