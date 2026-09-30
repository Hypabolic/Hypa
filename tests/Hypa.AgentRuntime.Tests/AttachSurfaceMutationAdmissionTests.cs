using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class AttachSurfaceMutationAdmissionTests
{
    [Fact]
    public async Task Without_hello_surface_render_is_admitted()
    {
        var state = new AppState(SessionId.New("attach-render-no-hello"));
        state.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var cp = new ControlPlaneService(
            state,
            new NullPaneRuntimeFactory(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector());
        try
        {
            Assert.True(cp.TryAdmitAttachSurfaceRender("conn_local_attach", 1, 1, out _));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Surface_interest_off_then_on_restores_visible_set()
    {
        var state = new AppState(SessionId.New("attach-surface-restore"));
        state.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        // A manual clock keeps the 16 ms render tick from sending the pending
        // paint before the test reads it.
        var clock = new ManualTimeProvider();
        var cp = new ControlPlaneService(
            state,
            new NullPaneRuntimeFactory(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            timeProvider: clock);
        var conn = new FakeConnection("conn_surface_restore");
        try
        {
            await cp.HandleAttachHelloAsync(SampleHello("cli_a"), conn, CancellationToken.None);
            await cp.HandleAttachSurfaceInterestAsync(
                new AttachSurfaceInterestRequest
                {
                    RequestId = "surface-on",
                    ClientId = "cli_a",
                    Active = true,
                    GeometryRevision = 1,
                },
                conn,
                CancellationToken.None);
            var published = cp.VisibleSets.Publish(new VisibleSetPublishRequest
            {
                ConnectionId = conn.ConnectionId,
                PaneIds = ["pane-a"],
            });
            Assert.True(published.IsOk);
            Assert.Contains("pane-a", published.Value.UnionPaneIds);

            await cp.HandleAttachSurfaceInterestAsync(
                new AttachSurfaceInterestRequest
                {
                    RequestId = "surface-off",
                    ClientId = "cli_a",
                    Active = false,
                    GeometryRevision = 1,
                },
                conn,
                CancellationToken.None);
            Assert.Equal(["pane-a"], cp.VisibleSets.RetainedPaneIds(conn.ConnectionId));

            await cp.HandleAttachSurfaceInterestAsync(
                new AttachSurfaceInterestRequest
                {
                    RequestId = "surface-on-again",
                    ClientId = "cli_a",
                    Active = true,
                    GeometryRevision = 1,
                },
                conn,
                CancellationToken.None);
            Assert.Equal(["pane-a"], cp.VisibleSets.RetainedPaneIds(conn.ConnectionId));
            Assert.True(cp.VisibleSets.MayCapture("pane-a", clock.GetUtcNow()));
            Assert.True(cp.TryPeekCoalescedPaint("pane-a", out var forceFull));
            Assert.True(forceFull);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Hello_then_surface_interest_admits_render()
    {
        var state = new AppState(SessionId.New("attach-render-hello"));
        state.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var cp = new ControlPlaneService(
            state,
            new NullPaneRuntimeFactory(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector());
        var conn = new FakeConnection("conn_local_hello");
        try
        {
            await cp.HandleAttachHelloAsync(SampleHello("cli_a"), conn, CancellationToken.None);
            Assert.False(cp.TryAdmitAttachSurfaceRender(conn.ConnectionId, 1, 1, out _));

            await cp.HandleAttachSurfaceInterestAsync(
                new AttachSurfaceInterestRequest
                {
                    RequestId = "local-surface-on",
                    ClientId = "cli_a",
                    Active = true,
                    GeometryRevision = 1,
                },
                conn,
                CancellationToken.None);

            Assert.True(cp.TryAdmitAttachSurfaceRender(conn.ConnectionId, 1, 1, out var binding));
            Assert.Equal(conn.ConnectionId, binding.ConnectionId);
            Assert.False(string.IsNullOrWhiteSpace(binding.LeaseId));
            Assert.Equal(1UL, binding.SnapshotRevision);

            Assert.True(cp.TryAdmitAttachSurfaceRender(conn.ConnectionId, 3, 42, out var later));
            Assert.Equal(1UL, later.SnapshotRevision);
            Assert.Equal(3UL, later.SurfaceRevision);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Local_attach_without_hello_may_mutate()
    {
        var state = new AppState(SessionId.New("attach-local-no-hello"));
        state.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var cp = new ControlPlaneService(
            state,
            new NullPaneRuntimeFactory(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector());
        var conn = new FakeConnection("conn_local_attach");
        try
        {
            var first = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                JsonDocument.Parse("""{"cwd":"/tmp","create_pane":false,"label":"a"}""").RootElement,
                CancellationToken.None);
            var second = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceCreate,
                JsonDocument.Parse("""{"cwd":"/tmp","create_pane":false,"label":"b"}""").RootElement,
                CancellationToken.None);
            var moved = await cp.DispatchAsync(
                ProtocolMethods.WorkspaceMoveBlock,
                JsonDocument.Parse(new JsonObject
                {
                    ["workspace_ids"] = new JsonArray(first.GetProperty("workspace_id").GetString()),
                    ["before_workspace_id"] = second.GetProperty("workspace_id").GetString(),
                }.ToJsonString()).RootElement,
                conn,
                CancellationToken.None);
            Assert.True(moved.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.True);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Move_block_rejects_inactive_attach_surface()
    {
        var (cp, conn, workspaceIds) = await SeedAttachAsync();
        try
        {
            DeactivateSurface(cp, conn.ConnectionId);
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.WorkspaceMoveBlock,
                    JsonDocument.Parse(new JsonObject
                    {
                        ["workspace_ids"] = new JsonArray(workspaceIds[0]),
                        ["before_workspace_id"] = workspaceIds[1],
                    }.ToJsonString()).RootElement,
                    conn,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidState, ex.Code);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Pane_input_set_rejects_inactive_attach_surface()
    {
        var (cp, conn, _) = await SeedAttachAsync();
        try
        {
            var pane = await cp.DispatchAsync(
                ProtocolMethods.PaneCreate,
                JsonDocument.Parse(new JsonObject
                {
                    ["workspace_id"] = (await cp.DispatchAsync(
                        ProtocolMethods.WorkspaceList,
                        parameters: null,
                        CancellationToken.None))[0].GetProperty("workspace_id").GetString(),
                    ["command"] = "/bin/echo",
                }.ToJsonString()).RootElement,
                CancellationToken.None);
            DeactivateSurface(cp, conn.ConnectionId);
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneInputSet,
                    JsonDocument.Parse(new JsonObject
                    {
                        ["pane_id"] = pane.GetProperty("pane_id").GetString(),
                        ["right_click"] = "pane",
                    }.ToJsonString()).RootElement,
                    conn,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidState, ex.Code);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private static async Task<(ControlPlaneService Cp, FakeConnection Conn, string[] WorkspaceIds)> SeedAttachAsync()
    {
        var state = new AppState(SessionId.New("attach-mutation-admission"));
        state.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var cp = new ControlPlaneService(
            state,
            new NullPaneRuntimeFactory(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector());
        var conn = new FakeConnection("conn_attach_mut");
        cp.AttachSurfaceInterest.ApplyHello(new AttachEndpointHelloApplyRequest
        {
            ConnectionId = conn.ConnectionId,
            EndpointId = state.Snapshot().Id.Value,
            Hello = SampleHello("cli_a"),
        });
        cp.AttachSurfaceInterest.ApplySurfaceInterest(new AttachSurfaceInterestApplyRequest
        {
            ConnectionId = conn.ConnectionId,
            ClientId = "cli_a",
            Active = true,
            GeometryRevision = 1,
        });

        var first = await cp.DispatchAsync(
            ProtocolMethods.WorkspaceCreate,
            JsonDocument.Parse("""{"cwd":"/tmp","create_pane":false,"label":"a"}""").RootElement,
            CancellationToken.None);
        var second = await cp.DispatchAsync(
            ProtocolMethods.WorkspaceCreate,
            JsonDocument.Parse("""{"cwd":"/tmp","create_pane":false,"label":"b"}""").RootElement,
            CancellationToken.None);
        var ids = new[]
        {
            first.GetProperty("workspace_id").GetString()!,
            second.GetProperty("workspace_id").GetString()!,
        };
        return (cp, conn, ids);
    }

    private static void DeactivateSurface(ControlPlaneService cp, string connectionId) =>
        cp.AttachSurfaceInterest.ApplySurfaceInterest(new AttachSurfaceInterestApplyRequest
        {
            ConnectionId = connectionId,
            ClientId = "cli_a",
            Active = false,
            GeometryRevision = 1,
        });

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

    private sealed class FakeConnection(string id) : IClientConnection
    {
        public string ConnectionId { get; } = id;
        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default) => Task.CompletedTask;
    }
}
