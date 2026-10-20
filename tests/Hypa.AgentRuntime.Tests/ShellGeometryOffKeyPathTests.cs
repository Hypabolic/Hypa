using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.ControlPlane;
using Hypa.Runtime.Domain.Common;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// A key from a client shell can make that client the geometry owner of its
/// tab. The tab resize then runs as an internal work item. The key does not
/// wait for it.
/// </summary>
public sealed class ShellGeometryOffKeyPathTests
{
    [Fact]
    public async Task A_key_that_claims_the_tab_returns_while_the_tab_resize_is_still_held()
    {
        var factory = new GatedPaneFactory();
        var (cp, paneId, tabId) = await SeedPaneAsync(factory);
        var first = new FakeConnection("conn_a");
        var second = new FakeConnection("conn_b");
        try
        {
            await HelloAndActivateAsync(cp, first, "cli_a", columns: 120, rows: 40);
            await FocusAsync(cp, first, "cli_a", tabId, paneId);
            Assert.Equal((120, 40), factory.LastSize(paneId));
            await HelloAndActivateAsync(cp, second, "cli_b", columns: 80, rows: 24);
            Assert.True(cp.TabGeometry.IsController(first.ConnectionId, tabId));

            factory.HoldResizes();
            var send = SendKeysAsync(cp, second, paneId, "x");

            // The key returns while the tab resize waits inside the pane runtime.
            var reply = await send.WaitAsync(Poll.HangGuard);
            Assert.Equal(1, reply.GetProperty("accepted_bytes").GetInt32());
            await Poll.GuardAsync(factory.ResizeHeld, "the queued tab resize to start");
            Assert.True(cp.TabGeometry.IsController(second.ConnectionId, tabId));
            Assert.Equal((120, 40), factory.LastSize(paneId));
            await Poll.UntilAsync(() => factory.Writes.Contains("x"), "the key write");

            factory.ReleaseResizes();
            await cp.ShellGeometryWorkIdleAsync().WaitAsync(Poll.HangGuard);
            Assert.Equal((80, 24), factory.LastSize(paneId));
        }
        finally
        {
            factory.ReleaseResizes();
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_key_from_the_tab_owner_queues_no_tab_resize()
    {
        var factory = new GatedPaneFactory();
        var (cp, paneId, tabId) = await SeedPaneAsync(factory);
        var first = new FakeConnection("conn_a");
        try
        {
            await HelloAndActivateAsync(cp, first, "cli_a", columns: 120, rows: 40);
            await FocusAsync(cp, first, "cli_a", tabId, paneId);
            Assert.True(cp.TabGeometry.IsController(first.ConnectionId, tabId));
            var resizes = factory.ResizeCount;

            factory.HoldResizes();
            await SendKeysAsync(cp, first, paneId, "y").WaitAsync(Poll.HangGuard);
            await cp.ShellGeometryWorkIdleAsync().WaitAsync(Poll.HangGuard);
            Assert.Equal(resizes, factory.ResizeCount);
        }
        finally
        {
            factory.ReleaseResizes();
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private static async Task<(ControlPlaneService Cp, string PaneId, string TabId)> SeedPaneAsync(
        IPaneRuntimeFactory factory)
    {
        var state = new AppState(SessionId.New("shell-geometry-off-key-path"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var cp = new ControlPlaneService(
            state,
            factory,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            subscriptions: new EventSubscriptionHub());
        var created = await cp.DispatchAsync(
            ProtocolMethods.WorkspaceCreate,
            Params(new JsonObject
            {
                ["cwd"] = Path.GetTempPath(),
                ["create_pane"] = true,
            }),
            CancellationToken.None);
        var snap = await cp.DispatchAsync(
            ProtocolMethods.SessionSnapshot, parameters: null, CancellationToken.None);
        var paneId = created.GetProperty("pane").GetProperty("pane_id").GetString();
        var tabId = snap.GetProperty("focused_tab_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(paneId));
        Assert.False(string.IsNullOrWhiteSpace(tabId));
        return (cp, paneId!, tabId!);
    }

    private static async Task HelloAndActivateAsync(
        ControlPlaneService cp,
        IClientConnection connection,
        string clientId,
        ushort columns,
        ushort rows)
    {
        await cp.HandleAttachHelloAsync(
            new AttachEndpointHello
            {
                EndpointGeneration = AttachEndpointProtocol.EndpointGeneration,
                ProtocolMajor = 1,
                ProtocolMinor = 1,
                ClientId = clientId,
                Geometry = new AttachGeometry
                {
                    Columns = columns,
                    Rows = rows,
                    CellWidthPx = 8,
                    CellHeightPx = 16,
                    GeometryRevision = 1,
                },
                SurfaceActive = false,
                SnapshotCodecs = [AttachEndpointProtocol.SnapshotCodec],
                SurfaceCodecs = [AttachEndpointProtocol.SurfaceCodec],
                InputCodecs = [AttachEndpointProtocol.InputCodec],
                RequiredCapabilities = AttachEndpointProtocol.RequiredCapabilities.ToArray(),
            },
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

    private static Task<JsonElement> FocusAsync(
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

    private static Task<JsonElement> SendKeysAsync(
        ControlPlaneService cp,
        IClientConnection connection,
        string paneId,
        string text) =>
        cp.DispatchAsync(
            ProtocolMethods.PaneSendKeys,
            Params(new JsonObject
            {
                ["pane_id"] = paneId,
                ["encoding"] = "utf8",
                ["data"] = text,
            }),
            connection,
            CancellationToken.None);

    private static JsonElement Params(JsonObject obj)
    {
        using var doc = JsonDocument.Parse(obj.ToJsonString());
        return doc.RootElement.Clone();
    }

    private sealed class FakeConnection(string id) : IClientConnection
    {
        public string ConnectionId { get; } = id;

        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A pane factory whose resizes can be held until the test releases them.</summary>
    private sealed class GatedPaneFactory : IPaneRuntimeFactory
    {
        private readonly object _gate = new();
        private readonly List<string> _writes = [];
        private readonly Dictionary<string, (int Cols, int Rows)> _sizes = new(StringComparer.Ordinal);
        private TaskCompletionSource? _release;
        private TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _resizeCount;

        public IReadOnlyList<string> Writes
        {
            get { lock (_gate) return [.. _writes]; }
        }

        public int ResizeCount => Volatile.Read(ref _resizeCount);

        /// <summary>Completes when a held resize has started.</summary>
        public Task ResizeHeld
        {
            get { lock (_gate) return _held.Task; }
        }

        public void HoldResizes()
        {
            lock (_gate)
            {
                _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void ReleaseResizes()
        {
            TaskCompletionSource? release;
            lock (_gate)
            {
                release = _release;
                _release = null;
            }

            release?.TrySetResult();
        }

        public (int Cols, int Rows)? LastSize(string paneId)
        {
            lock (_gate)
                return _sizes.TryGetValue(paneId, out var size) ? size : null;
        }

        public IPaneRuntime Create(PaneSpawnOptions options) => new GatedPaneRuntime(this, options.Id);

        private void RecordWrite(string text)
        {
            lock (_gate)
                _writes.Add(text);
        }

        private async ValueTask ResizeAsync(string paneId, int cols, int rows, CancellationToken ct)
        {
            Interlocked.Increment(ref _resizeCount);
            Task? wait = null;
            lock (_gate)
            {
                if (_release is { } release)
                {
                    _held.TrySetResult();
                    wait = release.Task;
                }
            }

            if (wait is not null)
                await wait.WaitAsync(ct).ConfigureAwait(false);
            lock (_gate)
                _sizes[paneId] = (cols, rows);
        }

        private sealed class GatedPaneRuntime(GatedPaneFactory owner, PaneId id) : IPaneRuntime
        {
            public PaneId Id { get; } = id;
            public bool IsAlive { get; private set; } = true;
            public int? ExitCode { get; private set; }
            public int? Pid { get; private set; } = 42_396;
#pragma warning disable CS0067
            public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
            public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
            public event Action<IPaneRuntime, int>? Exited;
#pragma warning restore CS0067

            public Task StartAsync(CancellationToken ct)
            {
                IsAlive = true;
                return Task.CompletedTask;
            }

            public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
            {
                ObjectDisposedException.ThrowIf(!IsAlive, this);
                owner.RecordWrite(Encoding.UTF8.GetString(data.Span));
                return ValueTask.CompletedTask;
            }

            public ValueTask WriteTextAsync(string text, CancellationToken ct)
            {
                ObjectDisposedException.ThrowIf(!IsAlive, this);
                owner.RecordWrite(text);
                return ValueTask.CompletedTask;
            }

            public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct)
            {
                ObjectDisposedException.ThrowIf(!IsAlive, this);
                return owner.ResizeAsync(Id.Value, cols, rows, ct);
            }

            public string ReadVisibleText() => "";
            public string ReadRecentText(int maxLines) => "";
            public string ReadRecentUnwrappedText(int maxLines) => "";
            public string ReadDetectionText() => "";

            public ValueTask DisposeAsync()
            {
                IsAlive = false;
                return ValueTask.CompletedTask;
            }
        }
    }
}
