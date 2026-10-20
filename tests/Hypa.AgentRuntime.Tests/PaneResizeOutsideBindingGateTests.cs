using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// A pane resize runs the runtime resize outside the binding gate. A key
/// for another pane does not wait for it. The resize stays serialized with
/// other resizes, with an occupant swap, with a close and with the
/// checkpoint freeze.
/// </summary>
[Collection("CheckpointTests")]
public sealed class PaneResizeOutsideBindingGateTests : IAsyncLifetime, IDisposable
{
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(5);
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;
    private readonly List<ControlPlaneService> _planes = [];

    public PaneResizeOutsideBindingGateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-resize-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var cp in _planes)
        {
            try { await cp.ShutdownAsync(CancellationToken.None); }
            catch { /* teardown */ }
        }

        _planes.Clear();
    }

    public void Dispose() => SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);

    [Fact]
    public async Task A_key_for_one_pane_is_admitted_while_the_resize_of_another_pane_is_held()
    {
        var factory = new HeldResizeFactory();
        var (cp, state, first) = await NewPlaneAsync(factory);
        var second = await SplitAsync(cp, first);
        var owner = new FakeConnection("conn_a");
        var input = await ClaimAsync(cp, owner, first, "input");
        var resize = await ClaimAsync(cp, owner, second, "resize");
        factory.Hold(second);
        try
        {
            var resizing = ResizeAsync(cp, owner, second, resize, 100, 30);
            await factory.Held.WaitAsync(Poll.HangGuard);

            var reply = await SendKeysAsync(cp, owner, first, input, "x").WaitAsync(Prompt);
            Assert.Equal(1, reply.GetProperty("accepted_bytes").GetInt32());
            await Poll.UntilAsync(() => factory.Writes.Contains("x"), "the key write");
            Assert.False(resizing.IsCompleted);

            factory.Release();
            await resizing.WaitAsync(Poll.HangGuard);
            Assert.Equal(100, state.GetPane(new PaneId(second))!.Cols);
            Assert.Equal((100, 30), factory.CurrentRuntimeSize(second));
        }
        finally
        {
            factory.Release();
        }
    }

    [Fact]
    public async Task An_agent_start_during_a_resize_leaves_the_new_occupant_at_the_resized_size()
    {
        var factory = new HeldResizeFactory();
        var (cp, state, pane) = await NewPlaneAsync(factory);
        var owner = new FakeConnection("conn_a");
        var resize = await ClaimAsync(cp, owner, pane, "resize");
        factory.Hold(pane);
        try
        {
            var resizing = ResizeAsync(cp, owner, pane, resize, 100, 30);
            await factory.Held.WaitAsync(Poll.HangGuard);
            var starting = cp.DispatchAsync(
                ProtocolMethods.AgentStart,
                Params(new JsonObject { ["pane_id"] = pane, ["kind"] = "muse" }),
                CancellationToken.None);

            factory.Release();
            await resizing.WaitAsync(Poll.HangGuard);
            await starting.WaitAsync(Poll.HangGuard);

            Assert.Equal(2, factory.CreatedFor(pane));
            Assert.Equal((100, 30), factory.CurrentRuntimeSize(pane));
            Assert.Equal(100, state.GetPane(new PaneId(pane))!.Cols);
            Assert.Equal(30, state.GetPane(new PaneId(pane))!.Rows);
        }
        finally
        {
            factory.Release();
        }
    }

    [Fact]
    public async Task A_pane_close_during_a_resize_ends_the_resize_with_not_found()
    {
        var factory = new HeldResizeFactory();
        var (cp, state, first) = await NewPlaneAsync(factory);
        var second = await SplitAsync(cp, first);
        var owner = new FakeConnection("conn_a");
        var resize = await ClaimAsync(cp, owner, second, "resize");
        factory.Hold(second);
        try
        {
            var resizing = ResizeAsync(cp, owner, second, resize, 100, 30);
            await factory.Held.WaitAsync(Poll.HangGuard);
            await cp.DispatchAsync(
                    ProtocolMethods.PaneClose,
                    Params(new JsonObject { ["pane_id"] = second }),
                    CancellationToken.None)
                .WaitAsync(Prompt);

            factory.Release();
            var failed = await Assert.ThrowsAsync<ControlPlaneException>(() => resizing.WaitAsync(Poll.HangGuard));
            Assert.Equal(ProtocolErrorCodes.NotFound, failed.Code);
            Assert.Null(state.GetPane(new PaneId(second)));
            Assert.Equal(0, factory.ResizesAfterDispose);
        }
        finally
        {
            factory.Release();
        }
    }

    [Fact]
    public async Task Two_resizes_of_one_pane_end_at_the_size_of_the_later_resize()
    {
        var factory = new HeldResizeFactory();
        var (cp, state, pane) = await NewPlaneAsync(factory);
        var owner = new FakeConnection("conn_a");
        var resize = await ClaimAsync(cp, owner, pane, "resize");
        factory.Hold(pane);
        try
        {
            var earlier = ResizeAsync(cp, owner, pane, resize, 100, 30);
            await factory.Held.WaitAsync(Poll.HangGuard);
            var later = ResizeAsync(cp, owner, pane, resize, 90, 20);

            factory.Release();
            await earlier.WaitAsync(Poll.HangGuard);
            await later.WaitAsync(Poll.HangGuard);

            Assert.Equal((90, 20), factory.CurrentRuntimeSize(pane));
            Assert.Equal(90, state.GetPane(new PaneId(pane))!.Cols);
            Assert.Equal(20, state.GetPane(new PaneId(pane))!.Rows);
        }
        finally
        {
            factory.Release();
        }
    }

    [Fact]
    public async Task A_checkpoint_prepare_waits_until_the_resize_in_progress_ends()
    {
        var factory = new HeldResizeFactory();
        var (cp, state, pane) = await NewPlaneAsync(factory, withCheckpoints: true);
        var owner = new FakeConnection("conn_a");
        var resize = await ClaimAsync(cp, owner, pane, "resize");
        factory.Hold(pane);
        try
        {
            var resizing = ResizeAsync(cp, owner, pane, resize, 100, 30);
            await factory.Held.WaitAsync(Poll.HangGuard);
            var preparing = cp.DispatchAsync(
                ProtocolMethods.RuntimeCheckpointPrepare,
                JsonDocument.Parse("""{"reason":"test","include_workspace_files":false}""").RootElement,
                CancellationToken.None);

            // The prepare holds the checkpoint gate. Its next step is the wait
            // for the geometry gate, which the held resize holds.
            await Poll.UntilAsync(() => CheckpointGateHeld(cp), "the prepare to take the checkpoint gate");
            Assert.False(preparing.IsCompleted);
            Assert.False(SessionLifecycle.IsFrozen(state.Snapshot().LifecycleState));

            factory.Release();
            await resizing.WaitAsync(Poll.HangGuard);
            var prepared = await preparing.WaitAsync(Poll.HangGuard);
            Assert.Equal("prepared", prepared.GetProperty("state").GetString());
            Assert.Equal(100, state.GetPane(new PaneId(pane))!.Cols);
            Assert.Equal((100, 30), factory.CurrentRuntimeSize(pane));
        }
        finally
        {
            factory.Release();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_hide_during_a_shell_tab_resize_ends_as_a_hide_after_the_resize(
        bool hideDuringItsOwnResize)
    {
        var factory = new HeldResizeFactory();
        var (cp, state, first) = await NewPlaneAsync(factory);
        var second = await SplitAsync(cp, first);
        var tabId = state.GetPane(new PaneId(first))!.TabId.Value;
        var shell = new FakeConnection("conn_shell");
        var other = new FakeConnection("conn_other");
        await HelloAndActivateAsync(cp, shell, "cli_shell", columns: 120, rows: 40);
        await cp.HandleAttachFocusAsync(
            new AttachFocusRequest
            {
                RequestId = "focus",
                ClientId = "cli_shell",
                Focused = true,
                TabId = tabId,
                PaneId = first,
            },
            shell,
            CancellationToken.None);
        Assert.True(cp.TabGeometry.IsController(shell.ConnectionId, tabId));
        var hideLease = await ClaimAsync(cp, other, second, "input");
        var before = factory.CurrentRuntimeSize(second);

        // Hold the resize of the first pane: the apply has read its targets
        // and the second pane is next. Or hold the resize of the second pane:
        // the hide runs while its runtime resize is in progress.
        factory.Hold(hideDuringItsOwnResize ? second : first);
        try
        {
            var applying = cp.HandleAttachResizeAsync(
                new AttachResizeRequest
                {
                    RequestId = "resize",
                    ClientId = "cli_shell",
                    Geometry = Geometry(100, 30, revision: 2),
                },
                shell,
                CancellationToken.None);
            await factory.Held.WaitAsync(Poll.HangGuard);
            Assert.Equal(before, factory.CurrentRuntimeSize(second));

            await cp.DispatchAsync(
                    ProtocolMethods.PaneHide,
                    Params(new JsonObject { ["pane_id"] = second, ["lease_id"] = hideLease }),
                    other,
                    CancellationToken.None)
                .WaitAsync(Prompt);
            Assert.Equal(PanePlacement.Hidden, state.GetPane(new PaneId(second))!.Placement);

            factory.Release();
            await applying.WaitAsync(Poll.HangGuard);

            // The apply read its sizes before the hide. The end state is that
            // of a hide just after the apply: the hidden pane keeps the size
            // of the apply, and its PTY size equals its state size.
            var applied = factory.Requests(second)[^1];
            Assert.NotEqual(before, applied);
            Assert.Equal(applied, factory.CurrentRuntimeSize(second));
            Assert.Equal(applied.Cols, state.GetPane(new PaneId(second))!.Cols);
            Assert.Equal(applied.Rows, state.GetPane(new PaneId(second))!.Rows);
        }
        finally
        {
            factory.Release();
        }
    }

    [Fact]
    public async Task A_split_ratio_change_during_a_shell_tab_resize_leaves_each_pane_at_the_size_of_the_apply()
    {
        var factory = new HeldResizeFactory();
        var (cp, state, first) = await NewPlaneAsync(factory);
        var second = await SplitAsync(cp, first);
        var tabId = state.GetPane(new PaneId(first))!.TabId.Value;
        var shell = new FakeConnection("conn_shell");
        var other = new FakeConnection("conn_other");
        await HelloAndActivateAsync(cp, shell, "cli_shell", columns: 120, rows: 40);
        await cp.HandleAttachFocusAsync(
            new AttachFocusRequest
            {
                RequestId = "focus",
                ClientId = "cli_shell",
                Focused = true,
                TabId = tabId,
                PaneId = first,
            },
            shell,
            CancellationToken.None);
        var tab = state.GetTab(new TabId(tabId))!;
        var leasePane = tab.FocusedPaneId?.Value ?? tab.PaneIds[0].Value;
        var layoutLease = await ClaimAsync(cp, other, leasePane, "resize");
        factory.Hold(second);
        try
        {
            var applying = cp.HandleAttachResizeAsync(
                new AttachResizeRequest
                {
                    RequestId = "resize",
                    ClientId = "cli_shell",
                    Geometry = Geometry(100, 30, revision: 2),
                },
                shell,
                CancellationToken.None);
            await factory.Held.WaitAsync(Poll.HangGuard);
            var held = factory.Requests(second)[^1];

            await cp.DispatchAsync(
                    ProtocolMethods.LayoutSetSplitRatio,
                    Params(new JsonObject
                    {
                        ["tab_id"] = tabId,
                        ["path"] = new JsonArray(),
                        ["ratio"] = 0.3,
                        ["lease_id"] = layoutLease,
                    }),
                    other,
                    CancellationToken.None)
                .WaitAsync(Prompt);

            factory.Release();
            await applying.WaitAsync(Poll.HangGuard);

            // Both panes have the sizes of one layout: the layout that the
            // apply read. The client sends the sizes of the new layout after
            // its layout.updated event. The PTY size equals the state size.
            Assert.Equal(held, factory.CurrentRuntimeSize(second));
            Assert.Equal(factory.Requests(first)[^1], factory.CurrentRuntimeSize(first));
            Assert.Equal(100, factory.CurrentRuntimeSize(first).Cols + factory.CurrentRuntimeSize(second).Cols);
            foreach (var pane in new[] { first, second })
            {
                Assert.Equal(factory.CurrentRuntimeSize(pane).Cols, state.GetPane(new PaneId(pane))!.Cols);
                Assert.Equal(factory.CurrentRuntimeSize(pane).Rows, state.GetPane(new PaneId(pane))!.Rows);
            }
        }
        finally
        {
            factory.Release();
        }
    }

    [Fact]
    public async Task A_pane_runtime_does_not_resize_its_terminal_after_dispose()
    {
        var engines = new RecordingVtEngineFactory();
        var factory = TestPaneFactories.Create(new PaneIntelligencePipeline(), vtEngineFactory: engines);
        var runtime = factory.Create(new PaneSpawnOptions
        {
            Id = PaneId.New(),
            Cwd = Path.GetTempPath(),
            Command = "/bin/sh",
            Cols = 80,
            Rows = 24,
        });
        await runtime.StartAsync(CancellationToken.None);
        await runtime.DisposeAsync();

        await runtime.ResizeAsync(100, 30, CancellationToken.None);

        var engine = Assert.Single(engines.Created);
        Assert.True(engine.IsDisposed);
        Assert.Equal(80, engine.Cols);
    }

    private async Task<(ControlPlaneService Cp, AppState State, string PaneId)> NewPlaneAsync(
        IPaneRuntimeFactory factory,
        bool withCheckpoints = false)
    {
        var state = new AppState(SessionId.New("resize-outside-binding-gate"));
        state.UpdateSession(s => s with
        {
            Name = "resize-outside-binding-gate",
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });

        ControlPlaneService cp;
        if (withCheckpoints)
        {
            var migrator = new SqliteRuntimeSchemaMigrator(_paths);
            Assert.True((await migrator.MigrateAsync()).IsOk);
            var store = new SqliteRuntimeSessionStore(_paths);
            var manifests = new SqliteJournalManifestStore(_paths);
            Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
            var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
            Assert.True((await journal.RecoverAsync()).IsOk);
            var checkpoints = new DefaultCheckpointService(
                new FileCheckpointStore(_paths),
                new DefaultCheckpointArtifactBuilder());
            cp = new ControlPlaneService(
                state,
                factory,
                new PaneIntelligencePipeline(),
                new HeuristicAgentDetector(),
                store: store,
                journal: journal,
                subscriptions: new EventSubscriptionHub(),
                redactor: new DefaultEventPayloadRedactor(),
                checkpoints: checkpoints);
        }
        else
        {
            cp = new ControlPlaneService(
                state,
                factory,
                new PaneIntelligencePipeline(),
                new HeuristicAgentDetector(),
                subscriptions: new EventSubscriptionHub());
        }

        _planes.Add(cp);
        var created = await cp.DispatchAsync(
            ProtocolMethods.WorkspaceCreate,
            Params(new JsonObject
            {
                ["cwd"] = Path.GetTempPath(),
                ["command"] = "/bin/sh",
                ["create_pane"] = true,
            }),
            CancellationToken.None);
        var paneId = created.GetProperty("pane").GetProperty("pane_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(paneId));
        return (cp, state, paneId!);
    }

    private static async Task<string> SplitAsync(ControlPlaneService cp, string paneId)
    {
        var split = await cp.DispatchAsync(
            ProtocolMethods.PaneSplit,
            Params(new JsonObject { ["pane_id"] = paneId, ["direction"] = "right", ["command"] = "/bin/sh" }),
            CancellationToken.None);
        return split.GetProperty("pane_id").GetString()!;
    }

    /// <summary>True when a checkpoint prepare holds the checkpoint gate of the plane.</summary>
    private static bool CheckpointGateHeld(ControlPlaneService cp)
    {
        var gate = (SemaphoreSlim)typeof(ControlPlaneService)
            .GetField("_checkpointGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cp)!;
        return gate.CurrentCount == 0;
    }

    private static async Task<string> ClaimAsync(
        ControlPlaneService cp,
        IClientConnection connection,
        string paneId,
        string scope)
    {
        var claim = await cp.DispatchAsync(
            ProtocolMethods.RuntimeLeaseClaim,
            Params(new JsonObject { ["pane_id"] = paneId, ["scope"] = scope }),
            connection,
            CancellationToken.None);
        return claim.GetProperty("lease_id").GetString()!;
    }

    private static Task<JsonElement> ResizeAsync(
        ControlPlaneService cp,
        IClientConnection connection,
        string paneId,
        string leaseId,
        int cols,
        int rows) =>
        cp.DispatchAsync(
            ProtocolMethods.PaneResize,
            Params(new JsonObject
            {
                ["pane_id"] = paneId,
                ["lease_id"] = leaseId,
                ["cols"] = cols,
                ["rows"] = rows,
            }),
            connection,
            CancellationToken.None);

    private static Task<JsonElement> SendKeysAsync(
        ControlPlaneService cp,
        IClientConnection connection,
        string paneId,
        string leaseId,
        string text) =>
        cp.DispatchAsync(
            ProtocolMethods.PaneSendKeys,
            Params(new JsonObject
            {
                ["pane_id"] = paneId,
                ["lease_id"] = leaseId,
                ["encoding"] = "utf8",
                ["data"] = text,
            }),
            connection,
            CancellationToken.None);

    private static AttachGeometry Geometry(ushort columns, ushort rows, ulong revision) => new()
    {
        Columns = columns,
        Rows = rows,
        CellWidthPx = 8,
        CellHeightPx = 16,
        GeometryRevision = revision,
    };

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
                Geometry = Geometry(columns, rows, revision: 1),
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

    /// <summary>Creates a stub VT engine for each pane and keeps it for the test.</summary>
    private sealed class RecordingVtEngineFactory : IVtEngineFactory
    {
        private readonly List<StubVtEngine> _created = [];

        public VtProviderSelection Selection { get; } = new()
        {
            Provider = VtProviderKind.Ghostty,
            RequireGhostty = true,
        };

        public string ProviderWireName => "ghostty";

        public IReadOnlyList<StubVtEngine> Created
        {
            get { lock (_created) return [.. _created]; }
        }

        public IVtEngine Create(int cols, int rows)
        {
            var engine = new StubVtEngine(cols, rows);
            lock (_created)
                _created.Add(engine);
            return engine;
        }
    }

    /// <summary>
    /// A pane factory that can hold the resizes of one pane until the test
    /// releases them. It records the size of each runtime it creates.
    /// </summary>
    private sealed class HeldResizeFactory : IPaneRuntimeFactory
    {
        private readonly object _gate = new();
        private readonly List<string> _writes = [];
        private readonly Dictionary<string, HeldRuntime> _current = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _created = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<(int Cols, int Rows)>> _requests = new(StringComparer.Ordinal);
        private string? _heldPane;
        private TaskCompletionSource? _release;
        private TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _resizesAfterDispose;

        public IReadOnlyList<string> Writes
        {
            get { lock (_gate) return [.. _writes]; }
        }

        /// <summary>Resizes that started on a runtime that was already disposed.</summary>
        public int ResizesAfterDispose
        {
            get { lock (_gate) return _resizesAfterDispose; }
        }

        /// <summary>Completes when a held resize has started.</summary>
        public Task Held
        {
            get { lock (_gate) return _held.Task; }
        }

        public void Hold(string paneId)
        {
            lock (_gate)
            {
                _heldPane = paneId;
                _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void Release()
        {
            TaskCompletionSource? release;
            lock (_gate)
            {
                release = _release;
                _release = null;
                _heldPane = null;
            }

            release?.TrySetResult();
        }

        /// <summary>Each size that a resize asked for, in order.</summary>
        public IReadOnlyList<(int Cols, int Rows)> Requests(string paneId)
        {
            lock (_gate)
                return _requests.TryGetValue(paneId, out var list) ? [.. list] : [];
        }

        public int CreatedFor(string paneId)
        {
            lock (_gate)
                return _created.TryGetValue(paneId, out var count) ? count : 0;
        }

        /// <summary>The size of the runtime that the factory created last for the pane.</summary>
        public (int Cols, int Rows) CurrentRuntimeSize(string paneId)
        {
            lock (_gate)
                return _current[paneId].Size;
        }

        public IPaneRuntime Create(PaneSpawnOptions options)
        {
            var runtime = new HeldRuntime(this, options.Id, (options.Cols, options.Rows));
            lock (_gate)
            {
                _current[options.Id.Value] = runtime;
                _created[options.Id.Value] = (_created.TryGetValue(options.Id.Value, out var count) ? count : 0) + 1;
            }

            return runtime;
        }

        private void RecordWrite(string text)
        {
            lock (_gate)
                _writes.Add(text);
        }

        private async ValueTask ResizeAsync(HeldRuntime runtime, int cols, int rows, CancellationToken ct)
        {
            Task? wait = null;
            lock (_gate)
            {
                if (!_requests.TryGetValue(runtime.Id.Value, out var list))
                    _requests[runtime.Id.Value] = list = [];
                list.Add((cols, rows));
                if (runtime.Disposed)
                {
                    _resizesAfterDispose++;
                    return;
                }

                if (_release is { } release
                    && string.Equals(_heldPane, runtime.Id.Value, StringComparison.Ordinal))
                {
                    _held.TrySetResult();
                    wait = release.Task;
                }
            }

            if (wait is not null)
                await wait.WaitAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                // A resize that started before the dispose can end after it.
                if (!runtime.Disposed)
                    runtime.Size = (cols, rows);
            }
        }

        private void MarkDisposed(HeldRuntime runtime)
        {
            lock (_gate)
                runtime.Disposed = true;
        }

        private sealed class HeldRuntime(HeldResizeFactory owner, PaneId id, (int Cols, int Rows) size) : IPaneRuntime
        {
            public PaneId Id { get; } = id;
            public bool IsAlive { get; private set; } = true;
            public int? ExitCode { get; private set; }
            public int? Pid { get; private set; } = 42_018;

            /// <summary>Guarded by the lock of the owner factory.</summary>
            public (int Cols, int Rows) Size { get; set; } = size;

            /// <summary>Guarded by the lock of the owner factory.</summary>
            public bool Disposed { get; set; }

#pragma warning disable CS0067
            public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
            public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
            public event Action<IPaneRuntime, int>? Exited;
#pragma warning restore CS0067

            public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

            public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
            {
                owner.RecordWrite(Encoding.UTF8.GetString(data.Span));
                return ValueTask.CompletedTask;
            }

            public ValueTask WriteTextAsync(string text, CancellationToken ct)
            {
                owner.RecordWrite(text);
                return ValueTask.CompletedTask;
            }

            public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct) =>
                owner.ResizeAsync(this, cols, rows, ct);

            public string ReadVisibleText() => "";
            public string ReadRecentText(int maxLines) => "";
            public string ReadRecentUnwrappedText(int maxLines) => "";
            public string ReadDetectionText() => "";

            public ValueTask DisposeAsync()
            {
                IsAlive = false;
                owner.MarkDisposed(this);
                return ValueTask.CompletedTask;
            }
        }
    }
}
