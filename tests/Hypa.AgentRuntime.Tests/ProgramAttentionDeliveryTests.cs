using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.ProgramStatus;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach.Notification;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// The attach delivers the attention items of the mux. It does not decide when a pane needs
/// the person and it does not rate limit program status.
/// </summary>
public class ProgramAttentionDeliveryTests
{
    private static ProgramStatusAttentionItem Item(
        string pane = "p_1",
        string tab = "t_1",
        ProgramStatusState state = ProgramStatusState.Blocked,
        string? msg = "Allow it?",
        string? app = "brew") =>
        new()
        {
            PaneId = pane,
            TabId = tab,
            WorkspaceId = "w_1",
            State = state,
            App = app,
            Msg = msg,
            Source = new ProgramStatusSourceLabel("work", "main", "build"),
            Priority = state is ProgramStatusState.Done ? AttentionPriority.Normal : AttentionPriority.High,
        };

    private static NotificationDirector Director(
        ToastDelivery delivery = ToastDelivery.Terminal,
        RecordingSound? sound = null) =>
        new(
            new AttachUiConfig
            {
                Toast = new AttachToastConfig { Delivery = delivery, DelaySeconds = 0 },
            },
            sound: sound);

    [Fact]
    public void A_background_pane_gets_one_notice_that_names_the_pane_and_shows_the_message()
    {
        var director = Director();
        director.SetFocus("t_other", "p_other");

        director.OnProgramAttention(Item());

        var osc = Assert.Single(director.DrainOsc9());
        Assert.Contains("brew blocked", osc);
        Assert.Contains("build: Allow it?", osc);
    }

    [Fact]
    public void A_pane_in_view_gets_no_notice()
    {
        var director = Director();
        director.SetFocus("t_1", "p_1");

        director.OnProgramAttention(Item(tab: "t_1"));

        Assert.Empty(director.DrainOsc9());
    }

    [Fact]
    public void The_hypa_toast_shows_for_a_background_pane_and_not_for_a_pane_in_view()
    {
        var director = Director(ToastDelivery.Hypa);
        director.SetFocus("t_1", "p_1");
        Assert.Empty(director.OnProgramAttention(Item(tab: "t_1")));

        var toasts = director.OnProgramAttention(Item(pane: "p_2", tab: "t_2"));
        var hit = Assert.Single(toasts);
        Assert.Equal("p_2", hit.PaneId);
    }

    [Fact]
    public void The_system_notice_gets_title_and_body()
    {
        var director = Director(ToastDelivery.System);
        director.SetFocus("t_other", null);

        director.OnProgramAttention(Item(state: ProgramStatusState.Error, msg: "rsync failed"));

        var shown = Assert.Single(director.SystemShows);
        Assert.Equal("brew failed", shown.Title);
        Assert.Equal("build: rsync failed", shown.Body);
    }

    [Fact]
    public void Off_delivery_gives_no_notice()
    {
        var director = Director(ToastDelivery.Off);
        director.SetFocus("t_other", null);
        director.OnProgramAttention(Item());
        Assert.Empty(director.DrainOsc9());
    }

    [Fact]
    public void The_attach_applies_no_rate_limit_of_its_own()
    {
        var director = Director();
        director.SetFocus("t_other", null);

        for (var i = 0; i < 5; i++)
            director.OnProgramAttention(Item(pane: "p_1", msg: "ask " + i));

        Assert.Equal(5, director.DrainOsc9().Count);
    }

    [Fact]
    public void Blocked_plays_the_request_sound_and_done_plays_the_done_sound()
    {
        var sound = new RecordingSound();
        var director = Director(sound: sound);
        director.SetFocus("t_other", null);

        director.OnProgramAttention(Item(state: ProgramStatusState.Blocked));
        director.OnProgramAttention(Item(pane: "p_2", state: ProgramStatusState.Done));

        Assert.Equal(
            [NotificationSounds.Request, NotificationSounds.Done],
            director.SoundsPlayed.Select(s => s.Kind));
    }

