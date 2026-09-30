using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class ServerLiveHandoffTests
{
    [Fact]
    public async Task Unix_live_handoff_succeeds_without_server_stop()
    {
        var cp = NewService();
        var result = await cp.HandleServerLiveHandoffAsync(
            new Hypa.AgentRuntime.Protocol.Models.EmptyParams(),
            unix: true,
            CancellationToken.None);
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.True(cp.LiveHandoffStarted);
        Assert.False(cp.ServerStopStarted);
    }

    [Fact]
    public async Task Non_unix_live_handoff_fails_closed()
    {
        var cp = NewService();
        var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            cp.HandleServerLiveHandoffAsync(
                new Hypa.AgentRuntime.Protocol.Models.EmptyParams(),
                unix: false,
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.InvalidState, ex.Code);
        Assert.Contains("Unix-only", ex.Message, StringComparison.Ordinal);
        Assert.False(cp.LiveHandoffStarted);
        Assert.False(cp.ServerStopStarted);
    }

    [Fact]
    public void Registry_advertises_live_handoff()
    {
        var cp = NewService();
        var registry = new Hypa.ControlPlane.Dispatch.ControlPlaneMethodRegistry(cp);
        Assert.Contains(ProtocolMethods.ServerLiveHandoff, registry.MethodNames);
        Assert.Contains(ProtocolMethods.ServerLiveHandoff, ProtocolMethods.All);
        Assert.True(FixtureCatalog.Exists(FixtureCatalog.MethodRequestPath(ProtocolMethods.ServerLiveHandoff)));
        Assert.True(FixtureCatalog.Exists(FixtureCatalog.MethodResponsePath(ProtocolMethods.ServerLiveHandoff)));
    }

    private static ControlPlaneService NewService()
    {
        var state = new AppState(SessionId.New("livehandoff"));
        state.UpdateSession(s => s with { Name = "livehandoff", LifecycleState = SessionLifecycle.Ready });
        return new ControlPlaneService(
            state,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector());
    }
}
