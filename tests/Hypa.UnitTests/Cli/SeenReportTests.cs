using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.ProgramStatus;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The attach tells the mux which panes the person can see. The selector decides which panes.
/// The attach sends one call for a batch, and no call when nothing is pending.
/// </summary>
public sealed class SeenReportTests
{
    // ---- selector ----

    [Fact]
    public void An_unseen_done_on_the_tab_in_view_is_pending()
    {
        var selector = new SeenReportSelector();

        selector.NotePane("p1", "t1", "done", seen: false, tabInView: "t1");

        Assert.Equal(["p1"], selector.Take());
    }

    [Fact]
    public void An_unseen_error_on_the_tab_in_view_is_pending()
    {
        var selector = new SeenReportSelector();

        selector.NotePane("p1", "t1", "error", seen: false, tabInView: "t1");

        Assert.Equal(["p1"], selector.Take());
    }

    [Fact]
    public void An_unseen_done_on_another_tab_waits_until_that_tab_is_in_view()
    {
        var selector = new SeenReportSelector();
        selector.NotePane("p1", "t2", "done", seen: false, tabInView: "t1");
        selector.NotePane("p2", "t2", "error", seen: false, tabInView: "t1");
        selector.NotePane("p3", "t1", "working", seen: false, tabInView: "t1");

        Assert.Empty(selector.Take());

        selector.NoteTabInView("t2");

        Assert.Equal(["p1", "p2"], selector.Take());
    }

    [Theory]
    [InlineData("working")]
    [InlineData("blocked")]
    [InlineData("idle")]
    [InlineData(null)]
    public void Only_done_and_error_are_results(string? state)
    {
        var selector = new SeenReportSelector();

        selector.NotePane("p1", "t1", state, seen: false, tabInView: "t1");

        Assert.Empty(selector.Take());
    }

    [Fact]
    public void A_seen_result_is_not_pending_and_clears_an_earlier_one()
    {
        var selector = new SeenReportSelector();
        selector.NotePane("p1", "t2", "done", seen: false, tabInView: "t1");
        selector.NotePane("p1", "t2", "done", seen: true, tabInView: "t1");

        selector.NoteTabInView("t2");

        Assert.Empty(selector.Take());
    }

    [Fact]
    public void A_pane_that_changed_state_is_not_reported_when_its_tab_comes_in_view()
    {
        var selector = new SeenReportSelector();
        selector.NotePane("p1", "t2", "done", seen: false, tabInView: "t1");
        selector.NotePane("p1", "t2", "working", seen: false, tabInView: "t1");

        selector.NoteTabInView("t2");

        Assert.Empty(selector.Take());
    }

    [Fact]
    public void A_batch_is_taken_once_and_nothing_is_pending_after_it()
    {
        var selector = new SeenReportSelector();
        selector.NotePane("p2", "t1", "done", seen: false, tabInView: "t1");
        selector.NotePane("p1", "t1", "done", seen: false, tabInView: "t1");

        Assert.True(selector.HasPending);
        Assert.Equal(["p1", "p2"], selector.Take());
        Assert.False(selector.HasPending);
        Assert.Empty(selector.Take());
        selector.NoteTabInView("t1");
        Assert.Empty(selector.Take());
    }

    [Fact]
    public void A_failed_batch_comes_back()
    {
        var selector = new SeenReportSelector();
        selector.NotePane("p1", "t1", "done", seen: false, tabInView: "t1");
        _ = selector.Take();

        selector.RestoreLastBatch();

        Assert.Equal(["p1"], selector.Take());
    }

    [Fact]
    public void A_forgotten_pane_is_not_reported()
    {
        var selector = new SeenReportSelector();
        selector.NotePane("p1", "t2", "done", seen: false, tabInView: "t1");
        selector.Forget("p1");

        selector.NoteTabInView("t2");

        Assert.Empty(selector.Take());
    }

    // ---- attach ----

    private sealed class RecordingPort : IAttachCommandPort
    {
        public List<(string Method, JsonObject? Params)> Calls { get; } = [];

