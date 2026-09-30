using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class AttachClientViewControlPlaneTests
{
    [Fact]
    public async Task Two_clients_keep_independent_snapshot_tabs_and_visible_union()
    {
        var graph = SeedTwoTabs();
        var cp = CreateControlPlane(graph.State);
        var first = new FakeConnection("conn_a");
        var second = new FakeConnection("conn_b");
        try
        {
            await HelloAndActivateAsync(cp, first, "cli_a");
            await HelloAndActivateAsync(cp, second, "cli_b");

            await FocusAsync(cp, first, "cli_a", graph.TabOne, graph.PaneOne);
            await FocusAsync(cp, second, "cli_b", graph.TabTwo, graph.PaneTwo);

            var snapA = await SnapshotAsync(cp, first);
            var snapB = await SnapshotAsync(cp, second);
            Assert.Equal(graph.TabOne, snapA.GetProperty("focused_tab_id").GetString());
            Assert.Equal(graph.TabTwo, snapB.GetProperty("focused_tab_id").GetString());
            Assert.NotEqual(
                snapA.GetProperty("focused_tab_id").GetString(),
                snapB.GetProperty("focused_tab_id").GetString());

            var catalog = await SnapshotAsync(cp, connection: null);
            Assert.Equal(graph.TabOne, catalog.GetProperty("focused_tab_id").GetString());

            cp.VisibleSets.Publish(new VisibleSetPublishRequest
            {
                ConnectionId = first.ConnectionId,
                PaneIds = [graph.PaneOne],
            });
            var union = cp.VisibleSets.Publish(new VisibleSetPublishRequest
            {
                ConnectionId = second.ConnectionId,
                PaneIds = [graph.PaneTwo],
            });
            Assert.True(union.IsOk);
            Assert.Contains(graph.PaneOne, union.Value.UnionPaneIds);
            Assert.Contains(graph.PaneTwo, union.Value.UnionPaneIds);
            var expected = new[] { graph.PaneOne, graph.PaneTwo };
            Array.Sort(expected, StringComparer.Ordinal);
            Assert.Equal(
                expected,
                cp.AttachClientViews.CollectActivePaneIds(cp.AttachSurfaceInterest.IsSurfaceActive));

            var floorB = cp.AttachSurfaceInterest.GetSnapshot(second.ConnectionId)!;
            Assert.True(floorB.ProjectionRevision >= 2);
            var stale = cp.AttachSurfaceInterest.AdmitSurface(new AttachActivationAdmissionRequest
            {
                ConnectionId = second.ConnectionId,
                ProjectionRevision = 1,
                SurfaceRevision = 1,
                GeometryRevision = floorB.GeometryRevision,
                BootId = floorB.BootId,
                LeaseId = floorB.LeaseId!,
            });
            Assert.False(stale.IsOk);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Detach_drops_server_view_and_inactive_hint_leaves_union()
    {
        var graph = SeedTwoTabs();
        var cp = CreateControlPlane(graph.State);
        var first = new FakeConnection("conn_a");
        var second = new FakeConnection("conn_b");
        try
        {
            await HelloAndActivateAsync(cp, first, "cli_a");
            await HelloAndActivateAsync(cp, second, "cli_b");
            await FocusAsync(cp, first, "cli_a", graph.TabOne, graph.PaneOne);
            await FocusAsync(cp, second, "cli_b", graph.TabTwo, graph.PaneTwo);
            cp.VisibleSets.Publish(new VisibleSetPublishRequest
            {
                ConnectionId = first.ConnectionId,
                PaneIds = [graph.PaneOne],
            });
            cp.VisibleSets.Publish(new VisibleSetPublishRequest
            {
                ConnectionId = second.ConnectionId,
                PaneIds = [graph.PaneTwo],
            });

            await cp.HandleAttachSurfaceInterestAsync(
                new AttachSurfaceInterestRequest
                {
                    RequestId = "off",
                    ClientId = "cli_a",
                    Active = false,
                    GeometryRevision = 1,
                },
                first,
                CancellationToken.None);

            Assert.NotNull(cp.AttachClientViews.Get(first.ConnectionId));
            Assert.Equal(
                [graph.PaneTwo],
                cp.AttachClientViews.CollectActivePaneIds(cp.AttachSurfaceInterest.IsSurfaceActive));

            cp.OnClientDisconnected(first);
            Assert.Null(cp.AttachClientViews.Get(first.ConnectionId));
            var afterDetach = cp.VisibleSets.Publish(new VisibleSetPublishRequest
            {
                ConnectionId = second.ConnectionId,
                PaneIds = [graph.PaneTwo],
            });
            Assert.DoesNotContain(graph.PaneOne, afterDetach.Value.UnionPaneIds);
            Assert.Contains(graph.PaneTwo, afterDetach.Value.UnionPaneIds);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Closed_tab_reconciles_only_that_client()
    {
        var graph = SeedTwoTabs();
        var cp = CreateControlPlane(graph.State);
        var first = new FakeConnection("conn_a");
        var second = new FakeConnection("conn_b");
        try
        {
            await HelloAndActivateAsync(cp, first, "cli_a");
            await HelloAndActivateAsync(cp, second, "cli_b");
            await FocusAsync(cp, first, "cli_a", graph.TabOne, graph.PaneOne);
            await FocusAsync(cp, second, "cli_b", graph.TabTwo, graph.PaneTwo);

            await cp.HandleTabCloseAsync(
                new TabCloseParams { TabId = graph.TabTwo },
                second,
                CancellationToken.None);

            Assert.Equal(graph.TabOne, cp.AttachClientViews.Get(first.ConnectionId)!.FocusedTabId());
            Assert.Equal(graph.TabOne, cp.AttachClientViews.Get(second.ConnectionId)!.FocusedTabId());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Stale_generation_is_rejected_after_reconnect()
    {
        var graph = SeedTwoTabs();
        var cp = CreateControlPlane(graph.State);
        var first = new FakeConnection("conn_old");
        var second = new FakeConnection("conn_new");
        try
        {
            await HelloAndActivateAsync(cp, first, "cli_a");
            await HelloAndActivateAsync(cp, second, "cli_a");

            await Assert.ThrowsAsync<ControlPlaneException>(() =>
                FocusAsync(cp, first, "cli_a", graph.TabTwo, graph.PaneTwo));

            await FocusAsync(cp, second, "cli_a", graph.TabTwo, graph.PaneTwo);
            Assert.Null(cp.AttachClientViews.Get(first.ConnectionId));
            Assert.Equal(graph.TabTwo, cp.AttachClientViews.Get(second.ConnectionId)!.FocusedTabId());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private static async Task HelloAndActivateAsync(
        ControlPlaneService cp,
        IClientConnection connection,
        string clientId)
    {
        await cp.HandleAttachHelloAsync(
            SampleHello(clientId),
            connection,
            CancellationToken.None);
        await cp.HandleAttachSurfaceInterestAsync(
            new AttachSurfaceInterestRequest
            {
                RequestId = "on",
                ClientId = clientId,
                Active = true,
                GeometryRevision = 1,
            },
            connection,
            CancellationToken.None);
    }

    private static Task<System.Text.Json.JsonElement> FocusAsync(
        ControlPlaneService cp,
        IClientConnection connection,
        string clientId,
        string tabId,
        string paneId) =>
        cp.HandleAttachFocusAsync(
            new AttachFocusRequest
            {
                RequestId = "focus",
                ClientId = clientId,
                Focused = true,
                TabId = tabId,
                PaneId = paneId,
            },
            connection,
            CancellationToken.None);

    private static Task<System.Text.Json.JsonElement> SnapshotAsync(
        ControlPlaneService cp,
        IClientConnection? connection) =>
        cp.DispatchAsync(ProtocolMethods.SessionSnapshot, parameters: null, connection, CancellationToken.None);

    private static Graph SeedTwoTabs()
    {
        var state = new AppState(SessionId.New("attach-client-views"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var workspace = state.CreateWorkspace("/tmp/views", "views");
        var tabOne = state.ListTabs(workspace.Id)[0];
        var tabTwo = state.CreateTab(workspace.Id, "two", focus: false);
        var paneOne = state.RegisterPane(new PaneState
        {
            Id = PaneId.New(),
            TabId = tabOne.Id,
            WorkspaceId = workspace.Id,
            Label = "one",
            IsAlive = true,
        });
        var paneTwo = state.RegisterPane(new PaneState
        {
            Id = PaneId.New(),
            TabId = tabTwo.Id,
            WorkspaceId = workspace.Id,
            Label = "two",
            IsAlive = true,
        });
        return new Graph(state, tabOne.Id.Value, tabTwo.Id.Value, paneOne.Id.Value, paneTwo.Id.Value);
    }

    private static ControlPlaneService CreateControlPlane(AppState state) =>
        new(
            state,
            new NullPaneRuntimeFactory(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector());

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

    private sealed record Graph(
        AppState State,
        string TabOne,
        string TabTwo,
        string PaneOne,
        string PaneTwo);

    private sealed class FakeConnection(string id) : IClientConnection
    {
        public string ConnectionId { get; } = id;

        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default) => Task.CompletedTask;
    }

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
