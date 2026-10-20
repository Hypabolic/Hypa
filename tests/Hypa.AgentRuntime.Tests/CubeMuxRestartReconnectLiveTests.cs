using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Sidebar;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// A live attach shows a cube tab with two tiled panes. The cube mux
/// restarts: a second <see cref="ControlPlaneService"/> restores the same
/// pane ids from the same state, at a size that is not the client box, and
/// its frame generations start again. The attach then commits a new
/// connection to the same cube, as the reconnect does, and runs the commit
/// chrome refresh. The render events of each mux go through the attach
/// render path.
/// </summary>
[Trait("Category", "Ghostty")]
[Collection("GhosttyPtyTests")]
public sealed class CubeMuxRestartReconnectLiveTests
{
    private const string CubeId = "plc_restart";

    // The attach keeps its endpoint client id across a redial.
    private const string EndpointClientId = "cube-client";
    private const int HostCols = 100;
    private const int HostRows = 30;

    // The old mux paints each pane this many times before the restart, so
    // its frame generations are well above the first generations of the
    // restored mux.
    private const int OldPaints = 30;

    private readonly string? _lib = GhosttyTestRequire.TryResolveNativeLibraryPath();

    [SkippableFact]
    public async Task Reconnect_to_a_restarted_cube_mux_sends_each_visible_pane_its_box_size()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var run = await RestartScenario.RunAsync(_lib);

