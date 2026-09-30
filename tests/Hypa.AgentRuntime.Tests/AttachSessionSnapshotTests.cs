using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class AttachSessionSnapshotTests
{
    [Fact]
    public async Task Session_snapshot_emits_live_attach_projection_revision()
    {
        var state = new AppState(SessionId.New("attach-snapshot-revision"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var cp = new ControlPlaneService(
            state,
            new NullPaneRuntimeFactory(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector());
        try
        {
            cp.AttachSurfaceInterest.ApplyHello(new AttachEndpointHelloApplyRequest
            {
                ConnectionId = "conn_1",
                EndpointId = state.Snapshot().Id.Value,
                Hello = SampleHello("cli_a"),
            });
            cp.AttachSurfaceInterest.ApplySurfaceInterest(new AttachSurfaceInterestApplyRequest
            {
                ConnectionId = "conn_1",
                ClientId = "cli_a",
                Active = true,
                GeometryRevision = 1,
            });

            var snap = await cp.DispatchAsync(
                ProtocolMethods.SessionSnapshot,
                parameters: null,
                CancellationToken.None);
            var revision = snap.GetProperty("revision").GetUInt64();
            var projection = snap.GetProperty("projection_revision").GetUInt64();
            var bootId = snap.GetProperty("boot_id").GetString();

            Assert.Equal(1UL, revision);
            Assert.Equal(revision, projection);
            Assert.False(string.IsNullOrWhiteSpace(bootId));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private static AttachEndpointHello SampleHello(string clientId) =>
        new()
        {
            EndpointGeneration = AttachEndpointProtocol.EndpointGeneration,
            ProtocolMajor = 1,
            ProtocolMinor = 1,
            ClientId = clientId,
            Geometry = new AttachGeometry
            {
                Columns = 80,
                Rows = 24,
                CellWidthPx = 8,
                CellHeightPx = 16,
                GeometryRevision = 1,
            },
            SurfaceActive = false,
            SnapshotCodecs = [AttachEndpointProtocol.SnapshotCodec],
            SurfaceCodecs = [AttachEndpointProtocol.SurfaceCodec],
            InputCodecs = [AttachEndpointProtocol.InputCodec],
            RequiredCapabilities = AttachEndpointProtocol.RequiredCapabilities.ToArray(),
        };

    private sealed class NullPaneRuntimeFactory : IPaneRuntimeFactory
    {
        public IPaneRuntime Create(PaneSpawnOptions options) => new NullPaneRuntime(options.Id);

        private sealed class NullPaneRuntime(PaneId id) : IPaneRuntime
        {
            public PaneId Id { get; } = id;
            public bool IsAlive => true;
            public int? ExitCode => null;
            public int? Pid => null;
#pragma warning disable CS0067
            public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
            public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
            public event Action<IPaneRuntime, int>? Exited;
#pragma warning restore CS0067
            public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
            public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => ValueTask.CompletedTask;
            public ValueTask WriteTextAsync(string text, CancellationToken ct) => ValueTask.CompletedTask;
            public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct) => ValueTask.CompletedTask;
            public string ReadVisibleText() => string.Empty;
            public string ReadRecentText(int maxChars) => string.Empty;
            public string ReadRecentUnwrappedText(int maxChars) => string.Empty;
            public string ReadDetectionText() => string.Empty;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