        public bool Fail { get; set; }

        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            Calls.Add((method, parameters));
            if (Fail)
                throw new Hypa.ControlPlane.ControlPlaneException(ProtocolErrorCodes.InvalidState, "no");
            return Task.FromResult(default(JsonElement));
        }
    }

    private static AttachLiveState Live(string tabInView)
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(new RecordingPort(), "w1", "t1", "p1", "lease"),
            ChromeEnabled = false,
            PaneId = "p1",
        };
        live.Notifications.SetFocus(tabInView, "p1");
        return live;
    }

    private static JsonElement ProgramEvent(string paneId, string tabId, ProgramStatusState state, bool seen)
    {
        var view = new ProgramStatusView
        {
            State = state,
            RecordCount = 1,
            Source = new ProgramStatusSourceLabel("w", "t", "p"),
            Seen = seen,
        };
        return JsonDocument.Parse(new JsonObject
        {
            ["event"] = ProtocolEventTypes.RuntimeEvent,
            ["params"] = new JsonObject
            {
                ["type"] = ProtocolEventTypes.PaneProgramStatusChanged,
                ["payload"] = new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["occupant_generation"] = 1,
                    ["tab_id"] = tabId,
                    ["workspace_id"] = "w1",
                    ["program_status"] = new JsonArray(),
                    ["view"] = ProgramStatusViewWire.ToNode(view),
                    ["tab_view"] = null,
                    ["session_view"] = null,
                },
            },
        }.ToJsonString()).RootElement.Clone();
    }

    private static JsonElement AgentEvent(string paneId, string tabId, string status, bool seen) =>
        JsonDocument.Parse(new JsonObject
        {
            ["event"] = ProtocolEventTypes.RuntimeEvent,
            ["params"] = new JsonObject
            {
                ["type"] = ProtocolEventTypes.PaneAgentStatusChanged,
                ["payload"] = new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["occupant_generation"] = 1,
                    ["tab_id"] = tabId,
                    ["agent_status"] = status,
                    ["seen"] = seen,
                },
            },
        }.ToJsonString()).RootElement.Clone();

    [Fact]
    public async Task An_unseen_done_event_for_the_tab_in_view_sends_one_call_with_the_pane()
    {
        var live = Live("t1");
        var port = new RecordingPort();

        AttachSession.ApplyNotifyEvent(live, ProgramEvent("p_2", "t1", ProgramStatusState.Done, seen: false), tty: null);
        await AttachSession.FlushSeenReportAsync(port, live, CancellationToken.None);
        await AttachSession.FlushSeenReportAsync(port, live, CancellationToken.None);

        var call = Assert.Single(port.Calls);
        Assert.Equal(ProtocolMethods.PaneMarkSeen, call.Method);
        Assert.Equal(["p_2"], call.Params!["pane_ids"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task An_agent_status_event_for_the_tab_in_view_is_reported_too()
    {
        var live = Live("t1");
        var port = new RecordingPort();

        AttachSession.ApplyNotifyEvent(live, AgentEvent("p_2", "t1", "error", seen: false), tty: null);
        await AttachSession.FlushSeenReportAsync(port, live, CancellationToken.None);

        Assert.Single(port.Calls);
    }

    [Fact]
    public async Task Steady_state_events_send_no_call()
    {
        var live = Live("t1");
        var port = new RecordingPort();

        AttachSession.ApplyNotifyEvent(live, ProgramEvent("p_2", "t1", ProgramStatusState.Working, seen: false), tty: null);
        AttachSession.ApplyNotifyEvent(live, ProgramEvent("p_3", "t1", ProgramStatusState.Done, seen: true), tty: null);
        AttachSession.ApplyNotifyEvent(live, ProgramEvent("p_4", "t2", ProgramStatusState.Done, seen: false), tty: null);
        AttachSession.ApplyNotifyEvent(live, AgentEvent("p_5", "t1", "working", seen: false), tty: null);
        await AttachSession.FlushSeenReportAsync(port, live, CancellationToken.None);

        Assert.Empty(port.Calls);
    }

    [Fact]
    public async Task A_done_on_another_tab_is_reported_when_that_tab_comes_in_view()
    {
        var live = Live("t1");
        var port = new RecordingPort();
        AttachSession.ApplyNotifyEvent(live, ProgramEvent("p_4", "t2", ProgramStatusState.Done, seen: false), tty: null);
        AttachSession.ApplyNotifyEvent(live, ProgramEvent("p_5", "t2", ProgramStatusState.Error, seen: false), tty: null);

        live.Notifications.SetFocus("t2", "p_4");
        live.SeenReports.NoteTabInView("t2");
        await AttachSession.FlushSeenReportAsync(port, live, CancellationToken.None);

        var call = Assert.Single(port.Calls);
        Assert.Equal(
            ["p_4", "p_5"],
            call.Params!["pane_ids"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task The_snapshot_feeds_the_selector()
    {
        var live = Live("t1");
        var port = new RecordingPort();
        using var snapshot = JsonDocument.Parse("""
            {"panes":[
              {"pane_id":"a","tab_id":"t1","state":"done","seen":false},
              {"pane_id":"b","tab_id":"t1","state":"done","seen":true},
              {"pane_id":"c","tab_id":"t2","state":"error","seen":false}]}
            """);

        AttachSession.NoteSeenFromSnapshot(live, snapshot.RootElement);
        await AttachSession.FlushSeenReportAsync(port, live, CancellationToken.None);

        var call = Assert.Single(port.Calls);
        Assert.Equal(["a"], call.Params!["pane_ids"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task A_failed_call_keeps_the_pane_for_the_next_flush()
    {
        var live = Live("t1");
        var port = new RecordingPort { Fail = true };
        AttachSession.ApplyNotifyEvent(live, ProgramEvent("p_2", "t1", ProgramStatusState.Done, seen: false), tty: null);

        await AttachSession.FlushSeenReportAsync(port, live, CancellationToken.None);
        port.Fail = false;
        await AttachSession.FlushSeenReportAsync(port, live, CancellationToken.None);

        Assert.Equal(2, port.Calls.Count);
    }
}
