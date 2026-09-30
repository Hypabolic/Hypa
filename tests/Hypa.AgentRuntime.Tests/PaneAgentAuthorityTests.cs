using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Semantic pane authority: report owns waits and notify; session does not;
/// clear and release check the owner before sequence.
/// </summary>
public sealed class PaneAgentAuthorityTests
{
    [Fact]
    public async Task Report_agent_settles_wait_and_list_row()
    {
        var (cp, state) = NewPlane();
        try
        {
            var paneId = await CreatePaneAsync(cp);
            await DispatchAsync(cp, ProtocolMethods.PaneReportAgent, new JsonObject
            {
                ["pane_id"] = paneId,
                ["source"] = "plugin:claude",
                ["agent"] = "claude",
                ["state"] = "blocked",
                ["seq"] = 1,
            });

            var wait = await DispatchAsync(cp, ProtocolMethods.AgentWait, new JsonObject
            {
                ["pane_id"] = paneId,
                ["until"] = "blocked",
                ["timeout_ms"] = 250,
            });
            Assert.Equal("blocked", wait.GetProperty("state").GetString());
            Assert.False(wait.GetProperty("wait").GetProperty("timed_out").GetBoolean());

            var listed = await DispatchAsync(cp, ProtocolMethods.AgentList, new JsonObject());
            var row = listed.EnumerateArray().Single(el => el.GetProperty("pane_id").GetString() == paneId);
            Assert.Equal("blocked", row.GetProperty("state").GetString());
            Assert.Equal("claude", row.GetProperty("agent").GetString());

            var pane = state.GetPane(new PaneId(paneId));
            Assert.NotNull(pane);
            Assert.NotNull(pane.AgentAuthority);
            Assert.Equal("plugin:claude", pane.AgentAuthority!.Source);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Session_report_does_not_change_wait()
    {
        var (cp, state) = NewPlane();
        try
        {
            var paneId = await CreatePaneAsync(cp);
            await DispatchAsync(cp, ProtocolMethods.PaneReportAgent, new JsonObject
            {
                ["pane_id"] = paneId,
                ["source"] = "plugin:claude",
                ["agent"] = "claude",
                ["state"] = "working",
                ["seq"] = 1,
            });
            await DispatchAsync(cp, ProtocolMethods.PaneReportAgentSession, new JsonObject
            {
                ["pane_id"] = paneId,
                ["source"] = "hypa:claude",
                ["agent"] = "claude",
                ["agent_session_id"] = "sess-1",
                ["seq"] = 1,
            });

            var got = await DispatchAsync(cp, ProtocolMethods.AgentGet, new JsonObject
            {
                ["pane_id"] = paneId,
            });
            Assert.Equal("working", got.GetProperty("state").GetString());
            Assert.Equal("id", got.GetProperty("agent_session").GetProperty("kind").GetString());
            Assert.Equal("sess-1", got.GetProperty("agent_session").GetProperty("value").GetString());

            var wait = await DispatchAsync(cp, ProtocolMethods.AgentWait, new JsonObject
            {
                ["pane_id"] = paneId,
                ["until"] = "blocked",
                ["timeout_ms"] = 250,
            });
            Assert.Equal("working", wait.GetProperty("state").GetString());
            Assert.True(wait.GetProperty("wait").GetProperty("timed_out").GetBoolean());

            var pane = state.GetPane(new PaneId(paneId));
            Assert.NotNull(pane!.AgentAuthority);
            Assert.Equal(AgentStatus.Working, pane.AgentAuthority!.State);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Release_owner_drops_authority()
    {
        var (cp, state) = NewPlane();
        try
        {
            var paneId = await CreatePaneAsync(cp);
            await DispatchAsync(cp, ProtocolMethods.PaneReportAgent, new JsonObject
            {
                ["pane_id"] = paneId,
                ["source"] = "plugin:claude",
                ["agent"] = "claude",
                ["state"] = "blocked",
                ["seq"] = 2,
            });
            await DispatchAsync(cp, ProtocolMethods.PaneReleaseAgent, new JsonObject
            {
                ["pane_id"] = paneId,
                ["source"] = "plugin:claude",
                ["agent"] = "claude",
                ["seq"] = 3,
            });

            var pane = state.GetPane(new PaneId(paneId));
            Assert.NotNull(pane);
            Assert.Null(pane.AgentAuthority);
            Assert.NotEqual(AgentStatus.Blocked, pane.AgentStatus);

            var wait = await DispatchAsync(cp, ProtocolMethods.AgentWait, new JsonObject
            {
                ["pane_id"] = paneId,
                ["until"] = "blocked",
                ["timeout_ms"] = 250,
            });
            Assert.True(wait.GetProperty("wait").GetProperty("timed_out").GetBoolean());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Clear_non_owner_fails_closed_even_when_sequence_is_stale()
    {
        var (cp, _) = NewPlane();
        try
        {
            var paneId = await CreatePaneAsync(cp);
            await DispatchAsync(cp, ProtocolMethods.PaneReportAgent, new JsonObject
            {
                ["pane_id"] = paneId,
                ["source"] = "plugin:old",
                ["agent"] = "claude",
                ["state"] = "working",
                ["seq"] = 5,
            });
            await DispatchAsync(cp, ProtocolMethods.PaneReleaseAgent, new JsonObject
            {
                ["pane_id"] = paneId,
                ["source"] = "plugin:old",
                ["agent"] = "claude",
                ["seq"] = 6,
            });
            await DispatchAsync(cp, ProtocolMethods.PaneReportAgent, new JsonObject
            {
                ["pane_id"] = paneId,
                ["source"] = "plugin:new",
                ["agent"] = "claude",
                ["state"] = "blocked",
                ["seq"] = 1,
            });

            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                DispatchAsync(cp, ProtocolMethods.PaneClearAgentAuthority, new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["source"] = "plugin:old",
                    ["seq"] = 4,
                }));
            Assert.Equal(ProtocolErrorCodes.InvalidState, ex.Code);

            var got = await DispatchAsync(cp, ProtocolMethods.AgentGet, new JsonObject
            {
                ["pane_id"] = paneId,
            });
            Assert.Equal("blocked", got.GetProperty("state").GetString());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Clear_without_source_ignores_stale_current_owner_sequence()
    {
        var (cp, state) = NewPlane();
        try
        {
            var paneId = await CreatePaneAsync(cp);
            await DispatchAsync(cp, ProtocolMethods.PaneReportAgent, new JsonObject
            {
                ["pane_id"] = paneId,
                ["source"] = "plugin:claude",
                ["agent"] = "claude",
                ["state"] = "working",
                ["seq"] = 20,
            });

            var stale = await DispatchAsync(cp, ProtocolMethods.PaneClearAgentAuthority, new JsonObject
            {
                ["pane_id"] = paneId,
                ["seq"] = 19,
            });
            Assert.True(stale.GetProperty("ok").GetBoolean());

            var pane = state.GetPane(new PaneId(paneId));
            Assert.NotNull(pane!.AgentAuthority);
            Assert.Equal(AgentStatus.Working, pane.AgentStatus);

            var missing = await DispatchAsync(cp, ProtocolMethods.PaneClearAgentAuthority, new JsonObject
            {
                ["pane_id"] = paneId,
            });
            Assert.True(missing.GetProperty("ok").GetBoolean());
            Assert.NotNull(state.GetPane(new PaneId(paneId))!.AgentAuthority);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Clear_without_source_accepts_newer_current_owner_sequence()
    {
        var (cp, state) = NewPlane();
        try
        {
            var paneId = await CreatePaneAsync(cp);
            await DispatchAsync(cp, ProtocolMethods.PaneReportAgent, new JsonObject
            {
                ["pane_id"] = paneId,
                ["source"] = "plugin:claude",
                ["agent"] = "claude",
                ["state"] = "working",
                ["seq"] = 20,
            });

            var cleared = await DispatchAsync(cp, ProtocolMethods.PaneClearAgentAuthority, new JsonObject
            {
                ["pane_id"] = paneId,
                ["seq"] = 21,
            });
            Assert.True(cleared.GetProperty("ok").GetBoolean());
            Assert.Null(state.GetPane(new PaneId(paneId))!.AgentAuthority);
            Assert.Equal(21, state.GetPane(new PaneId(paneId))!.AgentAuthoritySequences["plugin:claude"]);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Report_non_owner_fails_closed_even_when_sequence_is_stale()
    {
        var (cp, _) = NewPlane();
        try
        {
            var paneId = await CreatePaneAsync(cp);
            await DispatchAsync(cp, ProtocolMethods.PaneReportAgent, new JsonObject
            {
                ["pane_id"] = paneId,
                ["source"] = "plugin:old",
                ["agent"] = "claude",
                ["state"] = "working",
                ["seq"] = 5,
            });
            await DispatchAsync(cp, ProtocolMethods.PaneReleaseAgent, new JsonObject
            {
                ["pane_id"] = paneId,
                ["source"] = "plugin:old",
                ["agent"] = "claude",
                ["seq"] = 6,
            });
            await DispatchAsync(cp, ProtocolMethods.PaneReportAgent, new JsonObject
            {
                ["pane_id"] = paneId,
                ["source"] = "plugin:new",
                ["agent"] = "claude",
                ["state"] = "blocked",
                ["seq"] = 1,
            });

            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                DispatchAsync(cp, ProtocolMethods.PaneReportAgent, new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["source"] = "plugin:old",
                    ["agent"] = "claude",
                    ["state"] = "idle",
                    ["seq"] = 4,
                }));
            Assert.Equal(ProtocolErrorCodes.InvalidState, ex.Code);

            var got = await DispatchAsync(cp, ProtocolMethods.AgentGet, new JsonObject
            {
                ["pane_id"] = paneId,
            });
            Assert.Equal("blocked", got.GetProperty("state").GetString());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Release_non_owner_fails_closed_even_when_sequence_is_stale()
    {
        var (cp, _) = NewPlane();
        try
        {
            var paneId = await CreatePaneAsync(cp);
            await DispatchAsync(cp, ProtocolMethods.PaneReportAgent, new JsonObject
            {
                ["pane_id"] = paneId,
                ["source"] = "plugin:old",
                ["agent"] = "claude",
                ["state"] = "working",
                ["seq"] = 5,
            });
            await DispatchAsync(cp, ProtocolMethods.PaneReleaseAgent, new JsonObject
            {
                ["pane_id"] = paneId,
                ["source"] = "plugin:old",
                ["agent"] = "claude",
                ["seq"] = 6,
            });
            await DispatchAsync(cp, ProtocolMethods.PaneReportAgent, new JsonObject
            {
                ["pane_id"] = paneId,
                ["source"] = "plugin:new",
                ["agent"] = "claude",
                ["state"] = "blocked",
                ["seq"] = 1,
            });

            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                DispatchAsync(cp, ProtocolMethods.PaneReleaseAgent, new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["source"] = "plugin:old",
                    ["agent"] = "claude",
                    ["seq"] = 4,
                }));
            Assert.Equal(ProtocolErrorCodes.InvalidState, ex.Code);

            var got = await DispatchAsync(cp, ProtocolMethods.AgentGet, new JsonObject
            {
                ["pane_id"] = paneId,
            });
            Assert.Equal("blocked", got.GetProperty("state").GetString());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Report_metadata_tokens_appear_on_agent_row()
    {
        var (cp, _) = NewPlane();
        try
        {
            var paneId = await CreatePaneAsync(cp);
            await DispatchAsync(cp, ProtocolMethods.PaneReportMetadata, new JsonObject
            {
                ["pane_id"] = paneId,
                ["source"] = "git",
                ["tokens"] = new JsonObject { ["name"] = "claude" },
                ["seq"] = 1,
            });

            var listed = await DispatchAsync(cp, ProtocolMethods.AgentList, new JsonObject());
            var row = listed.EnumerateArray().Single(el => el.GetProperty("pane_id").GetString() == paneId);
            Assert.Equal("claude", row.GetProperty("tokens").GetProperty("name").GetString());

            var got = await DispatchAsync(cp, ProtocolMethods.AgentGet, new JsonObject
            {
                ["pane_id"] = paneId,
            });
            Assert.Equal("claude", got.GetProperty("tokens").GetProperty("name").GetString());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Report_agent_rejects_id_and_path_together_even_when_sequence_is_stale()
    {
        var (cp, state) = NewPlane();
        try
        {
            var paneId = await CreatePaneAsync(cp);
            await DispatchAsync(cp, ProtocolMethods.PaneReportAgent, new JsonObject
            {
                ["pane_id"] = paneId,
                ["source"] = "plugin:claude",
                ["agent"] = "claude",
                ["state"] = "working",
                ["seq"] = 2,
            });

            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                DispatchAsync(cp, ProtocolMethods.PaneReportAgent, new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["source"] = "plugin:claude",
                    ["agent"] = "claude",
                    ["state"] = "idle",
                    ["seq"] = 1,
                    ["agent_session_id"] = "sess-1",
                    ["agent_session_path"] = "/tmp/s",
                }));
            Assert.Equal(ProtocolErrorCodes.InvalidParams, ex.Code);

            var pane = state.GetPane(new PaneId(paneId));
            Assert.Equal(AgentStatus.Working, pane!.AgentStatus);
            Assert.Null(pane.AgentSession);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Report_emits_agent_status_changed()
    {
        var hub = new EventSubscriptionHub();
        var sink = new CapturingSink();
        var (cp, _) = NewPlane(hub);
        try
        {
            var paneId = await CreatePaneAsync(cp);
            // Live-only emits use seq 0 when the host has no journal. FromSeq
            // must be below that so the first status event is not skipped.
            var sub = hub.Register(new EventSubscriptionRequest
            {
                SubscriptionId = "sub_auth",
                ConnectionId = "c_auth",
                FromSeq = -1,
                Classes = new HashSet<EventClass>(),
                NamedTypes = new HashSet<string>(StringComparer.Ordinal)
                {
                    ProtocolEventTypes.PaneAgentStatusChanged,
                },
                ReplayBudget = 0,
                Live = true,
                Sink = sink,
            });
            sub.EnableLive();
            await DispatchAsync(cp, ProtocolMethods.PaneReportAgent, new JsonObject
            {
                ["pane_id"] = paneId,
                ["source"] = "plugin:claude",
                ["agent"] = "claude",
                ["state"] = "blocked",
                ["seq"] = 1,
            });

            var deadline = DateTime.UtcNow.AddSeconds(2);
            JsonElement? payload = null;
            while (DateTime.UtcNow < deadline && payload is null)
            {
                foreach (var line in sink.Snapshot())
                {
                    using var doc = JsonDocument.Parse(line);
                    if (!doc.RootElement.TryGetProperty("params", out var prms))
                        continue;
                    if (prms.GetProperty("type").GetString() != ProtocolEventTypes.PaneAgentStatusChanged)
                        continue;
                    if (!prms.TryGetProperty("payload", out var p))
                        continue;
                    if (p.ValueKind == JsonValueKind.String)
                        p = JsonDocument.Parse(p.GetString()!).RootElement.Clone();
                    else
                        p = p.Clone();
                    if (p.GetProperty("pane_id").GetString() == paneId
                        && p.GetProperty("agent_status").GetString() == "blocked")
                    {
                        payload = p;
                        break;
                    }
                }

                if (payload is null)
                    await Task.Delay(15);
            }

            Assert.NotNull(payload);
            Assert.Equal("blocked", payload.Value.GetProperty("agent_status").GetString());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private static (ControlPlaneService Cp, AppState State) NewPlane(IEventSubscriptionHub? hub = null)
    {
        var state = new AppState(SessionId.New("auth-" + Guid.NewGuid().ToString("N")[..8]));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Stub(),
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

    private static async Task<JsonElement> DispatchAsync(
        ControlPlaneService cp, string method, JsonObject body)
    {
        using var doc = JsonDocument.Parse(body.ToJsonString());
        return await cp.DispatchAsync(method, doc.RootElement, CancellationToken.None);
    }

    private sealed class CapturingSink : IEventPushSink
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];

        public string ConnectionId => "c_auth";

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