    [Fact]
    public void A_muted_app_gets_no_notice()
    {
        var director = new NotificationDirector(new AttachUiConfig
        {
            Toast = new AttachToastConfig { Delivery = ToastDelivery.Terminal, DelaySeconds = 0 },
            Sound = new AttachSoundConfig
            {
                Agents = new Dictionary<string, SoundAgentMode>(StringComparer.Ordinal)
                {
                    ["brew"] = SoundAgentMode.Off,
                },
            },
        });
        director.SetFocus("t_other", null);

        director.OnProgramAttention(Item(app: "brew"));

        Assert.Empty(director.DrainOsc9());
    }

    [Fact]
    public void An_agent_status_change_from_a_record_raises_no_second_notice()
    {
        var director = Director();
        director.SetFocus("t_other", null);

        director.OnAgentStatusChanged("p_1", "t_1", "blocked", "brew", "Allow it?", source: "osc7501");
        Assert.Empty(director.DrainOsc9());

        director.OnProgramAttention(Item());
        Assert.Single(director.DrainOsc9());
    }

    [Fact]
    public void An_agent_status_change_from_another_source_still_notifies()
    {
        var director = Director();
        director.SetFocus("t_other", null);

        director.OnAgentStatusChanged("p_1", "t_1", "blocked", "claude", "Allow it?", source: "hook");
        Assert.Single(director.DrainOsc9());
        director.OnAgentStatusChanged("p_2", "t_1", "blocked", "claude", "Allow it?", source: null);
        Assert.Single(director.DrainOsc9());
    }

    [Fact]
    public void An_error_from_a_hook_notifies_a_background_pane_with_a_failed_title_and_the_request_sound()
    {
        var director = Director(ToastDelivery.System, new RecordingSound());
        director.SetFocus("t_other", null);

        director.OnAgentStatusChanged("p_1", "t_1", "error", "claude", "Build failed", source: "hook");

        var shown = Assert.Single(director.SystemShows);
        Assert.Equal("Agent failed", shown.Title);
        Assert.Equal("Build failed", shown.Body);
        Assert.Equal(NotificationSounds.Request, Assert.Single(director.SoundsPlayed).Kind);
    }

    [Fact]
    public void An_error_from_a_hook_gives_no_notice_for_a_pane_in_view_or_a_seen_error()
    {
        var director = Director();
        director.SetFocus("t_1", "p_1");
        director.OnAgentStatusChanged("p_1", "t_1", "error", "claude", "x", source: "hook");
        Assert.Empty(director.DrainOsc9());

        director.SetFocus("t_other", null);
        director.OnAgentStatusChanged("p_1", "t_1", "error", "claude", "x", seen: true, source: "hook");
        Assert.Empty(director.DrainOsc9());
    }

    [Fact]
    public void The_item_wire_shape_round_trips()
    {
        var item = Item(state: ProgramStatusState.Blocked) with { Kind = ProgramStatusKind.Question };
        var json = JsonSerializer.Serialize(
            ProgramStatusAttentionWire.ToPayload(3, item),
            EventPayloadJsonContext.Default.PaneProgramAttentionPayload);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(3, doc.RootElement.GetProperty("occupant_generation").GetInt32());
        Assert.Equal("high", doc.RootElement.GetProperty("priority").GetString());
        Assert.Equal(item, ProgramStatusAttentionWire.TryRead(doc.RootElement));
    }

    [Theory]
    [InlineData("""{"pane_id":"p","state":"working","source":{}}""")]
    [InlineData("""{"pane_id":"p","state":"blocked"}""")]
    [InlineData("""{"state":"blocked","source":{}}""")]
    [InlineData("""{"pane_id":"p","state":"blocked","kind":"nope","source":{}}""")]
    public void A_bad_item_is_not_read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Null(ProgramStatusAttentionWire.TryRead(doc.RootElement));
    }

    private sealed class RecordingSound : INotificationSoundPlayer
    {
        public Task PlayAsync(string kind, string? path, CancellationToken ct = default) => Task.CompletedTask;
    }
}
