using System.Text;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Integrations;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Infrastructure.Config;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class PaneScreenHistoryTests
{
    private const string ScreenSecret = "split-secret";

    [Fact]
    public void Default_flag_is_off()
    {
        Assert.False(AttachClientConfig.Default.Experimental.PaneHistory);
        Assert.False(AttachExperimentalConfig.Default.PaneHistory);
        var empty = TomlAttachConfigBinder.Bind("");
        Assert.True(empty.IsOk);
        Assert.False(empty.Value.Experimental.PaneHistory);
    }

    [Fact]
    public void Binder_enables_opt_in_flag()
    {
        var bound = TomlAttachConfigBinder.Bind("""
            [experimental]
            pane_history = true
            """);
        Assert.True(bound.IsOk);
        Assert.True(bound.Value.Experimental.PaneHistory);
    }

    [Fact]
    public void Capture_records_hidden_pane_and_lookup_uses_indices()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "hypa-hist-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var app = ReadyApp("hist-capture");
            var ws = app.CreateWorkspace(cwd, "ws");
            var hidden = app.RegisterPane(new PaneState
            {
                Id = PaneId.New(),
                TabId = ws.FocusedTabId!.Value,
                WorkspaceId = ws.Id,
                Cwd = cwd,
                Placement = PanePlacement.Hidden,
                Cols = 80,
                Rows = 24,
                LifecycleState = PaneLifecycle.Running,
            });
            var factory = TestPaneFactories.Capturing();
            factory.ProgrammedHistory = ScreenSecret;
            var runtime = factory.Create(new PaneSpawnOptions
            {
                Id = hidden.Id,
                Cwd = cwd,
                Command = "/bin/sh",
            });

            var snapshot = PaneHistorySnapshotter.Capture(
                app,
                id => id.Value == hidden.Id.Value ? runtime : null);
            Assert.Equal(SessionHistorySnapshot.FormatVersion, snapshot.Version);
            var found = PaneHistorySnapshotter.Lookup(snapshot, 0, 0, hidden.Id.Value);
            Assert.NotNull(found);
            Assert.Equal(ScreenSecret, found!.Ansi);
            Assert.Null(PaneHistorySnapshotter.Lookup(snapshot, 1, 0, hidden.Id.Value));
            Assert.Null(PaneHistorySnapshotter.Lookup(snapshot, 0, 0, "missing"));
        }
        finally
        {
            TryDelete(cwd);
        }
    }

    [Fact]
    public void Planner_suppresses_history_when_official_plan_exists()
    {
        var history = new PaneHistorySnapshot { Ansi = ScreenSecret, Lines = 1 };
        var session = new NativeAgentSessionRef
        {
            Kind = NativeAgentSessionRef.KindId,
            Value = "sess-1",
            Source = OfficialAgentSources.Claude,
            Agent = "claude",
        };

        var withPlan = PaneRestoreStartupPlanner.Plan(session, history, resumeAgentsOnRestore: true);
        Assert.NotNull(withPlan.RestorePlan);
        Assert.Null(withPlan.InitialHistoryAnsi);

        var resumeOff = PaneRestoreStartupPlanner.Plan(session, history, resumeAgentsOnRestore: false);
        Assert.Null(resumeOff.RestorePlan);
        Assert.Equal(ScreenSecret, resumeOff.InitialHistoryAnsi);

        var noSession = PaneRestoreStartupPlanner.Plan(null, history, resumeAgentsOnRestore: true);
        Assert.Null(noSession.RestorePlan);
        Assert.Equal(ScreenSecret, noSession.InitialHistoryAnsi);
    }

    [Fact]
    public void File_store_round_trips_and_keeps_ansi_out_of_empty_graph()
    {
        var dir = NewDir("hist-file");
        try
        {
            var store = new FilePaneHistorySnapshotStore(dir);
            var paneId = PaneId.New().Value;
            Assert.True(store.Save(HistoryFor(paneId, ScreenSecret)).IsOk);
            Assert.True(File.Exists(store.FilePath));
            var json = File.ReadAllText(store.FilePath);
            Assert.Contains(ScreenSecret, json, StringComparison.Ordinal);
            Assert.Contains("\"ansi\"", json, StringComparison.Ordinal);
            Assert.Contains("\"lines\"", json, StringComparison.Ordinal);

            var loaded = store.Load();
            Assert.NotNull(loaded);
            var pane = PaneHistorySnapshotter.Lookup(loaded, 0, 0, paneId);
            Assert.Equal(ScreenSecret, pane!.Ansi);

            Assert.True(store.Clear().IsOk);
            Assert.False(File.Exists(store.FilePath));
            Assert.Null(store.Load());
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void File_store_ignores_future_version_and_invalid_json()
    {
        var dir = NewDir("hist-ignore");
        try
        {
            var store = new FilePaneHistorySnapshotStore(dir);
            File.WriteAllText(store.FilePath, """{"version":99,"workspaces":[]}""");
            Assert.Null(store.Load());

            File.WriteAllText(store.FilePath, "{not-json");
            Assert.Null(store.Load());
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task Flag_off_does_not_write_history_file()
    {
        await using var harness = CreateHarness(paneHistory: false);
        harness.Capturing.ProgrammedHistory = ScreenSecret;
        await harness.Cp.RestoreSpawnAsync(CancellationToken.None);
        await harness.Cp.ShutdownAsync(CancellationToken.None);
        Assert.False(File.Exists(harness.History.FilePath));
    }

    [Fact]
    public async Task Flag_off_clears_stale_history_file()
    {
        await using var harness = CreateHarness(paneHistory: false);
        File.WriteAllText(harness.History.FilePath, ScreenSecret);
        await harness.Cp.RestoreSpawnAsync(CancellationToken.None);
        Assert.Null(harness.Capturing.LastOptions!.InitialHistoryAnsi);
        await harness.Cp.ShutdownAsync(CancellationToken.None);
        Assert.False(File.Exists(harness.History.FilePath));
    }

    [Fact]
    public async Task Flag_on_writes_history_file_not_graph()
    {
        var dir = NewDir("hist-split");
        var paths = new RuntimeStatePaths { StateDirectory = dir };
        var migrator = new SqliteRuntimeSchemaMigrator(paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        await using var graph = new SqliteRuntimeSessionStore(paths);
        try
        {
            await using var harness = CreateHarness(
                paneHistory: true,
                dir: dir,
                graph: graph);
            harness.Capturing.ProgrammedHistory = ScreenSecret;
            await harness.Cp.RestoreSpawnAsync(CancellationToken.None);
            await harness.Cp.ShutdownAsync(CancellationToken.None);

            Assert.True(File.Exists(harness.History.FilePath));
            var history = File.ReadAllText(harness.History.FilePath);
            Assert.Contains(ScreenSecret, history, StringComparison.Ordinal);
            var db = await File.ReadAllTextAsync(paths.DatabasePath);
            Assert.DoesNotContain(ScreenSecret, db, StringComparison.Ordinal);
        }
        finally
        {
            SqliteTestCleanup.ReleaseAndDelete(dir, paths.DatabasePath);
        }
    }

    [Fact]
    public async Task Enabled_restore_seeds_ansi_and_starts_new_shell()
    {
        await using var harness = CreateHarness(paneHistory: true, oldPid: 99_999);
        Assert.True(harness.History.Save(HistoryFor(harness.Pane.Id.Value, ScreenSecret)).IsOk);

        await harness.Cp.RestoreSpawnAsync(CancellationToken.None);

        Assert.Equal(ScreenSecret, harness.Capturing.LastOptions!.InitialHistoryAnsi);
        Assert.Equal("/bin/sh", harness.Capturing.LastOptions.Command);
        var live = harness.App.GetPane(harness.Pane.Id);
        Assert.NotNull(live);
        Assert.Equal(42_012, live!.Pid);
        Assert.NotEqual(99_999, live.Pid);
        Assert.Equal(PaneLifecycle.Running, live.LifecycleState);
    }

    [Fact]
    public async Task Official_resume_suppresses_history_replay()
    {
        var session = OfficialClaude("sess-hist");
        await using var harness = CreateHarness(
            paneHistory: true,
            resumeAgents: true,
            session: session);
        Assert.True(harness.History.Save(HistoryFor(harness.Pane.Id.Value, ScreenSecret)).IsOk);

        await harness.Cp.RestoreSpawnAsync(CancellationToken.None);

        Assert.Null(harness.Capturing.LastOptions!.InitialHistoryAnsi);
        await harness.Cp.FlushOfficialAgentResumesAsync(CancellationToken.None);
        Assert.Contains(
            harness.Capturing.Writes,
            w => w.Contains("claude --resume sess-hist", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Duplicate_official_session_also_suppresses_history()
    {
        var cwd = NewDir("hist-dup");
        var app = ReadyApp("hist-dup");
        var ws = app.CreateWorkspace(cwd, "ws");
        NativeAgentSessionRef Session() => OfficialClaude("sess-dup");
        var first = RegisterOrphan(app, ws, cwd, Session());
        var second = RegisterOrphan(app, ws, cwd, Session());
        var capturing = TestPaneFactories.Capturing();
        var history = new FilePaneHistorySnapshotStore(cwd);
        Assert.True(history.Save(new SessionHistorySnapshot
        {
            Version = SessionHistorySnapshot.FormatVersion,
            Workspaces =
            [
                new WorkspaceHistorySnapshot
                {
                    Tabs =
                    [
                        new TabHistorySnapshot
                        {
                            Panes = new Dictionary<string, PaneHistorySnapshot>(StringComparer.Ordinal)
                            {
                                [first.Id.Value] = new() { Ansi = ScreenSecret, Lines = 1 },
                                [second.Id.Value] = new() { Ansi = ScreenSecret, Lines = 1 },
                            },
                        },
                    ],
                },
            ],
        }).IsOk);

        var cp = new ControlPlaneService(
            app,
            capturing,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            attachConfig: HistoryAttach(paneHistory: true),
            paneHistoryStore: history);
        try
        {
            await cp.RestoreSpawnAsync(CancellationToken.None);
            Assert.Equal(2, capturing.Options.Count);
            Assert.All(capturing.Options, o => Assert.Null(o.InitialHistoryAnsi));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
            TryDelete(cwd);
        }
    }

    [Fact]
    public async Task Resume_off_allows_history_replay_for_official_session()
    {
        await using var harness = CreateHarness(
            paneHistory: true,
            resumeAgents: false,
            session: OfficialClaude("sess-hist-off"));
        Assert.True(harness.History.Save(HistoryFor(harness.Pane.Id.Value, ScreenSecret)).IsOk);

        await harness.Cp.RestoreSpawnAsync(CancellationToken.None);

        Assert.Equal(ScreenSecret, harness.Capturing.LastOptions!.InitialHistoryAnsi);
        await harness.Cp.FlushOfficialAgentResumesAsync(CancellationToken.None);
        Assert.Empty(harness.Capturing.Writes);
    }

    [Fact]
    public async Task Reload_off_clears_history_file()
    {
        var dir = NewDir("hist-reload");
        var history = new FilePaneHistorySnapshotStore(dir);
        Assert.True(history.Save(HistoryFor("pane1", ScreenSecret)).IsOk);
        var runtime = new ScriptedAttachConfigRuntime
        {
            Current = HistoryAttach(paneHistory: true),
            AfterReload = HistoryAttach(paneHistory: false),
        };
        var app = ReadyApp("hist-reload");
        app.CreateWorkspace(dir, "ws");
        var cp = new ControlPlaneService(
            app,
            TestPaneFactories.Capturing(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            attachConfigRuntime: runtime,
            paneHistoryStore: history);
        try
        {
            await cp.DispatchAsync(ProtocolMethods.ServerReloadConfig, null, CancellationToken.None);
            Assert.False(File.Exists(history.FilePath));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task Pane_runtime_seeds_vt_before_pty()
    {
        var vt = new StubVtEngine(40, 6);
        var pty = new TestPaneFactories.InjectedPtyProcess();
        var options = new PaneSpawnOptions
        {
            Id = PaneId.New(),
            Cwd = Path.GetTempPath(),
            Command = "/bin/sh",
            Cols = 40,
            Rows = 8,
            InitialHistoryAnsi = ScreenSecret + "\n",
        };
        await using var runtime = new PaneRuntime(options, (_, _) => pty, vt);
        await runtime.StartAsync(CancellationToken.None);
        Assert.Contains(ScreenSecret, runtime.ReadRecentUnwrappedText(int.MaxValue), StringComparison.Ordinal);
        var snap = runtime.SnapshotHistory();
        Assert.NotNull(snap);
        Assert.Contains(ScreenSecret, snap, StringComparison.Ordinal);
    }

    private static HistoryHarness CreateHarness(
        bool paneHistory,
        bool resumeAgents = true,
        NativeAgentSessionRef? session = null,
        int? oldPid = null,
        string? dir = null,
        IRuntimeSessionStore? graph = null)
    {
        dir ??= NewDir("hist-cp");
        var app = ReadyApp("hist-cp");
        var ws = app.CreateWorkspace(dir, "ws");
        var pane = RegisterOrphan(app, ws, dir, session, oldPid);
        var capturing = TestPaneFactories.Capturing();
        var history = new FilePaneHistorySnapshotStore(dir);
        var cp = new ControlPlaneService(
            app,
            capturing,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: graph,
            attachConfig: HistoryAttach(paneHistory, resumeAgents),
            paneHistoryStore: history);
        return new HistoryHarness(dir, app, pane, capturing, cp, history, ownsDirectory: graph is null);
    }

    private static AppState ReadyApp(string name)
    {
        var app = new AppState(SessionId.New(name));
        app.UpdateSession(s => s with
        {
            Name = name,
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        return app;
    }

    private static PaneState RegisterOrphan(
        AppState app,
        WorkspaceState ws,
        string cwd,
        NativeAgentSessionRef? session = null,
        int? oldPid = null) =>
        app.RegisterPane(new PaneState
        {
            Id = PaneId.New(),
            TabId = ws.FocusedTabId!.Value,
            WorkspaceId = ws.Id,
            Cwd = cwd,
            Cols = 80,
            Rows = 24,
            LifecycleState = PaneLifecycle.Orphaned,
            IsAlive = false,
            OccupantGeneration = 1,
            Pid = oldPid,
            AgentSession = session,
        });

    private static NativeAgentSessionRef OfficialClaude(string value) => new()
    {
        Kind = NativeAgentSessionRef.KindId,
        Value = value,
        Source = OfficialAgentSources.Claude,
        Agent = "claude",
    };

    private static AttachClientConfig HistoryAttach(bool paneHistory, bool resumeAgents = true) =>
        AttachClientConfig.Default with
        {
            Experimental = AttachExperimentalConfig.Default with { PaneHistory = paneHistory },
            Session = AttachSessionConfig.Default with { ResumeAgentsOnRestore = resumeAgents },
            Terminal = AttachTerminalConfig.Default with
            {
                DefaultShell = "/bin/sh",
                ShellMode = TerminalShellMode.NonLogin,
            },
        };

    private static SessionHistorySnapshot HistoryFor(string paneId, string ansi) => new()
    {
        Version = SessionHistorySnapshot.FormatVersion,
        Workspaces =
        [
            new WorkspaceHistorySnapshot
            {
                Tabs =
                [
                    new TabHistorySnapshot
                    {
                        Panes = new Dictionary<string, PaneHistorySnapshot>(StringComparer.Ordinal)
                        {
                            [paneId] = new() { Ansi = ansi, Lines = 1 },
                        },
                    },
                ],
            },
        ],
    };

    private static string NewDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hypa-" + prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // teardown
        }
    }

    private sealed class HistoryHarness : IAsyncDisposable
    {
        private readonly bool _ownsDirectory;

        public HistoryHarness(
            string dir,
            AppState app,
            PaneState pane,
            TestPaneFactories.CapturingPaneFactory capturing,
            ControlPlaneService cp,
            FilePaneHistorySnapshotStore history,
            bool ownsDirectory)
        {
            Dir = dir;
            App = app;
            Pane = pane;
            Capturing = capturing;
            Cp = cp;
            History = history;
            _ownsDirectory = ownsDirectory;
        }

        public string Dir { get; }
        public AppState App { get; }
        public PaneState Pane { get; }
        public TestPaneFactories.CapturingPaneFactory Capturing { get; }
        public ControlPlaneService Cp { get; }
        public FilePaneHistorySnapshotStore History { get; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Cp.ShutdownAsync(CancellationToken.None);
            }
            catch
            {
                // teardown
            }

            if (_ownsDirectory)
                TryDelete(Dir);
        }
    }

    private sealed class ScriptedAttachConfigRuntime : IAttachConfigRuntime
    {
        public required AttachClientConfig Current { get; set; }
        public required AttachClientConfig AfterReload { get; set; }

        public AttachConfigReloadReport ReloadFromDisk()
        {
            Current = AfterReload;
            return AttachConfigReloadReport.Applied();
        }
    }
}

[Collection("GhosttyPtyTests")]
public sealed class PaneScreenHistoryGhosttyTests
{
    [SkippableFact]
    public async Task Ghostty_unwrapped_ansi_round_trips_through_seed()
    {
        var lib = GhosttyTestRequire.TryResolveNativeLibraryPath();
        GhosttyTestRequire.RequireNativeLibrary(lib);
        var factory = new TestPaneFactories.GhosttyEmitPaneFactory(lib);
        var options = new PaneSpawnOptions
        {
            Id = PaneId.New(),
            Cwd = Path.GetTempPath(),
            Command = "/bin/sh",
            Cols = 40,
            Rows = 8,
            InitialHistoryAnsi = "\u001b[31m" + "split-secret" + "\u001b[0m\r\n",
        };
        await using var runtime = (PaneRuntime)factory.Create(options);
        await runtime.StartAsync(CancellationToken.None);
        var snap = runtime.SnapshotHistory();
        Assert.NotNull(snap);
        Assert.Contains("split-secret", snap, StringComparison.Ordinal);

        using var replay = new GhosttyVtEngine(40, 8, maxScrollback: 40, libraryPathOverride: lib);
        replay.Feed(Encoding.UTF8.GetBytes(snap));
        Assert.Contains("split-secret", replay.GetRecentUnwrappedText(int.MaxValue), StringComparison.Ordinal);
    }
}
