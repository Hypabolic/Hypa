using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class EventsWaitTests
{
    [Fact]
    public void Inventory_has_request_and_response_fixtures()
    {
        Assert.Contains(ProtocolMethods.EventsWait, ProtocolMethods.All);
        foreach (var method in ProtocolMethods.EventWait)
        {
            var req = FixtureCatalog.Load(FixtureCatalog.MethodRequestPath(method));
            var res = FixtureCatalog.Load(FixtureCatalog.MethodResponsePath(method));
            Assert.Contains(method, req, StringComparison.Ordinal);
            Assert.Contains("timeout_ms", req, StringComparison.Ordinal);
            Assert.Contains(EventsWaitResult.WaitMatched, res, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task First_match_returns_wait_matched()
    {
        var (cp, state) = NewPlane();
        try
        {
            var paneId = await CreatePaneAsync(cp);
            var pinned = Math.Max(1, state.GetPane(new PaneId(paneId))!.OccupantGeneration);
            await ReportAgentAsync(cp, paneId, "working", seq: 1);

            var waitTask = DispatchAsync(cp, WaitBody(paneId, "blocked", timeoutMs: 2000));
            await Task.Delay(40);
            await ReportAgentAsync(cp, paneId, "blocked", seq: 2);

            var wait = await waitTask;
            Assert.Equal(EventsWaitResult.WaitMatched, wait.GetProperty("type").GetString());
            Assert.Equal(
                EventsWaitMatch.PaneAgentStatusChanged,
                wait.GetProperty("event").GetProperty("event").GetString());
            var data = wait.GetProperty("event").GetProperty("data");
            Assert.Equal(paneId, data.GetProperty("pane_id").GetString());
            Assert.Equal("blocked", data.GetProperty("agent_status").GetString());
            Assert.Equal(pinned, data.GetProperty("occupant_generation").GetInt32());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Already_matching_status_returns_without_waiting()
    {
        var (cp, _) = NewPlane();
        try
        {
            var paneId = await CreatePaneAsync(cp);
            await ReportAgentAsync(cp, paneId, "blocked", seq: 1);

            var wait = await DispatchAsync(cp, WaitBody(paneId, "blocked", timeoutMs: 250));
            Assert.Equal(EventsWaitResult.WaitMatched, wait.GetProperty("type").GetString());
            Assert.Equal(
                "blocked",
                wait.GetProperty("event").GetProperty("data").GetProperty("agent_status").GetString());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Transient_matching_status_returns_wait_matched()
    {
        var (cp, state) = NewPlane();
        try
        {
            var paneId = await CreatePaneAsync(cp);
            var pinned = Math.Max(1, state.GetPane(new PaneId(paneId))!.OccupantGeneration);
            await ReportAgentAsync(cp, paneId, "working", seq: 1);

            var waitTask = DispatchAsync(cp, WaitBody(paneId, "blocked", timeoutMs: 2000));
            await Task.Delay(40);
            await ReportAgentAsync(cp, paneId, "blocked", seq: 2);
            await ReportAgentAsync(cp, paneId, "idle", seq: 3);

            var wait = await waitTask;
            Assert.Equal(EventsWaitResult.WaitMatched, wait.GetProperty("type").GetString());
            Assert.Equal(
                EventsWaitMatch.PaneAgentStatusChanged,
                wait.GetProperty("event").GetProperty("event").GetString());
            var data = wait.GetProperty("event").GetProperty("data");
            Assert.Equal(paneId, data.GetProperty("pane_id").GetString());
            Assert.Equal("blocked", data.GetProperty("agent_status").GetString());
            Assert.Equal(pinned, data.GetProperty("occupant_generation").GetInt32());
            Assert.Equal(AgentStatus.Idle, state.GetPane(new PaneId(paneId))!.AgentStatus);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Process_exit_status_transition_returns_wait_matched()
    {
        var factory = TestPaneFactories.Scripted();
        var (cp, state) = NewPlane(factory: factory);
        try
        {
            var paneId = await CreatePaneAsync(cp);
            var runtime = Assert.Single(factory.Created);
            var pinned = Math.Max(1, state.GetPane(new PaneId(paneId))!.OccupantGeneration);
            Assert.Equal(AgentStatus.Working, state.GetPane(new PaneId(paneId))!.AgentStatus);

            var waitTask = DispatchAsync(cp, WaitBody(paneId, "done", timeoutMs: 2000));
            await Task.Delay(40);
            runtime.FireExited(0);

            var wait = await waitTask;
            Assert.Equal(EventsWaitResult.WaitMatched, wait.GetProperty("type").GetString());
            Assert.Equal(
                EventsWaitMatch.PaneAgentStatusChanged,
                wait.GetProperty("event").GetProperty("event").GetString());
            var data = wait.GetProperty("event").GetProperty("data");
            Assert.Equal(paneId, data.GetProperty("pane_id").GetString());
            Assert.Equal("done", data.GetProperty("agent_status").GetString());
            Assert.Equal(pinned, data.GetProperty("occupant_generation").GetInt32());
            Assert.Equal(AgentStatus.Done, state.GetPane(new PaneId(paneId))!.AgentStatus);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Occupant_replace_does_not_satisfy_an_old_wait()
    {
        var (cp, state) = NewPlane();
        try
        {
            var paneId = await CreatePaneAsync(cp);
            var pinned = state.GetPane(new PaneId(paneId))!.OccupantGeneration;
            await ReportAgentAsync(cp, paneId, "working", seq: 1);

            var waitTask = DispatchAsync(cp, WaitBody(paneId, "blocked", timeoutMs: 3000));
            await Task.Delay(40);
            await cp.BumpOccupantGenerationAsync(paneId, CancellationToken.None);
            await ReportAgentAsync(cp, paneId, "blocked", seq: 1);

            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() => waitTask);
            Assert.Equal(ProtocolErrorCodes.OccupantReplaced, ex.Code);
            Assert.Equal(EventsWaitErrors.AgentNotRunning, ex.ErrorCode);
            Assert.Equal(EventsWaitErrors.AgentNotRunningMessage, ex.Message);
            Assert.NotEqual(pinned, state.GetPane(new PaneId(paneId))!.OccupantGeneration);
            Assert.Equal(AgentStatus.Blocked, state.GetPane(new PaneId(paneId))!.AgentStatus);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Queued_match_does_not_satisfy_wait_after_occupant_replace()
    {
        var (cp, state) = NewPlane();
        try
        {
            var paneId = await CreatePaneAsync(cp);
            var pinned = state.GetPane(new PaneId(paneId))!.OccupantGeneration;
            await ReportAgentAsync(cp, paneId, "working", seq: 1);
            cp.EventsWaitAfterPinAsync = async () =>
            {
                await ReportAgentAsync(cp, paneId, "blocked", seq: 2);
                await cp.BumpOccupantGenerationAsync(paneId, CancellationToken.None);
            };

            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                DispatchAsync(cp, WaitBody(paneId, "blocked", timeoutMs: 2000)));
            Assert.Equal(ProtocolErrorCodes.OccupantReplaced, ex.Code);
            Assert.Equal(EventsWaitErrors.AgentNotRunning, ex.ErrorCode);
            Assert.Equal(EventsWaitErrors.AgentNotRunningMessage, ex.Message);
            Assert.NotEqual(pinned, state.GetPane(new PaneId(paneId))!.OccupantGeneration);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Timeout_fails_with_timeout_error()
    {
        var (cp, _) = NewPlane();
        try
        {
            var paneId = await CreatePaneAsync(cp);
            await ReportAgentAsync(cp, paneId, "working", seq: 1);

            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                DispatchAsync(cp, WaitBody(paneId, "blocked", timeoutMs: 50)));
            Assert.Equal(ProtocolErrorCodes.InvalidState, ex.Code);
            Assert.Equal(EventsWaitErrors.Timeout, ex.ErrorCode);
            Assert.Equal(EventsWaitErrors.TimeoutMessage, ex.Message);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Unsupported_match_fails_closed()
    {
        var (cp, _) = NewPlane();
        try
        {
            var paneId = await CreatePaneAsync(cp);
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                DispatchAsync(cp, new JsonObject
                {
                    ["match_event"] = new JsonObject
                    {
                        ["event"] = "layout_updated",
                        ["pane_id"] = paneId,
                        ["agent_status"] = "blocked",
                    },
                    ["timeout_ms"] = 100,
                }));
            Assert.Equal(ProtocolErrorCodes.InvalidParams, ex.Code);
            Assert.Equal(EventsWaitErrors.UnsupportedMatch, ex.ErrorCode);
            Assert.Equal(EventsWaitErrors.UnsupportedMatchMessage, ex.Message);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Dotted_event_token_fails_closed()
    {
        var (cp, _) = NewPlane();
        try
        {
            var paneId = await CreatePaneAsync(cp);
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                DispatchAsync(cp, new JsonObject
                {
                    ["match_event"] = new JsonObject
                    {
                        ["event"] = "pane.agent_status_changed",
                        ["pane_id"] = paneId,
                        ["agent_status"] = "blocked",
                    },
                    ["timeout_ms"] = 100,
                }));
            Assert.Equal(ProtocolErrorCodes.InvalidParams, ex.Code);
            Assert.Equal(EventsWaitErrors.UnsupportedMatch, ex.ErrorCode);
            Assert.Equal(EventsWaitErrors.UnsupportedMatchMessage, ex.Message);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Missing_pane_fails_with_pane_not_found()
    {
        var (cp, _) = NewPlane();
        try
        {
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                DispatchAsync(cp, WaitBody("pane_missing", "blocked", timeoutMs: 100)));
            Assert.Equal(ProtocolErrorCodes.NotFound, ex.Code);
            Assert.Equal(EventsWaitErrors.PaneNotFound, ex.ErrorCode);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Wait_does_not_leave_a_subscription()
    {
        await using var hub = new EventSubscriptionHub();
        var (cp, _) = NewPlane(hub);
        try
        {
            var paneId = await CreatePaneAsync(cp);
            await ReportAgentAsync(cp, paneId, "blocked", seq: 1);
            var sink = new CapturingSink();
            using var doc = JsonDocument.Parse(WaitBody(paneId, "blocked", timeoutMs: 250).ToJsonString());
            await cp.DispatchAsync(
                ProtocolMethods.EventsWait, doc.RootElement, sink, CancellationToken.None);
            Assert.Empty(hub.ListForConnection(sink.ConnectionId));
            Assert.Empty(sink.Snapshot());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private static Task<JsonElement> ReportAgentAsync(
        ControlPlaneService cp, string paneId, string state, int seq) =>
        DispatchAsync(cp, ProtocolMethods.PaneReportAgent, new JsonObject
        {
            ["pane_id"] = paneId,
            ["source"] = "plugin:claude",
            ["agent"] = "claude",
            ["state"] = state,
            ["seq"] = seq,
        });

    private static JsonObject WaitBody(string paneId, string status, int timeoutMs) =>
        new()
        {
            ["match_event"] = new JsonObject
            {
                ["event"] = EventsWaitMatch.PaneAgentStatusChanged,
                ["pane_id"] = paneId,
                ["agent_status"] = status,
            },
            ["timeout_ms"] = timeoutMs,
        };

    private static (ControlPlaneService Cp, AppState State) NewPlane(
        IEventSubscriptionHub? hub = null,
        IPaneRuntimeFactory? factory = null)
    {
        var state = new AppState(SessionId.New("ew-" + Guid.NewGuid().ToString("N")[..8]));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var cp = new ControlPlaneService(
            state,
            factory ?? TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            subscriptions: hub);
        return (cp, state);
    }

    private static async Task<string> CreatePaneAsync(ControlPlaneService cp)
    {
        var created = await DispatchAsync(cp, ProtocolMethods.WorkspaceCreate, new JsonObject
        {
            ["cwd"] = Path.GetTempPath(),
            ["command"] = "/bin/true",
            ["create_pane"] = true,
        });
        return created.GetProperty("pane").GetProperty("pane_id").GetString()!;
    }

    private static Task<JsonElement> DispatchAsync(ControlPlaneService cp, JsonObject body) =>
        DispatchAsync(cp, ProtocolMethods.EventsWait, body);

    private static async Task<JsonElement> DispatchAsync(
        ControlPlaneService cp, string method, JsonObject body)
    {
        using var doc = JsonDocument.Parse(body.ToJsonString());
        return await cp.DispatchAsync(method, doc.RootElement, CancellationToken.None);
    }

    private sealed class CapturingSink : IClientConnection
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];

        public string ConnectionId => "c_events_wait";

        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default)
        {
            lock (_gate)
                _lines.Add(jsonLine);
            return Task.CompletedTask;
        }

        public string[] Snapshot()
        {
            lock (_gate)
                return [.. _lines];
        }
    }
}
