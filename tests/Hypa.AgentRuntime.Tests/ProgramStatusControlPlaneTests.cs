using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.ProgramStatus;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;
using Hypa.AgentRuntime.Tests.Support;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class ProgramStatusControlPlaneTests : IDisposable
{
    private const string EventType = ProtocolEventTypes.PaneProgramStatusChanged;
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public ProgramStatusControlPlaneTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-program-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose() => SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);

    private static ProgramStatusSignal Report(
        ProgramStatusState state,
        string? msg = null,
        string? app = null,
        ProgramStatusKind? kind = null) =>
        ProgramStatusSignal.FromReport(new ProgramStatusReport { State = state, Msg = msg, App = app, Kind = kind });

    [Fact]
    public async Task Report_shows_in_pane_get_and_fires_one_event()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(Report(ProgramStatusState.Working, "Installing updates", "brew"));

        var changed = await fixture.Sink.WaitForTypeAsync(EventType);
        var record = changed.GetProperty("params").GetProperty("payload").GetProperty("program_status")[0];
        Assert.Equal("working", record.GetProperty("state").GetString());

        var pane = await fixture.GetPaneAsync();
        var rec = Assert.Single(pane.GetProperty("program_status").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, rec.GetProperty("id").ValueKind);
        Assert.Equal("working", rec.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, rec.GetProperty("kind").ValueKind);
        Assert.Equal(JsonValueKind.Null, rec.GetProperty("progress").ValueKind);
        Assert.Equal("brew", rec.GetProperty("app").GetString());
        Assert.Equal(JsonValueKind.Null, rec.GetProperty("title").ValueKind);
        Assert.Equal("Installing updates", rec.GetProperty("msg").GetString());
        Assert.Equal(1, rec.GetProperty("updated_seq").GetInt64());
    }

    [Fact]
    public async Task Event_pane_get_snapshot_and_tab_list_carry_the_same_view()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(ProgramStatusSignal.FromReport(new ProgramStatusReport
        {
            State = ProgramStatusState.Working,
            Progress = 40,
            App = "brew",
            Msg = "Install \u202Eit",
        }));

        var payload = (await fixture.Sink.WaitForTypeAsync(EventType)).GetProperty("params").GetProperty("payload");
        var eventView = payload.GetProperty("view");
        Assert.Equal("Install it", eventView.GetProperty("msg").GetString());
        Assert.Equal(40, eventView.GetProperty("progress").GetInt32());
        Assert.Equal(1, eventView.GetProperty("record_count").GetInt32());

        var pane = await fixture.GetPaneAsync();
        Assert.Equal(eventView.GetRawText(), pane.GetProperty("program_status_view").GetRawText());
        // The raw record keeps the decoded text.
        Assert.Contains("\u202E", pane.GetProperty("program_status")[0].GetProperty("msg").GetString());

        var snapshot = await fixture.Call(ProtocolMethods.SessionSnapshot, new JsonObject());
        var agentView = snapshot.GetProperty("agent_view");
        Assert.False(agentView.GetProperty("active").GetBoolean());
        var inSnapshot = Assert.Single(agentView.GetProperty("panes").EnumerateArray());
        Assert.Equal(fixture.PaneId, inSnapshot.GetProperty("pane_id").GetString());
        Assert.Equal("Install it", inSnapshot.GetProperty("msg").GetString());

        var tabView = payload.GetProperty("tab_view");
        Assert.Equal("working", tabView.GetProperty("state").GetString());
        Assert.Equal(40, tabView.GetProperty("progress").GetInt32());
        Assert.Equal(tabView.GetRawText(), Assert.Single(agentView.GetProperty("tabs").EnumerateArray()).GetRawText());
        var tabs = await fixture.Call(ProtocolMethods.TabList, new JsonObject());
        Assert.Equal(
            tabView.GetRawText(),
            tabs.EnumerateArray().First().GetProperty("program_status_view").GetRawText());
    }

    [Fact]
    public async Task A_typed_consumer_reads_the_session_summary_and_the_views_of_the_snapshot()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Done, msg: "Finished"));
        await fixture.Sink.WaitForAsync(EventType, p => p.GetProperty("program_status").GetArrayLength() == 1);

        var snapshot = await fixture.Call(ProtocolMethods.SessionSnapshot, new JsonObject());
        var typed = JsonSerializer.Deserialize(
            snapshot.GetProperty("agent_view").GetRawText(),
            Hypa.AgentRuntime.Protocol.Json.ProtocolJsonContext.Default.SessionAgentViewSnapshot);

        Assert.NotNull(typed);
        Assert.NotNull(typed.Session);
        Assert.Equal("done", typed.Session.Value.GetProperty("state").GetString());
        Assert.Single(typed.Panes!);
        Assert.Single(typed.Tabs!);
    }

    [Fact]
    public async Task Closing_a_pane_publishes_the_recomputed_tab_view()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(ProgramStatusSignal.FromReport(new ProgramStatusReport
        {
            State = ProgramStatusState.Working,
            Progress = 40,
        }));
        await fixture.Sink.WaitForTypeAsync(EventType);

        var split = await fixture.Call(
            ProtocolMethods.PaneSplit,
            new JsonObject { ["pane_id"] = fixture.PaneId, ["direction"] = "right", ["command"] = "muse" });
        var otherId = split.GetProperty("pane_id").GetString()!;
        var other = fixture.Factory.Created.Single(r => r.Id.Value == otherId);
        other.Signal(Report(ProgramStatusState.Idle));
        await fixture.Sink.WaitForAsync(
            EventType,
            p => p.GetProperty("pane_id").GetString() == otherId
                && p.GetProperty("tab_view").GetProperty("state").GetString() == "working");

        await fixture.Call(ProtocolMethods.PaneClose, new JsonObject { ["pane_id"] = fixture.PaneId });

        var gone = (await fixture.Sink.WaitForAsync(
                EventType,
                p => p.GetProperty("pane_id").GetString() == fixture.PaneId
                    && p.GetProperty("program_status").GetArrayLength() == 0))
            .GetProperty("params").GetProperty("payload");
        Assert.Equal(JsonValueKind.Null, gone.GetProperty("view").ValueKind);
        var tabView = gone.GetProperty("tab_view");
        Assert.Equal("idle", tabView.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, tabView.GetProperty("progress").ValueKind);
        var tabs = await fixture.Call(ProtocolMethods.TabList, new JsonObject());
        Assert.Equal(
            "idle",
            tabs.EnumerateArray().First().GetProperty("program_status_view").GetProperty("state").GetString());
    }

    private static ProgramStatusSignal Progress(int percent) =>
        ProgramStatusSignal.FromReport(new ProgramStatusReport { State = ProgramStatusState.Working, Progress = percent });

    [Fact]
    public async Task Panes_of_the_same_urgency_show_the_one_that_changed_last_in_the_tab_and_session_views()
    {
        var clock = new SteppedClock();
        await using var fixture = await NewFixtureAsync(clock);
        var split = await fixture.Call(
            ProtocolMethods.PaneSplit,
            new JsonObject { ["pane_id"] = fixture.PaneId, ["direction"] = "right", ["command"] = "muse" });
        var secondId = split.GetProperty("pane_id").GetString()!;
        var first = fixture.Runtime;
        var second = fixture.Factory.Created.Single(r => r.Id.Value == secondId);

        // The pane ids are random. The result must not depend on their order.
        first.Signal(Report(ProgramStatusState.Blocked, "first asks"));
        clock.Advance(TimeSpan.FromMinutes(1));
        second.Signal(Report(ProgramStatusState.Blocked, "second asks"));
        await fixture.Sink.WaitForAsync(EventType, p =>
            p.GetProperty("pane_id").GetString() == secondId
            && p.GetProperty("session_view").GetProperty("pane_id").GetString() == secondId);

        clock.Advance(TimeSpan.FromMinutes(1));
        first.Signal(Report(ProgramStatusState.Blocked, "first asks again"));
        await fixture.Sink.WaitForAsync(EventType, p =>
            p.GetProperty("pane_id").GetString() == fixture.PaneId
            && p.GetProperty("session_view").GetProperty("pane_id").GetString() == fixture.PaneId
            && p.GetProperty("session_view").GetProperty("msg").GetString()!.EndsWith("first asks again", StringComparison.Ordinal));

        // Tab view: working panes tie. It shows the progress of the pane that changed last.
        clock.Advance(TimeSpan.FromMinutes(1));
        first.Signal(Progress(10));
        clock.Advance(TimeSpan.FromMinutes(1));
        second.Signal(Progress(20));
        await fixture.Sink.WaitForAsync(EventType, p => TabProgress(p) == 20);

        clock.Advance(TimeSpan.FromMinutes(1));
        first.Signal(Progress(30));
        await fixture.Sink.WaitForAsync(EventType, p => TabProgress(p) == 30);
    }

    private static int? TabProgress(JsonElement payload) =>
        payload.TryGetProperty("tab_view", out var tab)
        && tab.ValueKind == JsonValueKind.Object
        && tab.GetProperty("progress").ValueKind == JsonValueKind.Number
            ? tab.GetProperty("progress").GetInt32()
            : null;

    [Fact]
    public async Task Mapped_progress_makes_a_root_record_and_stops_after_a_real_report_until_reset()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(ProgramStatusSignal.FromReport(
            new ProgramStatusReport { State = ProgramStatusState.Working, Progress = 40 }, ProgramStatusReportSource.ConEmuProgress));
        await fixture.Sink.WaitForAsync(EventType, p => p.GetProperty("program_status")[0].GetProperty("progress").ValueKind == JsonValueKind.Number);
        var pane = await fixture.GetPaneAsync();
        var rec = Assert.Single(pane.GetProperty("program_status").EnumerateArray());
        Assert.Equal("working", rec.GetProperty("state").GetString());
        Assert.Equal(40, rec.GetProperty("progress").GetInt32());
        Assert.Equal(JsonValueKind.Null, rec.GetProperty("app").ValueKind);
        Assert.Equal(JsonValueKind.Null, rec.GetProperty("msg").ValueKind);
        Assert.Equal(JsonValueKind.Null, rec.GetProperty("title").ValueKind);
        Assert.Equal(JsonValueKind.Null, rec.GetProperty("kind").ValueKind);
        var view = pane.GetProperty("program_status_view");
        Assert.Equal(40, view.GetProperty("progress").GetInt32());

        fixture.Runtime.Signal(Report(ProgramStatusState.Blocked, "Need password", "brew", ProgramStatusKind.Auth));
        await fixture.Sink.WaitForAsync(EventType, p => p.GetProperty("program_status")[0].GetProperty("state").GetString() == "blocked");
        var before = fixture.Sink.Count(EventType);

        fixture.Runtime.Signal(ProgramStatusSignal.FromReport(
            new ProgramStatusReport { State = ProgramStatusState.Working, Progress = 10 }, ProgramStatusReportSource.ConEmuProgress));
        fixture.Runtime.Signal(ProgramStatusSignal.FromReport(new ProgramStatusReport { State = ProgramStatusState.Clear }, ProgramStatusReportSource.ConEmuProgress));
        // A later signal in the same order proves the mapped ones are done.
        fixture.Runtime.Signal(ProgramStatusSignal.Reset);
        await fixture.Sink.WaitForAsync(EventType, p => p.GetProperty("program_status").GetArrayLength() == 0);
        Assert.Equal(before + 1, fixture.Sink.Count(EventType));

        fixture.Runtime.Signal(ProgramStatusSignal.FromReport(
            new ProgramStatusReport { State = ProgramStatusState.Working, Progress = 10 }, ProgramStatusReportSource.ConEmuProgress));
        await fixture.Sink.WaitForAsync(EventType, p => p.GetProperty("program_status").GetArrayLength() == 1);
        var again = Assert.Single((await fixture.GetPaneAsync()).GetProperty("program_status").EnumerateArray());
        Assert.Equal(10, again.GetProperty("progress").GetInt32());
    }

    [Fact]
    public async Task Pane_without_records_has_no_view()
    {
        await using var fixture = await NewFixtureAsync();
        var pane = await fixture.GetPaneAsync();
        Assert.Equal(JsonValueKind.Null, pane.GetProperty("program_status_view").ValueKind);
    }

    [Fact]
    public async Task Event_carries_pane_tab_and_workspace_ids()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(Report(ProgramStatusState.Done, "x"));
        var payload = (await fixture.Sink.WaitForTypeAsync(EventType)).GetProperty("params").GetProperty("payload");
        var pane = await fixture.GetPaneAsync();
        Assert.Equal(fixture.PaneId, payload.GetProperty("pane_id").GetString());
        Assert.Equal(pane.GetProperty("tab_id").GetString(), payload.GetProperty("tab_id").GetString());
        Assert.Equal(pane.GetProperty("workspace_id").GetString(), payload.GetProperty("workspace_id").GetString());
        Assert.True(payload.GetProperty("occupant_generation").GetInt32() > 0);
    }

    [Fact]
    public async Task A_burst_of_reports_gives_few_events_the_last_state_and_no_queued_emit()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await NewFixtureAsync(clock);
        for (var i = 0; i < 3000; i++)
        {
            fixture.Runtime.Signal(ProgramStatusSignal.FromReport(
                new ProgramStatusReport { State = ProgramStatusState.Working, Progress = i % 100 }, ProgramStatusReportSource.ConEmuProgress));
        }

        fixture.Runtime.Signal(Report(ProgramStatusState.Done, "finished"));
        // The clock stands still, so the whole burst falls in one interval: one emit now
        // and one pending. Each step of the clock lets the pending emit go out.
        var last = (await fixture.WaitForAfterAdvancingAsync(
                clock,
                p => p.GetProperty("program_status")[0].GetProperty("msg").GetString() == "finished"))
            .GetProperty("params").GetProperty("payload");
        Assert.Equal("done", last.GetProperty("program_status")[0].GetProperty("state").GetString());

        // The count is bounded by the interval, not by the 3001 reports.
        clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.SettleAsync();
        Assert.True(fixture.Cp.ProgramStatusEmitsAreIdleForTest);
        var before = fixture.Sink.Count(EventType);
        Assert.InRange(before, 1, 2);
        clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.SettleAsync();
        Assert.Equal(before, fixture.Sink.Count(EventType));
    }

    [Fact]
    public async Task A_closed_pane_drops_its_pending_emit_and_keeps_the_final_event()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await NewFixtureAsync(clock);
        fixture.Runtime.Signal(Report(ProgramStatusState.Working, "one"));
        await fixture.Sink.WaitForTypeAsync(EventType);
        // This change is inside the interval. It waits.
        fixture.Runtime.Signal(Report(ProgramStatusState.Working, "two"));
        await fixture.Call(ProtocolMethods.PaneClose, new JsonObject { ["pane_id"] = fixture.PaneId });
        await fixture.Sink.WaitForAsync(EventType, p => p.GetProperty("program_status").GetArrayLength() == 0);
        var count = fixture.Sink.Count(EventType);

        // Past the interval, a pending emit of the closed pane would go out now.
        clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.SettleAsync();
        Assert.Equal(count, fixture.Sink.Count(EventType));
        Assert.True(fixture.Cp.ProgramStatusEmitsAreIdleForTest);
    }

    [Fact]
    public async Task Report_that_changes_nothing_fires_no_event()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(Report(ProgramStatusState.Working, "a"));
        fixture.Runtime.Signal(Report(ProgramStatusState.Working, "a"));
        fixture.Runtime.Signal(Report(ProgramStatusState.Done, "a"));
        await fixture.Sink.WaitForAsync(EventType, p => p.GetProperty("program_status")[0].GetProperty("state").GetString() == "done");
        Assert.Equal(2, fixture.Sink.Count(EventType));
    }

    [Fact]
    public async Task Process_exit_drops_working()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(Report(ProgramStatusState.Working, "a"));
        await fixture.Sink.WaitForTypeAsync(EventType);
        fixture.Runtime.RaiseExit(0);
        await fixture.Sink.WaitForAsync(EventType, p => p.GetProperty("program_status").GetArrayLength() == 0);
        Assert.Empty((await fixture.GetPaneAsync()).GetProperty("program_status").EnumerateArray());
    }

    [Fact]
    public async Task Process_exit_keeps_done_and_fires_no_event()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await NewFixtureAsync(clock);
        fixture.Runtime.Signal(Report(ProgramStatusState.Done, "a"));
        await fixture.Sink.WaitForTypeAsync(EventType);
        fixture.Runtime.RaiseExit(0);
        clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.SettleAsync();
        Assert.Equal(1, fixture.Sink.Count(EventType));
        Assert.Single((await fixture.GetPaneAsync()).GetProperty("program_status").EnumerateArray());
    }

    [Fact]
    public async Task Process_exit_drops_blocked()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(Report(ProgramStatusState.Blocked, "a", kind: ProgramStatusKind.Question));
        await fixture.Sink.WaitForTypeAsync(EventType);
        fixture.Runtime.RaiseExit(1);
        await fixture.Sink.WaitForAsync(EventType, p => p.GetProperty("program_status").GetArrayLength() == 0);
    }

    [Fact]
    public async Task Reset_drops_every_record_and_fires_an_event()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(Report(ProgramStatusState.Done, "a"));
        await fixture.Sink.WaitForTypeAsync(EventType);
        fixture.Runtime.Signal(ProgramStatusSignal.Reset);
        await fixture.Sink.WaitForAsync(EventType, p => p.GetProperty("program_status").GetArrayLength() == 0);
        Assert.Empty((await fixture.GetPaneAsync()).GetProperty("program_status").EnumerateArray());
    }

    [Fact]
    public async Task Signals_of_one_chunk_apply_in_order_and_fire_one_event()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await NewFixtureAsync(clock);
        fixture.Runtime.Signal(
            Report(ProgramStatusState.Working, "a"),
            ProgramStatusSignal.Reset,
            Report(ProgramStatusState.Idle, "b"));
        await fixture.Sink.WaitForTypeAsync(EventType);
        clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.SettleAsync();
        Assert.Equal(1, fixture.Sink.Count(EventType));
        var rec = Assert.Single((await fixture.GetPaneAsync()).GetProperty("program_status").EnumerateArray());
        Assert.Equal("idle", rec.GetProperty("state").GetString());
    }

    private const string MuseWorking =
        "⟩ hello\n\n◆ Working (0s · esc to interrupt)\n\n────────────────\n⟩\n────────────────\ngpt-5.4 · minimal · /workspace";

    private static ProgramStatusSignal ReportOf(
        ProgramStatusState state, string? id = null, string? msg = null, string? app = null) =>
        ProgramStatusSignal.FromReport(new ProgramStatusReport { State = state, Id = id, Msg = msg, App = app });

    private static async Task WaitStateAsync(Fixture fixture, string state)
    {
        // Two minutes of 15 ms polls. A hang guard only: the outcome never depends on it.
        for (var i = 0; i < 8000; i++)
        {
            if (await fixture.StateAsync() == state)
                return;
            await Task.Delay(15);
        }

        Assert.Equal(state, await fixture.StateAsync());
    }

    [Fact]
    public async Task Blocked_report_sets_pane_status_and_message_and_the_detector_does_not_overwrite_it()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Blocked, msg: "Password required", app: "brew"));
        await WaitStateAsync(fixture, "blocked");
        var pane = await fixture.GetPaneAsync();
        Assert.Equal("Password required", pane.GetProperty("message").GetString());
        Assert.Equal("brew", pane.GetProperty("agent").GetString());

        fixture.Runtime.DetectionText = MuseWorking;
        fixture.Cp.ScanDetectionNowForTests(fixture.PaneId);
        Assert.Equal("blocked", await fixture.StateAsync());
    }

    [Fact]
    public async Task Status_change_from_a_record_fires_pane_agent_status_changed()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Blocked, msg: "x"));
        var changed = await fixture.Sink.WaitForAsync(
            ProtocolEventTypes.PaneAgentStatusChanged,
            p => p.GetProperty("agent_status").GetString() == "blocked");
        Assert.Equal("x", changed.GetProperty("params").GetProperty("payload").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Root_working_and_child_blocked_give_blocked()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Working, msg: "root", app: "deploy"));
        await WaitStateAsync(fixture, "working");
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Blocked, id: "eu", msg: "Approve?"));
        await WaitStateAsync(fixture, "blocked");
        var pane = await fixture.GetPaneAsync();
        Assert.Equal("Approve?", pane.GetProperty("message").GetString());
        Assert.Equal("deploy", pane.GetProperty("agent").GetString());
    }

    [Fact]
    public async Task Error_report_gives_the_error_status_and_agent_wait_until_error_returns()
    {
        await using var fixture = await NewFixtureAsync();
        var pollingSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Cp.AgentWaitRegisteredForTests = _ => pollingSignal.TrySetResult();
        var polling = pollingSignal.Task;
        var wait = fixture.Call(
            ProtocolMethods.AgentWait,
            new JsonObject { ["pane_id"] = fixture.PaneId, ["until"] = "error", ["timeout_ms"] = 120_000 });
        await polling.WaitAsync(Poll.HangGuard);
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Error, msg: "rsync failed"));
        var result = await wait;
        Assert.False(result.GetProperty("wait").GetProperty("timed_out").GetBoolean());
        Assert.Equal("error", result.GetProperty("state").GetString());
        await WaitStateAsync(fixture, "error");
    }

    [Fact]
    public async Task Agent_wait_until_blocked_returns_when_a_pane_reports_blocked()
    {
        await using var fixture = await NewFixtureAsync();
        var pollingSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Cp.AgentWaitRegisteredForTests = _ => pollingSignal.TrySetResult();
        var polling = pollingSignal.Task;
        var wait = fixture.Call(
            ProtocolMethods.AgentWait,
            new JsonObject { ["pane_id"] = fixture.PaneId, ["until"] = "blocked", ["timeout_ms"] = 120_000 });
        await polling.WaitAsync(Poll.HangGuard);
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Blocked, msg: "x"));
        var result = await wait;
        Assert.False(result.GetProperty("wait").GetProperty("timed_out").GetBoolean());
        Assert.Equal("blocked", result.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Clear_of_the_last_record_gives_the_status_back_to_the_detector()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Blocked, msg: "x"));
        await WaitStateAsync(fixture, "blocked");
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Clear));
        await WaitStateAsync(fixture, "unknown");

        fixture.Runtime.DetectionText = MuseWorking;
        fixture.Cp.ScanDetectionNowForTests(fixture.PaneId);
        Assert.Equal("working", await fixture.StateAsync());
    }

    [Fact]
    public async Task Pane_with_report_agent_authority_keeps_it_when_a_record_arrives_and_gets_records_back_on_release()
    {
        await using var fixture = await NewFixtureAsync();
        await fixture.Call(ProtocolMethods.PaneReportAgent, new JsonObject
        {
            ["pane_id"] = fixture.PaneId,
            ["source"] = "hook",
            ["agent"] = "claude",
            ["state"] = "idle",
        });
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Blocked, msg: "x"));
        await fixture.Sink.WaitForTypeAsync(EventType);
        Assert.Equal("idle", await fixture.StateAsync());
        Assert.Single((await fixture.GetPaneAsync()).GetProperty("program_status").EnumerateArray());

        await fixture.Call(ProtocolMethods.PaneReleaseAgent, new JsonObject
        {
            ["pane_id"] = fixture.PaneId,
            ["source"] = "hook",
            ["agent"] = "claude",
        });
        await WaitStateAsync(fixture, "blocked");
    }

    [Fact]
    public async Task Report_agent_replaces_a_program_status_authority()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Blocked, msg: "x"));
        await WaitStateAsync(fixture, "blocked");
        await fixture.Call(ProtocolMethods.PaneReportAgent, new JsonObject
        {
            ["pane_id"] = fixture.PaneId,
            ["source"] = "hook",
            ["agent"] = "claude",
            ["state"] = "working",
        });
        Assert.Equal("working", await fixture.StateAsync());
    }

    [Fact]
    public async Task Report_agent_may_not_use_the_reserved_source()
    {
        await using var fixture = await NewFixtureAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Call(ProtocolMethods.PaneReportAgent, new JsonObject
        {
            ["pane_id"] = fixture.PaneId,
            ["source"] = "osc7501",
            ["agent"] = "claude",
            ["state"] = "working",
        }));
    }

    [Fact]
    public async Task Process_exit_drops_a_running_record_and_the_authority_but_keeps_a_done_status()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Done, id: "a", msg: "ok"));
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Working, id: "b"));
        await WaitStateAsync(fixture, "working");
        fixture.Runtime.RaiseExit(0);
        await WaitStateAsync(fixture, "done");
        Assert.Equal("ok", (await fixture.GetPaneAsync()).GetProperty("message").GetString());
    }

    [Fact]
    public async Task Prompt_start_drops_a_working_record_and_the_pane_status_follows()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Working, msg: "x"));
        await WaitStateAsync(fixture, "working");
        fixture.Runtime.Signal(ProgramStatusSignal.PromptStart);
        await fixture.Sink.WaitForAsync(EventType, p => p.GetProperty("program_status").GetArrayLength() == 0);
        await WaitStateAsync(fixture, "unknown");
        Assert.Empty((await fixture.GetPaneAsync()).GetProperty("program_status").EnumerateArray());
    }

    [Fact]
    public async Task Prompt_start_keeps_a_done_record_and_drops_blocked()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Done, id: "a", msg: "ok"));
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Blocked, id: "b", msg: "q"));
        await WaitStateAsync(fixture, "blocked");
        fixture.Runtime.Signal(ProgramStatusSignal.PromptStart);
        await WaitStateAsync(fixture, "done");
        var rec = Assert.Single((await fixture.GetPaneAsync()).GetProperty("program_status").EnumerateArray());
        Assert.Equal("done", rec.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Prompt_start_in_stream_order_drops_only_the_earlier_report()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(
            ReportOf(ProgramStatusState.Working, id: "a"),
            ProgramStatusSignal.PromptStart,
            ReportOf(ProgramStatusState.Working, id: "b"));
        await WaitStateAsync(fixture, "working");
        var rec = Assert.Single((await fixture.GetPaneAsync()).GetProperty("program_status").EnumerateArray());
        Assert.Equal("b", rec.GetProperty("id").GetString());

        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Working, id: "c"), ProgramStatusSignal.PromptStart);
        await WaitStateAsync(fixture, "unknown");
    }

    [Fact]
    public async Task Process_exit_emits_both_the_status_event_and_the_program_status_event()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Working, msg: "x"));
        await WaitStateAsync(fixture, "working");
        fixture.Runtime.RaiseExit(0);
        await fixture.Sink.WaitForAsync(EventType, p => p.GetProperty("program_status").GetArrayLength() == 0);
        await fixture.Sink.WaitForAsync(
            ProtocolEventTypes.PaneAgentStatusChanged,
            p => p.GetProperty("agent_status").GetString() == "done"
                && p.GetProperty("previous_status").GetString() == "working");
    }

    [Fact]
    public async Task Occupant_replace_clears_records_and_emits_the_program_status_event()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Blocked, msg: "x"));
        await fixture.Sink.WaitForTypeAsync(EventType);
        await fixture.Call(ProtocolMethods.AgentStart, new JsonObject
        {
            ["pane_id"] = fixture.PaneId,
            ["command"] = "/bin/true",
        });
        await fixture.Sink.WaitForAsync(EventType, p => p.GetProperty("program_status").GetArrayLength() == 0);
        Assert.Empty((await fixture.GetPaneAsync()).GetProperty("program_status").EnumerateArray());
    }

    [Fact]
    public async Task Blocked_report_publishes_one_attention_item_with_the_source_and_the_display_text()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(ProgramStatusSignal.FromReport(new ProgramStatusReport
        {
            State = ProgramStatusState.Blocked,
            Kind = ProgramStatusKind.Permission,
            App = "brew",
            Msg = "Allow \u202Eit?",
        }));

        var item = (await fixture.Sink.WaitForTypeAsync(ProtocolEventTypes.PaneProgramAttention))
            .GetProperty("params").GetProperty("payload");
        var pane = await fixture.GetPaneAsync();
        Assert.Equal(fixture.PaneId, item.GetProperty("pane_id").GetString());
        Assert.Equal(pane.GetProperty("tab_id").GetString(), item.GetProperty("tab_id").GetString());
        Assert.Equal(pane.GetProperty("workspace_id").GetString(), item.GetProperty("workspace_id").GetString());
        var status = (await fixture.Sink.WaitForAsync(
                ProtocolEventTypes.PaneAgentStatusChanged,
                p => p.GetProperty("agent_status").GetString() == "blocked"))
            .GetProperty("params").GetProperty("payload");
        Assert.Equal(status.GetProperty("occupant_generation").GetInt32(), item.GetProperty("occupant_generation").GetInt32());
        Assert.Equal("blocked", item.GetProperty("state").GetString());
        Assert.Equal("permission", item.GetProperty("kind").GetString());
        Assert.Equal("brew", item.GetProperty("app").GetString());
        Assert.Equal("Allow it?", item.GetProperty("msg").GetString());
        Assert.Equal("high", item.GetProperty("priority").GetString());
        Assert.Equal(
            pane.GetProperty("program_status_view").GetProperty("source").GetRawText(),
            item.GetProperty("source").GetRawText());
    }

    [Fact]
    public async Task The_status_event_of_a_record_names_its_source_so_a_frontend_does_not_notify_twice()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Blocked, msg: "x"));
        var changed = await fixture.Sink.WaitForAsync(
            ProtocolEventTypes.PaneAgentStatusChanged,
            p => p.GetProperty("agent_status").GetString() == "blocked");
        Assert.Equal("osc7501", changed.GetProperty("params").GetProperty("payload").GetProperty("source").GetString());
    }

    [Fact]
    public async Task A_pending_attention_item_is_dropped_when_a_report_takes_the_status()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await NewFixtureAsync(clock);
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Blocked, msg: "ask"));
        await fixture.Sink.WaitForTypeAsync(ProtocolEventTypes.PaneProgramAttention);
        // The pane limit holds this item back.
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Done, msg: "finished"));
        await WaitStateAsync(fixture, "done");

        await fixture.Call(ProtocolMethods.PaneReportAgent, new JsonObject
        {
            ["pane_id"] = fixture.PaneId,
            ["source"] = "hook",
            ["agent"] = "claude",
            ["state"] = "working",
        });
        clock.Advance(TimeSpan.FromMinutes(1));
        // Any later change lets the policy release what it still holds.
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Working, id: "other", msg: "later"));
        await fixture.WaitForAfterAdvancingAsync(
            clock,
            p => p.GetProperty("program_status").GetArrayLength() == 2,
            EventType);
        clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.SettleAsync();

        Assert.Equal(1, fixture.Sink.Count(ProtocolEventTypes.PaneProgramAttention));
    }

    [Fact]
    public async Task Records_that_get_the_status_back_on_release_publish_an_attention_item_and_the_result_is_unseen()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await NewFixtureAsync(clock);
        await fixture.Call(ProtocolMethods.PaneReportAgent, new JsonObject
        {
            ["pane_id"] = fixture.PaneId,
            ["source"] = "hook",
            ["agent"] = "claude",
            ["state"] = "idle",
        });
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Done, msg: "finished"));
        await fixture.Sink.WaitForTypeAsync(EventType);
        clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.SettleAsync();
        Assert.Equal(0, fixture.Sink.Count(ProtocolEventTypes.PaneProgramAttention));

        await fixture.Call(ProtocolMethods.PaneReleaseAgent, new JsonObject
        {
            ["pane_id"] = fixture.PaneId,
            ["source"] = "hook",
            ["agent"] = "claude",
        });

        var item = (await fixture.Sink.WaitForTypeAsync(ProtocolEventTypes.PaneProgramAttention))
            .GetProperty("params").GetProperty("payload");
        Assert.Equal("done", item.GetProperty("state").GetString());
        var pane = await fixture.GetPaneAsync();
        Assert.False(pane.GetProperty("seen").GetBoolean());
        Assert.False(pane.GetProperty("program_status_view").GetProperty("seen").GetBoolean());
    }

    [Fact]
    public async Task Clear_of_the_last_record_gives_back_the_kind_the_pane_had_before_the_records()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.DetectionText = MuseWorking;
        fixture.Cp.ScanDetectionNowForTests(fixture.PaneId);
        Assert.Equal("working", await fixture.StateAsync());
        var kindBefore = (await fixture.GetPaneAsync()).GetProperty("agent").GetString();
        Assert.False(string.IsNullOrEmpty(kindBefore));

        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Blocked, msg: "x", app: "brew"));
        await WaitStateAsync(fixture, "blocked");
        Assert.Equal("brew", (await fixture.GetPaneAsync()).GetProperty("agent").GetString());
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Clear));
        await WaitStateAsync(fixture, "unknown");

        var pane = await fixture.GetPaneAsync();
        Assert.Equal(kindBefore, pane.GetProperty("agent").GetString());
        Assert.Equal(JsonValueKind.Null, pane.GetProperty("message").ValueKind);
    }

    [Fact]
    public async Task Fifty_changes_in_a_burst_publish_one_attention_item()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await NewFixtureAsync(clock);
        for (var i = 0; i < 50; i++)
            fixture.Runtime.Signal(ReportOf(ProgramStatusState.Blocked, msg: "ask " + i));
        await WaitStateAsync(fixture, "blocked");
        // The clock moves only to the due time of the program status emit timer, one interval
        // at most, so it stays far inside the ten second attention window.
        await fixture.WaitForAfterAdvancingAsync(
            clock,
            p => p.GetProperty("program_status")[0].GetProperty("msg").GetString() == "ask 49",
            EventType,
            ProgramStatusEmitCoalescer.DefaultMinInterval);
        await fixture.SettleAsync();

        var item = (await fixture.Sink.WaitForTypeAsync(ProtocolEventTypes.PaneProgramAttention))
            .GetProperty("params").GetProperty("payload");
        Assert.Equal(1, fixture.Sink.Count(ProtocolEventTypes.PaneProgramAttention));
        Assert.Equal("ask 0", item.GetProperty("msg").GetString());
    }

    [Fact]
    public async Task Working_report_publishes_no_attention_item()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await NewFixtureAsync(clock);
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Working, msg: "x"));
        await fixture.Sink.WaitForTypeAsync(EventType);
        clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.SettleAsync();
        Assert.Equal(0, fixture.Sink.Count(ProtocolEventTypes.PaneProgramAttention));
    }

    [Fact]
    public async Task A_status_held_by_report_agent_publishes_no_attention_item()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await NewFixtureAsync(clock);
        await fixture.Call(ProtocolMethods.PaneReportAgent, new JsonObject
        {
            ["pane_id"] = fixture.PaneId,
            ["source"] = "hook",
            ["agent"] = "claude",
            ["state"] = "idle",
        });
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Blocked, msg: "x"));
        await fixture.Sink.WaitForTypeAsync(EventType);
        clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.SettleAsync();
        Assert.Equal(0, fixture.Sink.Count(ProtocolEventTypes.PaneProgramAttention));
    }

    [Fact]
    public async Task Records_survive_a_mux_restart_and_show_in_the_snapshot()
    {
        var fixture = await NewFixtureAsync();
        var paneId = fixture.PaneId;
        fixture.Runtime.Signal(Report(ProgramStatusState.Done, "All good", "brew"));
        await fixture.Sink.WaitForTypeAsync(EventType);
        await fixture.DisposeAsync();

        // A new process: load, reconcile, replace, then start a shell in each saved pane.
        await using var store = new SqliteRuntimeSessionStore(_paths);
        var load = await store.TryLoadAsync("program-status");
        Assert.True(load.IsOk);
        var reconciled = SessionGraphRestorer.ReconcileDeadPids(load.Value!, new DeadProbe());
        var restored = new AppState(reconciled.Id);
        restored.Replace(reconciled);
        var factory = new Factory();
        var cp = new ControlPlaneService(
            restored,
            factory,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store);
        try
        {
            await cp.RestoreSpawnAsync(CancellationToken.None);

            var pane = await cp.DispatchAsync(
                ProtocolMethods.PaneGet,
                JsonDocument.Parse(new JsonObject { ["pane_id"] = paneId }.ToJsonString()).RootElement,
                CancellationToken.None);
            var record = Assert.Single(pane.GetProperty("program_status").EnumerateArray());
            Assert.Equal("done", record.GetProperty("state").GetString());
            Assert.Equal("All good", pane.GetProperty("program_status_view").GetProperty("msg").GetString());

            var snapshot = await cp.DispatchAsync(
                ProtocolMethods.SessionSnapshot, JsonDocument.Parse("{}").RootElement, CancellationToken.None);
            var inSnapshot = Assert.Single(snapshot.GetProperty("agent_view").GetProperty("panes").EnumerateArray());
            Assert.Equal(paneId, inSnapshot.GetProperty("pane_id").GetString());
            Assert.Equal("All good", inSnapshot.GetProperty("msg").GetString());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private sealed class DeadProbe : IProcessLivenessProbe
    {
        public bool IsAlive(int pid) => false;
    }

    [Fact]
    public async Task A_done_is_unseen_until_the_person_focuses_the_pane_then_the_tab_and_session_views_drop_it()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Done, msg: "Finished"));

        var unseen = (await fixture.Sink.WaitForAsync(EventType, p => p.GetProperty("program_status").GetArrayLength() == 1))
            .GetProperty("params").GetProperty("payload");
        Assert.False(unseen.GetProperty("view").GetProperty("seen").GetBoolean());
        Assert.Equal("done", unseen.GetProperty("tab_view").GetProperty("state").GetString());
        Assert.Equal("done", unseen.GetProperty("session_view").GetProperty("state").GetString());

        await fixture.Call(ProtocolMethods.PaneFocus, new JsonObject { ["pane_id"] = fixture.PaneId });

        var seen = (await fixture.Sink.WaitForAsync(
                EventType,
                p => p.GetProperty("view").ValueKind == JsonValueKind.Object
                    && p.GetProperty("view").GetProperty("seen").GetBoolean()))
            .GetProperty("params").GetProperty("payload");
        // The pane view still shows the state. The records do not change.
        Assert.Equal("done", seen.GetProperty("view").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("tab_view").ValueKind);
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("session_view").ValueKind);
        var pane = await fixture.GetPaneAsync();
        Assert.Equal("done", Assert.Single(pane.GetProperty("program_status").EnumerateArray()).GetProperty("state").GetString());
        Assert.True(pane.GetProperty("program_status_view").GetProperty("seen").GetBoolean());
    }

    [Fact]
    public async Task An_error_is_unseen_on_arrival_even_for_a_pane_that_was_seen()
    {
        await using var fixture = await NewFixtureAsync();
        await fixture.Call(ProtocolMethods.PaneFocus, new JsonObject { ["pane_id"] = fixture.PaneId });
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Error, msg: "rsync failed"));

        var payload = (await fixture.Sink.WaitForAsync(EventType, p => p.GetProperty("program_status").GetArrayLength() == 1))
            .GetProperty("params").GetProperty("payload");
        Assert.False(payload.GetProperty("view").GetProperty("seen").GetBoolean());
        Assert.Equal("error", payload.GetProperty("tab_view").GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_blocked_pane_is_never_hidden_by_seen()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Blocked, msg: "Approve?"));
        await WaitStateAsync(fixture, "blocked");
        await fixture.Call(ProtocolMethods.PaneFocus, new JsonObject { ["pane_id"] = fixture.PaneId });

        var tabs = await fixture.Call(ProtocolMethods.TabList, new JsonObject());
        Assert.Equal(
            "blocked",
            tabs.EnumerateArray().First().GetProperty("program_status_view").GetProperty("state").GetString());
        var pane = await fixture.GetPaneAsync();
        Assert.False(pane.GetProperty("program_status_view").GetProperty("seen").GetBoolean());
    }

    [Fact]
    public async Task A_pane_rename_publishes_the_views_again_with_the_new_label()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Working, msg: "Building"));
        await fixture.Sink.WaitForAsync(EventType, p => p.GetProperty("program_status").GetArrayLength() == 1);

        await fixture.Call(
            ProtocolMethods.PaneRename,
            new JsonObject { ["pane_id"] = fixture.PaneId, ["label"] = "renamed pane" });

        var payload = (await fixture.Sink.WaitForAsync(
                EventType,
                p => p.GetProperty("session_view").ValueKind == JsonValueKind.Object
                    && p.GetProperty("session_view").GetProperty("msg").GetString() == "renamed pane: Building"))
            .GetProperty("params").GetProperty("payload");
        Assert.Equal("renamed pane", payload.GetProperty("view").GetProperty("source").GetProperty("pane").GetString());
    }

    [Fact]
    public async Task A_tab_rename_and_a_workspace_rename_publish_the_views_again()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Working, msg: "Building"));
        var first = (await fixture.Sink.WaitForAsync(EventType, p => p.GetProperty("program_status").GetArrayLength() == 1))
            .GetProperty("params").GetProperty("payload");

        await fixture.Call(
            ProtocolMethods.TabRename,
            new JsonObject { ["tab_id"] = first.GetProperty("tab_id").GetString(), ["label"] = "renamed tab" });
        await fixture.Sink.WaitForAsync(
            EventType,
            p => p.GetProperty("view").ValueKind == JsonValueKind.Object
                && p.GetProperty("view").GetProperty("source").GetProperty("tab").GetString() == "renamed tab");

        await fixture.Call(
            ProtocolMethods.WorkspaceRename,
            new JsonObject { ["workspace_id"] = first.GetProperty("workspace_id").GetString(), ["label"] = "renamed space" });
        await fixture.Sink.WaitForAsync(
            EventType,
            p => p.GetProperty("view").ValueKind == JsonValueKind.Object
                && p.GetProperty("view").GetProperty("source").GetProperty("workspace").GetString() == "renamed space");
    }

    [Fact]
    public async Task Mark_seen_marks_the_pane_without_a_focus_change_and_drops_the_done_from_the_summary()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Done, msg: "Finished"));
        await fixture.Sink.WaitForAsync(EventType, p => p.GetProperty("session_view").ValueKind == JsonValueKind.Object);
        var focusedBefore = (await fixture.Call(ProtocolMethods.PaneCurrent, new JsonObject())).GetProperty("pane_id").GetString();

        var result = await fixture.Call(
            ProtocolMethods.PaneMarkSeen,
            new JsonObject { ["pane_ids"] = new JsonArray(fixture.PaneId, "p_missing") });

        Assert.Equal(fixture.PaneId, Assert.Single(result.GetProperty("marked").EnumerateArray()).GetString());
        var payload = (await fixture.Sink.WaitForAsync(
                EventType,
                p => p.GetProperty("view").ValueKind == JsonValueKind.Object
                    && p.GetProperty("view").GetProperty("seen").GetBoolean()))
            .GetProperty("params").GetProperty("payload");
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("tab_view").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("session_view").ValueKind);
        Assert.Equal(
            focusedBefore,
            (await fixture.Call(ProtocolMethods.PaneCurrent, new JsonObject())).GetProperty("pane_id").GetString());
    }

    [Fact]
    public async Task Mark_seen_is_idempotent()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Done, msg: "Finished"));
        await fixture.Sink.WaitForAsync(EventType, p => p.GetProperty("program_status").GetArrayLength() == 1);
        var args = new JsonObject { ["pane_ids"] = new JsonArray(fixture.PaneId) };

        await fixture.Call(ProtocolMethods.PaneMarkSeen, args);
        await fixture.Sink.WaitForAsync(EventType, p => p.GetProperty("view").GetProperty("seen").GetBoolean());
        var again = await fixture.Call(ProtocolMethods.PaneMarkSeen, args);

        Assert.Equal(0, again.GetProperty("marked").GetArrayLength());
    }

    [Fact]
    public async Task A_new_done_result_is_unseen_after_a_seen_one_and_the_same_record_is_not_new()
    {
        await using var fixture = await NewFixtureAsync();
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Done, msg: "first"));
        await fixture.Sink.WaitForAsync(EventType, p => p.GetProperty("program_status").GetArrayLength() == 1);
        await fixture.Call(ProtocolMethods.PaneMarkSeen, new JsonObject { ["pane_ids"] = new JsonArray(fixture.PaneId) });
        await fixture.Sink.WaitForAsync(EventType, p => p.GetProperty("view").GetProperty("seen").GetBoolean());

        // The same record again changes nothing: it stays seen.
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Done, msg: "first"));
        await fixture.SettleAsync();
        Assert.True((await fixture.GetPaneAsync()).GetProperty("program_status_view").GetProperty("seen").GetBoolean());

        // A different result is unseen, also when the status stays done.
        fixture.Runtime.Signal(ReportOf(ProgramStatusState.Done, msg: "second"));
        var payload = (await fixture.Sink.WaitForAsync(
                EventType,
                p => p.GetProperty("view").GetProperty("msg").GetString() == "second"))
            .GetProperty("params").GetProperty("payload");
        Assert.False(payload.GetProperty("view").GetProperty("seen").GetBoolean());
        Assert.Equal("done", payload.GetProperty("tab_view").GetProperty("state").GetString());
        Assert.Equal("done", payload.GetProperty("session_view").GetProperty("state").GetString());
    }

    private async Task<Fixture> NewFixtureAsync(TimeProvider? time = null)
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        var state = new AppState(SessionId.New("program-status"));
        state.UpdateSession(s => s with { Name = "program-status", LifecycleState = SessionLifecycle.Ready });
        var store = new SqliteRuntimeSessionStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
        var manifests = new SqliteJournalManifestStore(_paths);
        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);
        var factory = new Factory();
        var cp = new ControlPlaneService(
            state,
            factory,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: new EventSubscriptionHub(),
            timeProvider: time);

        var sink = new Sink("c_program_status");
        var sub = await cp.DispatchAsync(
            ProtocolMethods.EventsSubscribe,
            JsonDocument.Parse(new JsonObject
            {
                ["from_seq"] = 0,
                ["types"] = new JsonArray(
                    EventType,
                    ProtocolEventTypes.PaneAgentStatusChanged,
                    ProtocolEventTypes.PaneProgramAttention),
                ["live"] = true,
            }.ToJsonString()).RootElement,
            sink,
            CancellationToken.None);
        await cp.CompleteEventsSubscribeAsync(
            sub.GetProperty("subscription_id").GetString()!,
            [],
            sink,
            CancellationToken.None);

        var ws = await cp.DispatchAsync(
            ProtocolMethods.WorkspaceCreate,
            JsonDocument.Parse(new JsonObject
            {
                ["cwd"] = Path.GetTempPath(),
                ["create_pane"] = true,
                ["command"] = "muse",
            }.ToJsonString()).RootElement,
            CancellationToken.None);
        var paneId = ws.GetProperty("pane").GetProperty("pane_id").GetString()!;
        return new Fixture(cp, sink, factory.Created.Single(r => r.Id.Value == paneId), paneId, factory);
    }

    private sealed class Fixture(ControlPlaneService cp, Sink sink, FakeRuntime runtime, string paneId, Factory factory) : IAsyncDisposable
    {
        public Factory Factory { get; } = factory;
        public Sink Sink { get; } = sink;
        public FakeRuntime Runtime { get; } = runtime;
        public string PaneId { get; } = paneId;

        public ControlPlaneService Cp => cp;

        public Task<JsonElement> Call(string method, JsonObject args) =>
            cp.DispatchAsync(method, JsonDocument.Parse(args.ToJsonString()).RootElement, CancellationToken.None);

        public async Task<string?> StateAsync() => (await GetPaneAsync()).GetProperty("state").GetString();

        public Task<JsonElement> GetPaneAsync() =>
            cp.DispatchAsync(
                ProtocolMethods.PaneGet,
                JsonDocument.Parse(new JsonObject { ["pane_id"] = PaneId }.ToJsonString()).RootElement,
                CancellationToken.None);

        // No emit is pending and every started emit reached the sink.
        public async Task SettleAsync()
        {
            await Poll.UntilAsync(() => cp.ProgramStatusEmitsAreIdleForTest, "the program status emits to end");
            await cp.WaitReliableEmitsQuiescentForTestsAsync();
        }

        // The fake clock moves only here, and only to the due time of an armed timer that is
        // at most one step away. The number of steps follows the timers, not the wall time:
        // an emit still in flight arms no timer, so the clock waits for it.
        public async Task<JsonElement> WaitForAfterAdvancingAsync(
            ManualTimeProvider clock,
            Func<JsonElement, bool> payloadMatch,
            string? type = null,
            TimeSpan? step = null)
        {
            var limit = step ?? TimeSpan.FromMilliseconds(150);
            var wait = Sink.WaitForAsync(type ?? EventType, payloadMatch);
            while (!wait.IsCompleted)
            {
                var next = clock.NextDue;
                var gap = next is { } due ? due - clock.GetUtcNow() : TimeSpan.MaxValue;
                if (gap <= limit)
                    clock.Advance(gap > TimeSpan.Zero ? gap : TimeSpan.Zero);
                else
                    await Task.Delay(1);
            }

            return await wait;
        }

        public async ValueTask DisposeAsync() => await cp.ShutdownAsync(CancellationToken.None);
    }

    private sealed class Factory : IPaneRuntimeFactory
    {
        public List<FakeRuntime> Created { get; } = [];

        public IPaneRuntime Create(PaneSpawnOptions options)
        {
            var runtime = new FakeRuntime(options.Id);
            Created.Add(runtime);
            return runtime;
        }
    }

    private sealed class FakeRuntime(PaneId id) : IPaneRuntime
    {
        public PaneId Id { get; } = id;
        public bool IsAlive { get; private set; } = true;
        public int? ExitCode { get; private set; }
        public int? Pid { get; } = 42_300;
#pragma warning disable CS0067
        public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
        public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
        public event Action<IPaneRuntime, int>? Exited;
#pragma warning restore CS0067
        public event Action<IPaneRuntime, IReadOnlyList<ProgramStatusSignal>>? ProgramStatusSignaled;

        public string DetectionText { get; set; } = "";

        public void Signal(params ProgramStatusSignal[] signals) => ProgramStatusSignaled?.Invoke(this, signals);

        public void RaiseExit(int code)
        {
            IsAlive = false;
            ExitCode = code;
            Exited?.Invoke(this, code);
        }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask WriteTextAsync(string text, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct) => ValueTask.CompletedTask;
        public string ReadVisibleText() => DetectionText;
        public string ReadRecentText(int maxLines) => DetectionText;
        public string ReadRecentUnwrappedText(int maxLines) => DetectionText;
        public string ReadDetectionText() => DetectionText;

        public ValueTask DisposeAsync()
        {
            IsAlive = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SteppedClock : TimeProvider
    {
        private TimeSpan _offset;

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + _offset;

        public void Advance(TimeSpan by) => _offset += by;
    }

    private sealed class Sink(string id) : IClientConnection
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];

        public string ConnectionId { get; } = id;

        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default)
        {
            lock (_gate)
                _lines.Add(jsonLine);
            return Task.CompletedTask;
        }

        public int Count(string type)
        {
            lock (_gate)
                return Matching(type).Count;
        }

        public Task<JsonElement> WaitForTypeAsync(string type) => WaitForAsync(type, _ => true);

        public async Task<JsonElement> WaitForAsync(string type, Func<JsonElement, bool> payloadMatch)
        {
            // A hang guard only: the outcome never depends on it.
            var deadline = DateTime.UtcNow.Add(Poll.HangGuard);
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    foreach (var element in Matching(type))
                    {
                        if (payloadMatch(element.GetProperty("params").GetProperty("payload")))
                            return element;
                    }
                }

                await Task.Delay(15).ConfigureAwait(false);
            }

            throw new TimeoutException("Did not observe " + type);
        }

        private List<JsonElement> Matching(string type)
        {
            var found = new List<JsonElement>();
            foreach (var line in _lines)
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("params", out var p)
                    && p.TryGetProperty("type", out var t)
                    && t.GetString() == type)
                {
                    found.Add(doc.RootElement.Clone());
                }
            }

            return found;
        }
    }
}