        Assert.Equal(run.BoxA, run.Lab.PaneSize(run.Lab.PaneA));
        Assert.Equal(run.BoxB, run.Lab.PaneSize(run.Lab.PaneB));
    }

    [SkippableFact]
    public async Task Reconnect_to_a_restarted_cube_mux_repaints_new_output_of_both_visible_panes()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var run = await RestartScenario.RunAsync(_lib);

        await run.Lab.FeedAsync(run.NewMux.Cp, run.Lab.PaneA, "new-a\r\n");
        await run.Lab.FeedAsync(run.NewMux.Cp, run.Lab.PaneB, "new-b\r\n");
        await run.View.PumpAsync(run.NewMux);

        var text = run.View.HostText();
        Assert.True(text.Contains("new-a", StringComparison.Ordinal), run.View.FrameLog());
        Assert.True(text.Contains("new-b", StringComparison.Ordinal), run.View.FrameLog());
    }

    [SkippableFact]
    public async Task Reconnect_to_a_restarted_cube_mux_observes_each_pane_without_a_reanchor_storm()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var run = await RestartScenario.RunAsync(_lib);

        await run.Lab.FeedAsync(run.NewMux.Cp, run.Lab.PaneA, "new-a\r\n");
        await run.Lab.FeedAsync(run.NewMux.Cp, run.Lab.PaneB, "new-b\r\n");
        await run.View.PumpAsync(run.NewMux);

        // Each pane gets its observe after the commit, and at most one
        // reanchor request more.
        var port = run.NewMux.Port;
        var observesA = port.ObservesSince(run.CommitMark, run.Lab.PaneA);
        var observesB = port.ObservesSince(run.CommitMark, run.Lab.PaneB);
        Assert.True(observesA is >= 1 and <= 2, port.Describe(run.CommitMark));
        Assert.True(observesB is >= 1 and <= 2, port.Describe(run.CommitMark));
    }

    [SkippableFact]
    public async Task Reconnect_to_the_same_cube_mux_observes_both_panes_again_and_keeps_their_frames()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var lab = await Lab.OpenAsync(_lib);
        using var view = new View(lab.PaneA);
        var first = await lab.ConnectAsync(lab.OldCp, EndpointClientId);
        await view.CommitAsync(first);
        await lab.FeedAsync(lab.OldCp, lab.PaneA, "before-a\r\n");
        await lab.FeedAsync(lab.OldCp, lab.PaneB, "before-b\r\n");
        await view.PumpAsync(first);
        Assert.Contains("before-b", view.HostText(), StringComparison.Ordinal);

        // The link drops and the attach dials the same mux again: the same
        // boot id, a new connection and a new subscription.
        var second = await lab.ConnectAsync(lab.OldCp, EndpointClientId);
        Assert.Equal(first.BootId, second.BootId);
        Assert.NotEqual(first.Generation, second.Generation);
        var mark = second.Port.CallCount;
        await view.CommitAsync(second);

        // The frames of the same mux stay on the screen until the new
        // subscription sends its frames.
        Assert.Contains("before-b", view.HostText(), StringComparison.Ordinal);

        await view.PumpAsync(second);
        await lab.FeedAsync(lab.OldCp, lab.PaneA, "after-a\r\n");
        await lab.FeedAsync(lab.OldCp, lab.PaneB, "after-b\r\n");
        await view.PumpAsync(second);

        var text = view.HostText();
        Assert.True(text.Contains("after-a", StringComparison.Ordinal), view.FrameLog());
        Assert.True(text.Contains("after-b", StringComparison.Ordinal), view.FrameLog());
        Assert.True(second.Port.ObservesSince(mark, lab.PaneA) is >= 1 and <= 2, second.Port.Describe(mark));
        Assert.True(second.Port.ObservesSince(mark, lab.PaneB) is >= 1 and <= 2, second.Port.Describe(mark));
    }

    /// <summary>
    /// The attach shows the two cube panes, the cube mux restarts, and the
    /// attach commits the new connection and runs the commit refresh.
    /// </summary>
    private sealed class RestartScenario : IAsyncDisposable
    {
        private RestartScenario(Lab lab, View view)
        {
            Lab = lab;
            View = view;
        }

        public Lab Lab { get; }

        public View View { get; }

        public MuxConnection NewMux { get; private set; } = null!;

        public (int Cols, int Rows) BoxA { get; private set; }

        public (int Cols, int Rows) BoxB { get; private set; }

        /// <summary>The call mark of the new connection at the commit.</summary>
        public int CommitMark { get; private set; }

        public static async Task<RestartScenario> RunAsync(string? lib)
        {
            var lab = await Lab.OpenAsync(lib);
            var run = new RestartScenario(lab, new View(lab.PaneA));
            try
            {
                await run.RunCoreAsync();
                return run;
            }
            catch
            {
                await run.DisposeAsync();
                throw;
            }
        }

        private async Task RunCoreAsync()
        {
            // The first connect to the cube.
            var oldMux = await Lab.ConnectAsync(Lab.OldCp, EndpointClientId);
            await View.CommitAsync(oldMux);
            var boxA = View.Box(Lab.PaneA);
            var boxB = View.Box(Lab.PaneB);
            BoxA = (boxA.Cols, boxA.Rows);
            BoxB = (boxB.Cols, boxB.Rows);
            Assert.Equal(BoxA, Lab.PaneSize(Lab.PaneA));
            Assert.Equal(BoxB, Lab.PaneSize(Lab.PaneB));
            for (var i = 0; i < OldPaints; i++)
            {
                await Lab.FeedAsync(Lab.OldCp, Lab.PaneA, "old-a-" + i + "\r\n");
                await Lab.FeedAsync(Lab.OldCp, Lab.PaneB, "old-b-" + i + "\r\n");
                await View.PumpAsync(oldMux);
            }

            var text = View.HostText();
            Assert.Contains("old-a-" + (OldPaints - 1), text, StringComparison.Ordinal);
            Assert.Contains("old-b-" + (OldPaints - 1), text, StringComparison.Ordinal);
            Assert.True(View.Live.LastAppliedBlit(Lab.PaneA).Generation > OldPaints);
            Assert.True(View.Live.LastAppliedBlit(Lab.PaneB).Generation > OldPaints);

            // The restart comes later than the resize grace of the last
            // pane resize of this client.
            View.Clock.Advance(TimeSpan.FromMinutes(1));

            // The cube mux restarts with the same pane ids. It sizes the
            // panes from its own geometry, not from the client boxes.
            var newCp = await Lab.RestartAsync();
            NewMux = await Lab.ConnectAsync(newCp, EndpointClientId);
            Assert.NotEqual(oldMux.BootId, NewMux.BootId);
            Assert.NotEqual(BoxA, Lab.PaneSize(Lab.PaneA));
            Assert.NotEqual(BoxB, Lab.PaneSize(Lab.PaneB));

            // The dest connect observes the focused pane on the new
            // subscription before the commit, as the connect prep does.
            await NewMux.Port.CallAsync(
                ProtocolMethods.TerminalVisibleSet,
                new JsonObject { ["subscription_id"] = NewMux.SubscriptionId, ["pane_ids"] = new JsonArray(Lab.PaneA) },
                CancellationToken.None);
            await NewMux.Port.CallAsync(
                ProtocolMethods.TerminalObserve,
                new JsonObject { ["pane_id"] = Lab.PaneA, ["subscription_id"] = NewMux.SubscriptionId, ["replace"] = true },
                CancellationToken.None);
            CommitMark = NewMux.Port.CallCount;
            await View.CommitAsync(NewMux);
            await View.PumpAsync(NewMux);
        }

        public async ValueTask DisposeAsync()
        {
            View.Dispose();
            await Lab.DisposeAsync();
        }
    }

    private static JsonElement Json(JsonObject obj) =>
        JsonDocument.Parse(obj.ToJsonString()).RootElement;

    private static AttachEndpointHello Hello(string clientId) =>
        new()
        {
            EndpointGeneration = AttachEndpointProtocol.EndpointGeneration,
            ProtocolMajor = (uint)ProtocolVersion.Major,
            ProtocolMinor = (uint)ProtocolVersion.Minor,
            ClientId = clientId,
            Geometry = new AttachGeometry
            {
                Columns = HostCols,
                Rows = HostRows,
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

    /// <summary>One connection of the attach to one mux instance.</summary>
    private sealed record MuxConnection(
        ControlPlaneService Cp,
        LaneSink Sink,
        MuxPort Port,
        string SubscriptionId,
        string BootId,
        ulong Generation)
    {
        public int Mark { get; set; }

        /// <summary>The event lines since the last call.</summary>
        public IReadOnlyList<string> TakeLines()
        {
            var lines = Sink.Lines;
            var taken = lines.Skip(Mark).ToArray();
            Mark = lines.Count;
            return taken;
        }
    }

    /// <summary>The cube state, the mux that serves it, and its restart.</summary>
    private sealed class Lab : IAsyncDisposable
    {
        private readonly string _dir;
        private readonly RuntimeStatePaths _paths;
        private readonly string? _lib;
        private readonly List<ControlPlaneService> _services = [];

        private Lab(string dir, RuntimeStatePaths paths, AppState state, string? lib)
        {
            _dir = dir;
            _paths = paths;
            State = state;
            _lib = lib;
        }

        public AppState State { get; }

        public ControlPlaneService OldCp { get; private set; } = null!;

        public string PaneA { get; private set; } = "";

        public string PaneB { get; private set; } = "";

        public string WorkspaceId { get; private set; } = "";

        public string TabId { get; private set; } = "";

        public static async Task<Lab> OpenAsync(string? lib)
        {
            var dir = Path.Combine(Path.GetTempPath(), "hypa-cube-restart-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var paths = new RuntimeStatePaths { StateDirectory = dir };
            var migrator = new SqliteRuntimeSchemaMigrator(paths);
            Assert.True((await migrator.MigrateAsync()).IsOk);
            var state = new AppState(SessionId.New("cube-restart"));
            state.UpdateSession(s => s with { Name = "cube-restart", LifecycleState = SessionLifecycle.Ready });
            state.CreateWorkspace(dir, label: "default");
            var store = new SqliteRuntimeSessionStore(paths);
            Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
            var lab = new Lab(dir, paths, state, lib);
            try
            {
                lab.OldCp = await lab.NewServiceAsync();
                var create = await lab.OldCp.DispatchAsync(
                    ProtocolMethods.PaneCreate,
                    Json(new JsonObject { ["command"] = "/bin/cat", ["cwd"] = dir }),
                    CancellationToken.None);
                lab.PaneA = create.GetProperty("pane_id").GetString()!;
                var split = await lab.OldCp.DispatchAsync(
                    ProtocolMethods.PaneSplit,
                    Json(new JsonObject
                    {
                        ["pane_id"] = lab.PaneA,
                        ["direction"] = "right",
                        ["command"] = "/bin/cat",
                        ["cwd"] = dir,
                    }),
                    CancellationToken.None);
                lab.PaneB = split.GetProperty("pane_id").GetString()!;
                var pane = state.GetPane(new PaneId(lab.PaneA))!;
                lab.WorkspaceId = pane.WorkspaceId.Value;
                lab.TabId = pane.TabId.Value;
                return lab;
            }
            catch
            {
                await lab.DisposeAsync();
                throw;
            }
        }

        private async Task<ControlPlaneService> NewServiceAsync()
        {
            var manifests = new SqliteJournalManifestStore(_paths);
            var journal = new FileRuntimeEventJournal(_paths, manifests, State.SessionId.Value);
            Assert.True((await journal.RecoverAsync()).IsOk);
            var factory = new PaneRuntimeFactory(
                new PaneIntelligencePipeline(),
                ptyFactory: TestPaneFactories.ProcessIo(),
                vtEngineFactory: new VtEngineFactory(new VtProviderSelection { LibraryPathOverride = _lib }));
            var cp = new ControlPlaneService(
                State,
                factory,
                new PaneIntelligencePipeline(),
                new HeuristicAgentDetector(),
                journal: journal,
                subscriptions: new EventSubscriptionHub());
            _services.Add(cp);
            return cp;
        }

        /// <summary>
        /// Stops the mux and starts a new one on the same state. The new mux
        /// restores the same pane ids, and its frame generations start again.
        /// </summary>
        public async Task<ControlPlaneService> RestartAsync()
        {
            await OldCp.ShutdownAsync(CancellationToken.None);
            var cp = await NewServiceAsync();
            await cp.RestoreSpawnAsync(CancellationToken.None);
            Assert.NotNull(cp.PeekRuntime(PaneA));
            Assert.NotNull(cp.PeekRuntime(PaneB));
            return cp;
        }

        public async Task<MuxConnection> ConnectAsync(ControlPlaneService cp, string clientId)
        {
            var sink = new LaneSink(clientId);
            var welcome = (await cp.HandleAttachHelloAsync(Hello(clientId), sink, CancellationToken.None))
                .Deserialize(ProtocolJsonContext.Default.AttachEndpointWelcome)!;
            await cp.HandleAttachSurfaceInterestAsync(
                new AttachSurfaceInterestRequest
                {
                    RequestId = "on",
                    ClientId = clientId,
                    Active = true,
                    GeometryRevision = 1,
                },
                sink,
                CancellationToken.None);
            var sub = await cp.DispatchAsync(
                ProtocolMethods.EventsSubscribe,
                // The subscribe of the dest connect: a fresh subscribe at the live edge.
                Json(AttachSession.BuildEventsSubscribeParams(["control", "lifecycle", "render"], null, snapshotPaint: true)),
                sink,
                CancellationToken.None);
            var subscriptionId = sub.GetProperty("subscription_id").GetString()!;
            await cp.CompleteEventsSubscribeAsync(subscriptionId, [], sink, CancellationToken.None);
            var connection = new MuxConnection(
                cp,
                sink,
                new MuxPort(cp, sink),
                subscriptionId,
                welcome.BootId,
                welcome.ConnectionGeneration ?? 0);
            connection.Mark = sink.Count;
            return connection;
        }

        public async Task FeedAsync(ControlPlaneService cp, string paneId, string text)
        {
            var runtime = Assert.IsType<PaneRuntime>(cp.PeekRuntime(paneId));
            runtime.PublishFedOutputForTests(text);
            await cp.WaitOutputDeliveredForTestsAsync(paneId);
            await PaintAsync(cp);
        }

        public async Task PaintAsync(ControlPlaneService cp)
        {
            foreach (var id in new[] { PaneA, PaneB })
            {
                await Poll.GuardAsync(cp.FlushCoalescedPaintForTestsAsync(id), "paint flush");
                await cp.WaitOutputDeliveredForTestsAsync(id);
            }
        }

        /// <summary>The size of the pane in the mux state.</summary>
        public (int Cols, int Rows) PaneSize(string paneId)
        {
            var pane = State.GetPane(new PaneId(paneId))!;
            return (pane.Cols, pane.Rows);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var cp in _services)
            {
                try
                {
                    await cp.ShutdownAsync(CancellationToken.None);
                }
                catch (ObjectDisposedException)
                {
                }
            }

            SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);
        }
    }

    /// <summary>The attach side: a chrome-on live state over an in-memory TTY.</summary>
    private sealed class View : IDisposable
    {
        private readonly UnixRawTerminal _tty = new(new MemoryStream(), HostCols, HostRows);
        private readonly SnapshotAssembler _assembler = new();

        public View(string focusedPaneId)
        {
            var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
            Live = new AttachLiveState
            {
                Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
                Table = table,
                Dispatcher = new AttachCommandDispatcher(new NullPort(), "", "", focusedPaneId, ""),
                Renew = new LeaseRenewLoop((_, _, _) => Task.CompletedTask),
                PaneId = focusedPaneId,
                ChromeEnabled = true,
                SidebarOpen = false,
                SidebarCollapsed = true,
                SessionName = "cube-restart",
                AttachClientId = "cli_restart",
                ProcessLog = new CapturingProcessLogSink(),
                Time = Clock,
                Cubes = [CubeItem],
                CubeCatalog = new FixedCatalog(),
            };
        }

        internal static SidebarCubeItem CubeItem { get; } = new()
        {
            Id = CubeId,
            Name = "cube",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Reachable,
        };

        public AttachLiveState Live { get; }

        public ManualClock Clock { get; } = new();

        public CellRect Box(string paneId) =>
            Assert.Single(Live.Chrome!.Panes, pane => pane.PaneId == paneId).Content;

        /// <summary>
        /// Commits <paramref name="mux"/> as the connection of the cube, then
        /// runs the commit chrome refresh: snapshot, layout, pane resize and
        /// the observe of the visible panes.
        /// </summary>
        public async Task CommitAsync(MuxConnection mux)
        {
            var snapshot = await mux.Port.CallAsync(ProtocolMethods.SessionSnapshot, null, CancellationToken.None);
            var outcome = new CubesConnectRetargetOutcome
            {
                Ok = true,
                Action = CubesConnectActions.Retargeted,
                DestinationKind = SidebarCubeKind.Peer,
                TransportKind = AttachEndpointKinds.Unix,
                SourceMuxAlive = true,
                NestedAttachBlocked = false,
                NestedAttachEnabled = false,
                SpawnsHypaAttach = false,
                WorkMoved = false,
                AllowNestedMutated = false,
                CalledServerStop = false,
                ProcessStartCount = 0,
                ProcessStartCommands = [],
                PaneProcessCommands = [],
                DestSnapshot = snapshot,
                DestPaneId = Live.PaneId,
                DestWorkspaceId = AttachSession.ChooseWorkspaceIdForPane(snapshot, Live.PaneId),
                DestTabId = AttachSession.ChooseTabIdForPane(snapshot, Live.PaneId),
                DestSubscribeId = mux.SubscriptionId,
                // The transport of the commit. The test sends its calls
                // through the render port below.
                DestClient = new ControlPlaneClient("/tmp/hypa-cube-restart-" + Guid.NewGuid().ToString("N")[..8] + ".sock"),
                TargetBootId = mux.BootId,
                TargetConnectionGeneration = mux.Generation,
            };
            AttachSession.ApplyCubesConnectOutcome(Live, mux.Port, CubeItem, outcome);
            Live.RenderPort = mux.Port;
            Live.Dispatcher.RebindPort(mux.Port);
            Live.CommitChromeRefreshPending = true;
            Assert.True(await AttachSession.TryFlushCommitChromeRefreshAsync(Live, _tty, controlGate: null, CancellationToken.None));
            Assert.NotNull(Live.Chrome);
        }

        /// <summary>
        /// Runs the render path of the attach on the events of
        /// <paramref name="mux"/> until no event and no reanchor request is
        /// left. The number of rounds is bounded.
        /// </summary>
        public async Task PumpAsync(MuxConnection mux)
        {
            var quiet = 0;
            for (var round = 0; round < 400 && quiet < 3; round++)
            {
                await mux.Port.IdleAsync();
                foreach (var id in Live.Chrome!.Panes.Select(pane => pane.PaneId))
                {
                    await Poll.GuardAsync(mux.Cp.FlushCoalescedPaintForTestsAsync(id), "paint flush");
                    await mux.Cp.WaitOutputDeliveredForTestsAsync(id);
                }

                var lines = mux.TakeLines();
                if (lines.Count == 0 && Live.ChainReanchors.InFlight == 0 && !Live.ChainReanchors.HasQueued)
                {
                    quiet++;
                    continue;
                }

                quiet = 0;
                foreach (var line in lines)
                {
                    using var doc = JsonDocument.Parse(line);
                    await AttachSession.HandleRenderEventAsync(
                        doc.RootElement.Clone(),
                        mux.Port,
                        _assembler,
                        _tty,
                        Live,
                        CancellationToken.None);
                }

                await AttachSession.StartChainReanchors(mux.Port, Live);
            }
        }

        /// <summary>The frame decisions and observe calls of the attach, for a failure message.</summary>
        public string FrameLog()
        {
            var sink = (CapturingProcessLogSink)Live.ProcessLog;
            return string.Join(
                "\n",
                sink.Records
                    .Where(record => record.Event == ProcessLogEvents.AttachObserve)
                    .Select(record => record.PaneId + " " + record.Outcome + " " + record.Reason));
        }

        public string HostText()
        {
            var text = new StringBuilder();
            for (var row = 0; row < Live.Host.Rows; row++)
            {
                for (var col = 0; col < Live.Host.Cols; col++)
                    text.Append(Live.Host.CellAt(col, row).Text);
                text.Append('\n');
            }

            return text.ToString();
        }

        public void Dispose() => _tty.Dispose();
    }

    /// <summary>
    /// The command port of one connection to one mux. It records every call
    /// and tracks the calls that have not ended.
    /// </summary>
    private sealed class MuxPort(ControlPlaneService cp, IClientConnection sink) : IAttachCommandPort
    {
        private readonly object _gate = new();
        private readonly List<(string Method, JsonObject? Parameters)> _calls = [];
        private int _open;

        public int CallCount
        {
            get
            {
                lock (_gate)
                    return _calls.Count;
            }
        }

        public async Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            lock (_gate)
                _calls.Add((method, parameters?.DeepClone().AsObject()));
            Interlocked.Increment(ref _open);
            try
            {
                var args = parameters is null ? default : Json(parameters);
                return (await cp.DispatchAsync(method, args, sink, ct)).Clone();
            }
            finally
            {
                Interlocked.Decrement(ref _open);
            }
        }

        /// <summary>Waits until no call is open.</summary>
        public Task IdleAsync() => Poll.UntilAsync(() => Volatile.Read(ref _open) == 0, "open mux calls");

        /// <summary>The observe calls of <paramref name="paneId"/> since the call mark, reanchor requests included.</summary>
        public int ObservesSince(int mark, string paneId)
        {
            lock (_gate)
            {
                return _calls.Skip(mark).Count(call =>
                    call.Method == ProtocolMethods.TerminalObserve
                    && call.Parameters?["pane_id"]?.GetValue<string>() == paneId);
            }
        }

        public string Describe(int mark)
        {
            lock (_gate)
            {
                return string.Join(
                    "\n",
                    _calls.Skip(mark).Select(call => call.Method + " " + call.Parameters?.ToJsonString()));
            }
        }
    }

    /// <summary>A clock that moves only when the test moves it. Timers use the system clock.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 18, 7, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FixedCatalog : ISidebarCubeCatalogSource
    {
        public ValueTask<SidebarCubeCatalogLoad> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(SidebarCubeCatalogLoad.Ready([View.CubeItem]));
    }

    private sealed class NullPort : IAttachCommandPort
    {
        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse("{}");
            return Task.FromResult(doc.RootElement.Clone());
        }
    }

    private sealed class LaneSink(string id) : IClientConnection
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];

        public string ConnectionId { get; } = id;

        public bool UsesWriterLanes => true;

        public int MaxLineBytes => 1_048_576;

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_gate)
                    return [.. _lines];
            }
        }

        public int Count
        {
            get
            {
                lock (_gate)
                    return _lines.Count;
            }
        }

        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default)
        {
            Add(jsonLine);
            return Task.CompletedTask;
        }

        public WriterLaneResult TryEnqueueRender(string jsonLine) => Add(jsonLine);

        public WriterLaneResult TryEnqueueRender(byte[] utf8Json) => Add(Encoding.UTF8.GetString(utf8Json));

        public WriterLaneResult EnqueueOrderedRender(string jsonLine) => Add(jsonLine);

        public WriterLaneResult EnqueueOrderedRender(byte[] utf8Json) => Add(Encoding.UTF8.GetString(utf8Json));

        private WriterLaneResult Add(string json)
        {
            lock (_gate)
                _lines.Add(json);
            return WriterLaneResult.Ok;
        }
    }
}
