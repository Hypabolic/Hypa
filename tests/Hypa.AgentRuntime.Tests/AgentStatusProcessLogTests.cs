using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class AgentStatusProcessLogTests
{
    [Fact]
    public async Task Agent_status_change_writes_a_mux_process_log_line()
    {
        var sink = new CapturingProcessLogSink();
        var state = new AppState(SessionId.New("agent-status-log"));
        state.UpdateSession(session => session with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var workspace = state.CreateWorkspace(Path.GetTempPath(), binding: null);
        var tabId = workspace.TabIds[0];
        state.RegisterPane(new PaneState
        {
            Id = new PaneId("p1"),
            TabId = tabId,
            WorkspaceId = workspace.Id,
            OccupantGeneration = 1,
            LifecycleState = PaneLifecycle.Running,
            IsAlive = true,
        });
        await using var hub = new EventSubscriptionHub();
        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            subscriptions: hub,
            processLog: sink);
        try
        {
            Assert.True(cp.TryApplyAgentStatus("p1", expectedGeneration: 1, AgentStatus.Idle, "claude", message: null));
            var until = DateTime.UtcNow.AddSeconds(2);
            ProcessLogRecord? line = null;
            while (DateTime.UtcNow < until)
            {
                line = sink.Records.FirstOrDefault(record =>
                    record.Event == ProcessLogEvents.MuxAgentStatusChanged);
                if (line is not null)
                    break;
                await Task.Delay(20);
            }

            Assert.NotNull(line);
            Assert.Equal("p1", line.PaneId);
            Assert.Equal("claude", line.Agent);
            Assert.Equal("idle", line.AgentStatus);
            Assert.Equal("unknown", line.PreviousStatus);
            Assert.Equal(state.SessionId.Value, line.SessionId);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }
}
