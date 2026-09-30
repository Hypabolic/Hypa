using System.Diagnostics;
using System.Text.Json;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// residual findings: fingerprint stability under pane exit, real git probe,
/// and prepare/export consistency under races.
/// </summary>
[Collection("CheckpointTests")]
public class CheckpointConcurrencyAndGitTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;
    private readonly List<ControlPlaneService> _planes = [];

    public CheckpointConcurrencyAndGitTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-h10-cg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = Path.Combine(_dir, "state") };
        Directory.CreateDirectory(_paths.StateDirectory);
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

    public void Dispose()
    {
        SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);
    }

    private async Task<(ControlPlaneService Cp, AppState State, FileRuntimeEventJournal Journal, ICheckpointService Checkpoints)>
        CreatePlaneAsync(
            string sessionName,
            IGitWorkspaceProbe? git = null,
            IPaneRuntimeFactory? factory = null)
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        var state = new AppState(SessionId.New(sessionName));
        state.UpdateSession(s => s with
        {
            Name = sessionName,
            LifecycleState = SessionLifecycle.Ready,
        });

        var store = new SqliteRuntimeSessionStore(_paths);
        var manifests = new SqliteJournalManifestStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);

        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);

        var cpStore = new FileCheckpointStore(_paths);
        var builder = new DefaultCheckpointArtifactBuilder(git ?? new CompleteGitWorkspaceProbe());
        var checkpoints = new DefaultCheckpointService(cpStore, builder);

        var cp = new ControlPlaneService(
            state,
            factory ?? TestPaneFactories.Create(new PaneIntelligencePipeline()),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: new EventSubscriptionHub(),
            redactor: new DefaultEventPayloadRedactor(),
            checkpoints: checkpoints);
        _planes.Add(cp);

        return (cp, state, journal, checkpoints);
    }

    [Fact]
    public async Task Pane_exit_after_prepare_does_not_conflict_export()
    {
        var (cp, state, journal, _) = await CreatePlaneAsync("pane-exit");

        // Register a synthetic running pane (no live process).
        var ws = state.CreateWorkspace("/tmp/pane-exit", "ws");
        var tabId = ws.FocusedTabId!.Value;
        var paneId = PaneId.New();
        state.RegisterPane(new PaneState
        {
            Id = paneId,
            TabId = tabId,
            WorkspaceId = ws.Id,
            Label = "shell",
            Cwd = "/tmp/pane-exit",
            Command = "/bin/true",
            LifecycleState = PaneLifecycle.Running,
            OccupantGeneration = 1,
            IsAlive = true,
            Pid = 4242,
        });

        var prep = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
            CancellationToken.None);
        var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;
        Assert.Equal(SessionLifecycle.FrozenReadOnly, state.Snapshot().LifecycleState);

        // Natural process exit while frozen (updates lifecycle_state only).
        state.UpdatePane(paneId, p => p with
        {
            IsAlive = false,
            Pid = null,
            ExitCode = 0,
            LifecycleState = PaneLifecycle.Exited,
            AgentStatus = AgentStatus.Done,
        });

        // Fingerprint must ignore pane lifecycle_state → export succeeds.
        var exp = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointExport,
            JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
            CancellationToken.None);
        Assert.Equal("exported", exp.GetProperty("state").GetString());

        await journal.DisposeAsync();
    }

    [Fact]
    public void SessionGraphFingerprint_ignores_pane_lifecycle_state()
    {
        var session = new SessionState
        {
            Id = SessionId.New("fp1"),
            Name = "fp1",
            Workspaces = new Dictionary<string, WorkspaceState>
            {
                ["ws1"] = new WorkspaceState
                {
                    Id = new WorkspaceId("ws1"),
                    Label = "w",
                    Cwd = "/tmp",
                    TabIds = [new TabId("t1")],
                    FocusedTabId = new TabId("t1"),
                },
            },
            Tabs = new Dictionary<string, TabState>
            {
                ["t1"] = new TabState
                {
                    Id = new TabId("t1"),
                    WorkspaceId = new WorkspaceId("ws1"),
                    Label = "main",
                    PaneIds = [new PaneId("p1")],
                },
            },
            Panes = new Dictionary<string, PaneState>
            {
                ["p1"] = new PaneState
                {
                    Id = new PaneId("p1"),
                    TabId = new TabId("t1"),
                    WorkspaceId = new WorkspaceId("ws1"),
                    Label = "shell",
                    Cwd = "/tmp",
                    Command = "bash",
                    LifecycleState = PaneLifecycle.Running,
                    OccupantGeneration = 1,
                },
            },
        };

        var a = SessionGraphFingerprint.Compute(session);
        var exited = session with
        {
            Panes = new Dictionary<string, PaneState>
            {
                ["p1"] = session.Panes["p1"] with { LifecycleState = PaneLifecycle.Exited, ExitCode = 1 },
            },
        };
        var b = SessionGraphFingerprint.Compute(exited);
        Assert.Equal(a, b);

        var mutated = session with
        {
            PlacementGeneration = session.PlacementGeneration + 1,
        };
        Assert.NotEqual(a, SessionGraphFingerprint.Compute(mutated));
    }

    [Fact]
    public async Task ProcessGitWorkspaceProbe_clean_repo_is_complete_with_head()
    {
        var repo = Path.Combine(_dir, "git-repo");
        Directory.CreateDirectory(repo);
        Assert.True(await RunGitAsync(repo, "init", "-b", "main"));
        Assert.True(await RunGitAsync(repo, "config", "user.email", "test@example.com"));
        Assert.True(await RunGitAsync(repo, "config", "user.name", "Test"));
        await File.WriteAllTextAsync(Path.Combine(repo, "readme.txt"), "hello");
        Assert.True(await RunGitAsync(repo, "add", "readme.txt"));
        Assert.True(await RunGitAsync(repo, "commit", "-m", "init"));

        var probe = new ProcessGitWorkspaceProbe();
        var result = await probe.ProbeAsync(repo);
        Assert.True(result.IsOk, result.IsOk ? null : result.Error.Message);
        Assert.False(result.Value.Incomplete);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.Head));
        Assert.Equal(40, result.Value.Head!.Length);
        Assert.False(result.Value.Dirty);
    }

    [Fact]
    public async Task ProcessGitWorkspaceProbe_non_git_dir_is_complete_without_head()
    {
        var plain = Path.Combine(_dir, "plain");
        Directory.CreateDirectory(plain);
        var probe = new ProcessGitWorkspaceProbe();
        var result = await probe.ProbeAsync(plain);
        Assert.True(result.IsOk);
        Assert.False(result.Value.Incomplete);
        Assert.Null(result.Value.Head);
    }

    [Fact]
    public async Task Export_clean_git_workspace_transfer_incomplete_false()
    {
        var repo = Path.Combine(_dir, "export-git");
        Directory.CreateDirectory(repo);
        Assert.True(await RunGitAsync(repo, "init", "-b", "main"));
        Assert.True(await RunGitAsync(repo, "config", "user.email", "test@example.com"));
        Assert.True(await RunGitAsync(repo, "config", "user.name", "Test"));
        await File.WriteAllTextAsync(Path.Combine(repo, "app.txt"), "body");
        Assert.True(await RunGitAsync(repo, "add", "app.txt"));
        Assert.True(await RunGitAsync(repo, "commit", "-m", "init"));

        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        var state = new AppState(SessionId.New("exp-git"));
        state.UpdateSession(s => s with
        {
            Name = "exp-git",
            LifecycleState = SessionLifecycle.Ready,
            Binding = new AtomicBinding { ProjectRoot = repo, RunId = "run_git" },
        });

        var store = new SqliteRuntimeSessionStore(_paths);
        var manifests = new SqliteJournalManifestStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);

        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);

        var cpStore = new FileCheckpointStore(_paths);
        var builder = new DefaultCheckpointArtifactBuilder(new ProcessGitWorkspaceProbe());
        var checkpoints = new DefaultCheckpointService(cpStore, builder);

        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Create(new PaneIntelligencePipeline()),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: new EventSubscriptionHub(),
            redactor: new DefaultEventPayloadRedactor(),
            checkpoints: checkpoints);
        _planes.Add(cp);

        var prep = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":true,"include_scrollback":false}""").RootElement,
            CancellationToken.None);
        var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;

        var exp = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointExport,
            JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
            CancellationToken.None);

        Assert.Equal("exported", exp.GetProperty("state").GetString());
        var manifestPath = Path.Combine(
            cpStore.GetCheckpointDirectory(checkpointId), "manifest.json");
        using (var manifestDoc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath)))
        {
            var warnings = manifestDoc.RootElement.TryGetProperty("warnings", out var w)
                && w.ValueKind == JsonValueKind.Array
                ? string.Join("; ", w.EnumerateArray().Select(e => e.GetString()))
                : "";
            Assert.False(
                exp.GetProperty("transfer_incomplete").GetBoolean(),
                "warnings: " + warnings);
            var copiedApp = manifestDoc.RootElement.GetProperty("artifacts").EnumerateArray()
                .Any(a => a.GetProperty("path").GetString() == "artifacts/workspace/app.txt");
            Assert.True(copiedApp, "clean repo must copy app.txt");
        }

        var gitMeta = Path.Combine(
            cpStore.GetCheckpointDirectory(checkpointId), "artifacts", "git_meta.json");
        Assert.True(File.Exists(gitMeta));
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(gitMeta));
        Assert.False(doc.RootElement.GetProperty("incomplete").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("head").GetString()));

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_prepare_and_pane_create_exportable_or_fail_closed()
    {
        var recording = new RecordingPaneFactory();
        var (cp, state, journal, _) = await CreatePlaneAsync("race-pane", factory: recording);
        var cwd = Path.Combine(_dir, "race-pane");
        Directory.CreateDirectory(cwd);

        await cp.DispatchAsync(
            "workspace.create",
            JsonDocument.Parse(
                $$"""{"cwd":{{JsonSerializer.Serialize(cwd)}},"create_pane":false}""").RootElement,
            CancellationToken.None);

        string? preparedId = null;
        var prepareTask = Task.Run(async () =>
        {
            try
            {
                var prep = await cp.DispatchAsync(
                    ProtocolMethods.RuntimeCheckpointPrepare,
                    JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
                    CancellationToken.None);
                preparedId = prep.GetProperty("checkpoint_id").GetString();
            }
            catch (ControlPlaneException)
            {
                // may lose race to invalid_state if already frozen from retry — not expected here
            }
        });

        var createTask = Task.Run(async () =>
        {
            try
            {
                await cp.DispatchAsync(
                    "pane.create",
                    JsonDocument.Parse("""{"command":"","label":"raced"}""").RootElement,
                    CancellationToken.None);
            }
            catch (ControlPlaneException ex) when (ex.Code == ProtocolErrorCodes.InvalidState)
            {
                // freeze won — expected fail-closed
            }
        });

        await Task.WhenAll(prepareTask, createTask);

        if (preparedId is not null)
        {
            // Either export succeeds (mutation lost) or conflict auto-unfreezes.
            try
            {
                var exp = await cp.DispatchAsync(
                    ProtocolMethods.RuntimeCheckpointExport,
                    JsonDocument.Parse($$"""{"checkpoint_id":"{{preparedId}}"}""").RootElement,
                    CancellationToken.None);
                Assert.Equal("exported", exp.GetProperty("state").GetString());
            }
            catch (ControlPlaneException ex) when (ex.Code == ProtocolErrorCodes.CheckpointConflict)
            {
                Assert.Equal(SessionLifecycle.Ready, state.Snapshot().LifecycleState);
            }

            // Session must not stick frozen without recovery path.
            if (SessionLifecycle.IsFrozen(state.Snapshot().LifecycleState))
            {
                await cp.DispatchAsync(
                    ProtocolMethods.RuntimeCheckpointAbort,
                    JsonDocument.Parse($$"""{"checkpoint_id":"{{preparedId}}"}""").RootElement,
                    CancellationToken.None);
            }

            Assert.False(SessionLifecycle.IsFrozen(state.Snapshot().LifecycleState));
        }

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_prepare_and_pane_close_never_removes_after_freeze()
    {
        // Either close fails invalid_state (freeze won) or close completes before freeze —
        // never a silent RemovePane after frozen_read_only.
        var recording = new RecordingPaneFactory();
        var (cp, state, journal, _) = await CreatePlaneAsync("close-race", factory: recording);

        var create = await cp.DispatchAsync(
            "pane.create",
            JsonDocument.Parse("""{"command":"","label":"close-race","cwd":"/tmp/close-race"}""").RootElement,
            CancellationToken.None);
        var paneId = create.GetProperty("pane_id").GetString()!;
        Assert.NotNull(state.GetPane(new PaneId(paneId)));

        string? preparedId = null;
        Exception? closeEx = null;

        var prepareTask = Task.Run(async () =>
        {
            try
            {
                var prep = await cp.DispatchAsync(
                    ProtocolMethods.RuntimeCheckpointPrepare,
                    JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
                    CancellationToken.None);
                preparedId = prep.GetProperty("checkpoint_id").GetString();
            }
            catch (ControlPlaneException)
            {
                // not expected for a single prepare
            }
        });

        var closeTask = Task.Run(async () =>
        {
            try
            {
                await cp.DispatchAsync(
                    "pane.close",
                    JsonDocument.Parse($$"""{"pane_id":"{{paneId}}"}""").RootElement,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                closeEx = ex;
            }
        });

        await Task.WhenAll(prepareTask, closeTask);

        if (closeEx is ControlPlaneException cpe)
        {
            Assert.Equal(ProtocolErrorCodes.InvalidState, cpe.Code);
            Assert.Equal(SessionLifecycle.FrozenReadOnly, state.Snapshot().LifecycleState);
            Assert.NotNull(state.GetPane(new PaneId(paneId)));
        }
        else
        {
            Assert.Null(closeEx);
            Assert.Null(state.GetPane(new PaneId(paneId)));
        }

        if (preparedId is not null && SessionLifecycle.IsFrozen(state.Snapshot().LifecycleState))
        {
            var exp = await cp.DispatchAsync(
                ProtocolMethods.RuntimeCheckpointExport,
                JsonDocument.Parse($$"""{"checkpoint_id":"{{preparedId}}"}""").RootElement,
                CancellationToken.None);
            Assert.Equal(CheckpointStates.Exported, exp.GetProperty("state").GetString());
        }

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Fail_spawn_after_prepare_never_removes_pane_while_frozen()
    {
        // RegisterPane then concurrent prepare, force start failure: either freeze rejected
        // cleanup (pane stays for fingerprint) or export succeeds with stable fingerprint —
        // never RemovePane after frozen_read_only without the binding mutation gate.
        var holdStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new HoldThenFailPaneFactory(holdStart);
        var (cp, state, journal, _) = await CreatePlaneAsync("spawn-fail-freeze", factory: factory);

        await cp.DispatchAsync(
            "workspace.create",
            JsonDocument.Parse(
                """{"cwd":"/tmp/spawn-fail-freeze","create_pane":false}""").RootElement,
            CancellationToken.None);

        var createTask = Task.Run(async () =>
        {
            try
            {
                await cp.DispatchAsync(
                    "pane.create",
                    JsonDocument.Parse(
                        """{"command":"","label":"held-fail"}""").RootElement,
                    CancellationToken.None);
                return (Exception?)null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        // Wait until RegisterPane landed (StartAsync is held).
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (state.ListPanes().Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.NotEmpty(state.ListPanes());
        var paneId = state.ListPanes()[0].Id;
        var fpBeforePrepare = SessionGraphFingerprint.Compute(state.Snapshot());

        var prep = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
            CancellationToken.None);
        var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;
        Assert.Equal(SessionLifecycle.FrozenReadOnly, state.Snapshot().LifecycleState);
        var fpAtPrepare = SessionGraphFingerprint.Compute(state.Snapshot());
        Assert.Equal(fpBeforePrepare, fpAtPrepare);

        // Release StartAsync → fails → FailSpawnCleanup under freeze must not RemovePane.
        holdStart.SetResult();
        var createEx = await createTask;
        Assert.NotNull(createEx);
        Assert.True(
            createEx is ControlPlaneException,
            "spawn failure should surface as ControlPlaneException, got " + createEx!.GetType().Name);

        // Pane remains in the frozen graph (fingerprint stable).
        Assert.NotNull(state.GetPane(paneId));
        Assert.Equal(fpAtPrepare, SessionGraphFingerprint.Compute(state.Snapshot()));

        var exp = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointExport,
            JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
            CancellationToken.None);
        Assert.Equal(CheckpointStates.Exported, exp.GetProperty("state").GetString());

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Successful_start_after_freeze_fails_closed_without_anchor_mutation()
    {
        // Start succeeds while frozen: post-start gate must EnsureNotFrozen and fail closed
        // without CaptureProcessAnchors / ungated graph rewrite.
        var holdStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new HoldThenSucceedPaneFactory(holdStart);
        var (cp, state, journal, _) = await CreatePlaneAsync("spawn-ok-freeze", factory: factory);

        await cp.DispatchAsync(
            "workspace.create",
            JsonDocument.Parse(
                """{"cwd":"/tmp/spawn-ok-freeze","create_pane":false}""").RootElement,
            CancellationToken.None);

        // Governed session so CaptureProcessAnchors would mutate fingerprint if it ran.
        // Pane-scoped admit requires step_id + project_root under LocalGoverned.
        state.UpdateSession(s => s with
        {
            Governed = true,
            Binding = new AtomicBinding
            {
                TenantId = "tenant_spawn_ok",
                RunId = "run_spawn_ok",
                AgentSessionId = "agent_spawn_ok",
                ProjectRoot = "/tmp/spawn-ok-freeze",
                StepId = "step_spawn_ok",
            },
        });

        var createTask = Task.Run(async () =>
        {
            try
            {
                await cp.DispatchAsync(
                    "pane.create",
                    JsonDocument.Parse(
                        """{"command":"","label":"held-ok"}""").RootElement,
                    CancellationToken.None);
                return (Exception?)null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (state.ListPanes().Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.NotEmpty(state.ListPanes());
        var paneId = state.ListPanes()[0].Id;

        var prep = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
            CancellationToken.None);
        var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;
        var fpAtPrepare = SessionGraphFingerprint.Compute(state.Snapshot());
        Assert.Null(state.Snapshot().ProcessTenantId);
        Assert.Null(state.Snapshot().ProcessRunId);

        holdStart.SetResult();
        var createEx = await createTask;
        Assert.IsType<ControlPlaneException>(createEx);
        Assert.Equal(ProtocolErrorCodes.InvalidState, ((ControlPlaneException)createEx!).Code);

        // Anchors must not land after freeze; pane stays (no RemovePane on fail-closed start).
        Assert.Null(state.Snapshot().ProcessTenantId);
        Assert.Null(state.Snapshot().ProcessRunId);
        Assert.NotNull(state.GetPane(paneId));
        Assert.Equal(fpAtPrepare, SessionGraphFingerprint.Compute(state.Snapshot()));

        var exp = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointExport,
            JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
            CancellationToken.None);
        Assert.Equal(CheckpointStates.Exported, exp.GetProperty("state").GetString());

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_prepare_and_send_text_no_write_after_freeze()
    {
        // prepare freezes under bindingMutationGate; send_text re-checks + writes under the
        // same gate — never a successful write after session_state is frozen_read_only.
        var recording = new RecordingPaneFactory();
        var (cp, state, journal, _) = await CreatePlaneAsync("send-race", factory: recording);

        var create = await cp.DispatchAsync(
            "pane.create",
            JsonDocument.Parse("""{"command":"","label":"send-race","cwd":"/tmp/send-race"}""").RootElement,
            CancellationToken.None);
        var paneId = create.GetProperty("pane_id").GetString()!;

        var conn = new FakeConnection("conn_send_race");
        var claim = await cp.DispatchAsync(
            ProtocolMethods.RuntimeLeaseClaim,
            JsonDocument.Parse("""{"pane_id":"PLACEHOLDER","scope":"input"}""".Replace("PLACEHOLDER", paneId)).RootElement,
            conn,
            CancellationToken.None);
        Assert.Equal(LeaseOutcomes.Granted, claim.GetProperty("outcome").GetString());

        var writeOk = 0;
        var writeInvalid = 0;
        var prepareOk = 0;

        var tasks = new List<Task>();
        for (var i = 0; i < 12; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(async () =>
            {
                if (idx % 3 == 0)
                {
                    try
                    {
                        await cp.DispatchAsync(
                            ProtocolMethods.RuntimeCheckpointPrepare,
                            JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
                            CancellationToken.None);
                        Interlocked.Increment(ref prepareOk);
                    }
                    catch (ControlPlaneException ex) when (ex.Code == ProtocolErrorCodes.InvalidState)
                    {
                        // already frozen
                    }
                }
                else
                {
                    try
                    {
                        await cp.DispatchAsync(
                            "pane.send_text",
                            JsonDocument.Parse(
                                $$"""{"pane_id":"{{paneId}}","text":"ping-{{idx}}"}""").RootElement,
                            conn,
                            CancellationToken.None);
                        Interlocked.Increment(ref writeOk);
                    }
                    catch (ControlPlaneException ex) when (ex.Code == ProtocolErrorCodes.InvalidState)
                    {
                        Interlocked.Increment(ref writeInvalid);
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);

        Assert.True(prepareOk <= 1, $"expected at most one prepare, got {prepareOk}");
        if (prepareOk == 1)
        {
            Assert.Equal(SessionLifecycle.FrozenReadOnly, state.Snapshot().LifecycleState);
            // Every write that landed must have been admitted before freeze; after freeze
            // only invalid_state. Recording count equals successful RPC writeOk.
            Assert.Equal(writeOk, recording.WriteCount);
            Assert.True(writeInvalid + writeOk == 8, "all send_text attempts accounted for");
        }

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_prepare_and_send_keys_no_write_after_freeze()
    {
        // send_keys re-checks freeze at entry and under the binding gate.
        // New admissions must not land after frozen_read_only.
        var recording = new RecordingPaneFactory();
        var (cp, state, journal, _) = await CreatePlaneAsync("keys-race", factory: recording);

        var create = await cp.DispatchAsync(
            "pane.create",
            JsonDocument.Parse("""{"command":"","label":"keys-race","cwd":"/tmp/keys-race"}""").RootElement,
            CancellationToken.None);
        var paneId = create.GetProperty("pane_id").GetString()!;

        var conn = new FakeConnection("conn_keys_race");
        var claim = await cp.DispatchAsync(
            ProtocolMethods.RuntimeLeaseClaim,
            JsonDocument.Parse("""{"pane_id":"PLACEHOLDER","scope":"input"}""".Replace("PLACEHOLDER", paneId)).RootElement,
            conn,
            CancellationToken.None);
        Assert.Equal(LeaseOutcomes.Granted, claim.GetProperty("outcome").GetString());
        var leaseId = claim.GetProperty("lease_id").GetString()!;

        var writeOk = 0;
        var writeInvalid = 0;
        var prepareOk = 0;

        var tasks = new List<Task>();
        for (var i = 0; i < 12; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(async () =>
            {
                if (idx % 3 == 0)
                {
                    try
                    {
                        await cp.DispatchAsync(
                            ProtocolMethods.RuntimeCheckpointPrepare,
                            JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
                            CancellationToken.None);
                        Interlocked.Increment(ref prepareOk);
                    }
                    catch (ControlPlaneException ex) when (ex.Code == ProtocolErrorCodes.InvalidState)
                    {
                        // already frozen
                    }
                }
                else
                {
                    try
                    {
                        await cp.DispatchAsync(
                            ProtocolMethods.PaneSendKeys,
                            JsonDocument.Parse(
                                $$"""{"pane_id":"{{paneId}}","lease_id":"{{leaseId}}","encoding":"utf8","data":"ping-{{idx}}"}""").RootElement,
                            conn,
                            CancellationToken.None);
                        Interlocked.Increment(ref writeOk);
                    }
                    catch (ControlPlaneException ex) when (ex.Code == ProtocolErrorCodes.InvalidState)
                    {
                        Interlocked.Increment(ref writeInvalid);
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);

        Assert.True(prepareOk <= 1, $"expected at most one prepare, got {prepareOk}");
        if (prepareOk == 1)
        {
            Assert.Equal(SessionLifecycle.FrozenReadOnly, state.Snapshot().LifecycleState);
            Assert.Equal(writeOk, recording.WriteCount);
            Assert.True(writeInvalid + writeOk == 8, "all send_keys attempts accounted for");
        }

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_prepare_and_resize_no_io_after_freeze()
    {
        // resize re-checks freeze under bindingMutationGate; ResizeAsync + graph Cols/Rows
        // must not land after frozen_read_only.
        var recording = new RecordingPaneFactory();
        var (cp, state, journal, _) = await CreatePlaneAsync("resize-race", factory: recording);

        var create = await cp.DispatchAsync(
            "pane.create",
            JsonDocument.Parse("""{"command":"","label":"resize-race","cwd":"/tmp/resize-race"}""").RootElement,
            CancellationToken.None);
        var paneId = create.GetProperty("pane_id").GetString()!;

        var conn = new FakeConnection("conn_resize_race");
        var claim = await cp.DispatchAsync(
            ProtocolMethods.RuntimeLeaseClaim,
            JsonDocument.Parse("""{"pane_id":"PLACEHOLDER","scope":"input"}""".Replace("PLACEHOLDER", paneId)).RootElement,
            conn,
            CancellationToken.None);
        Assert.Equal(LeaseOutcomes.Granted, claim.GetProperty("outcome").GetString());

        var resizeOk = 0;
        var resizeInvalid = 0;
        var prepareOk = 0;

        var tasks = new List<Task>();
        for (var i = 0; i < 12; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(async () =>
            {
                if (idx % 3 == 0)
                {
                    try
                    {
                        await cp.DispatchAsync(
                            ProtocolMethods.RuntimeCheckpointPrepare,
                            JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
                            CancellationToken.None);
                        Interlocked.Increment(ref prepareOk);
                    }
                    catch (ControlPlaneException ex) when (ex.Code == ProtocolErrorCodes.InvalidState)
                    {
                        // already frozen
                    }
                }
                else
                {
                    try
                    {
                        await cp.DispatchAsync(
                            "pane.resize",
                            JsonDocument.Parse(
                                $$"""{"pane_id":"{{paneId}}","cols":{{80 + idx}},"rows":24}""").RootElement,
                            conn,
                            CancellationToken.None);
                        Interlocked.Increment(ref resizeOk);
                    }
                    catch (ControlPlaneException ex) when (ex.Code == ProtocolErrorCodes.InvalidState)
                    {
                        Interlocked.Increment(ref resizeInvalid);
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);

        Assert.True(prepareOk <= 1, $"expected at most one prepare, got {prepareOk}");
        if (prepareOk == 1)
        {
            Assert.Equal(SessionLifecycle.FrozenReadOnly, state.Snapshot().LifecycleState);
            Assert.Equal(resizeOk, recording.ResizeCount);
            Assert.True(resizeInvalid + resizeOk == 8, "all resize attempts accounted for");
        }

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_prepare_and_agent_prompt_no_write_after_freeze()
    {
        // agent.prompt re-checks freeze under bindingMutationGate; WriteTextAsync must
        // not land after frozen_read_only.
        var recording = new RecordingPaneFactory();
        var (cp, state, journal, _) = await CreatePlaneAsync("prompt-race", factory: recording);

        var create = await cp.DispatchAsync(
            "pane.create",
            JsonDocument.Parse("""{"command":"","label":"prompt-race","cwd":"/tmp/prompt-race"}""").RootElement,
            CancellationToken.None);
        var paneId = create.GetProperty("pane_id").GetString()!;

        var conn = new FakeConnection("conn_prompt_race");
        var claim = await cp.DispatchAsync(
            ProtocolMethods.RuntimeLeaseClaim,
            JsonDocument.Parse("""{"pane_id":"PLACEHOLDER","scope":"input"}""".Replace("PLACEHOLDER", paneId)).RootElement,
            conn,
            CancellationToken.None);
        Assert.Equal(LeaseOutcomes.Granted, claim.GetProperty("outcome").GetString());

        var writeOk = 0;
        var writeInvalid = 0;
        var prepareOk = 0;

        var tasks = new List<Task>();
        for (var i = 0; i < 12; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(async () =>
            {
                if (idx % 3 == 0)
                {
                    try
                    {
                        await cp.DispatchAsync(
                            ProtocolMethods.RuntimeCheckpointPrepare,
                            JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
                            CancellationToken.None);
                        Interlocked.Increment(ref prepareOk);
                    }
                    catch (ControlPlaneException ex) when (ex.Code == ProtocolErrorCodes.InvalidState)
                    {
                        // already frozen
                    }
                }
                else
                {
                    try
                    {
                        await cp.DispatchAsync(
                            ProtocolMethods.AgentPrompt,
                            JsonDocument.Parse(
                                $$"""{"pane_id":"{{paneId}}","message":"ping-{{idx}}"}""").RootElement,
                            conn,
                            CancellationToken.None);
                        Interlocked.Increment(ref writeOk);
                    }
                    catch (ControlPlaneException ex) when (ex.Code == ProtocolErrorCodes.InvalidState)
                    {
                        Interlocked.Increment(ref writeInvalid);
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);

        Assert.True(prepareOk <= 1, $"expected at most one prepare, got {prepareOk}");
        if (prepareOk == 1)
        {
            Assert.Equal(SessionLifecycle.FrozenReadOnly, state.Snapshot().LifecycleState);
            Assert.Equal(writeOk, recording.WriteCount);
            Assert.True(writeInvalid + writeOk == 8, "all agent.prompt attempts accounted for");
        }

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Shutdown_drains_persist_before_per_db_pool_release()
    {
        // Aggregate FullyQualifiedName~Checkpoint crashed testhost (ResumeThread)
        // when Dispose called SqliteConnection.ClearAllPools while PersistGraph
        // was still in flight. Shutdown must drain persist; pool release is per-db.
        var (cp, state, journal, _) = await CreatePlaneAsync("teardown-drain");
        var cwd = Path.Combine(_dir, "teardown-drain");
        Directory.CreateDirectory(cwd);

        await cp.DispatchAsync(
            "workspace.create",
            JsonDocument.Parse(
                $$"""{"cwd":{{JsonSerializer.Serialize(cwd)}},"create_pane":false}""").RootElement,
            CancellationToken.None);

        await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
            CancellationToken.None);
        Assert.Equal(SessionLifecycle.FrozenReadOnly, state.Snapshot().LifecycleState);

        await cp.ShutdownAsync(CancellationToken.None);
        await journal.DisposeAsync();
        SqliteTestCleanup.ReleaseDatabase(_paths.DatabasePath);

        var store = new SqliteRuntimeSessionStore(_paths);
        var loaded = await store.TryLoadAsync("teardown-drain");
        Assert.True(loaded.IsOk, loaded.IsOk ? null : loaded.Error.Message);
        Assert.NotNull(loaded.Value);
        Assert.Equal(SessionLifecycle.FrozenReadOnly, loaded.Value!.LifecycleState);
    }

    [Fact]
    public async Task Repeated_prepare_export_shutdown_does_not_need_global_pool_clear()
    {
        for (var i = 0; i < 12; i++)
        {
            var dir = Path.Combine(_dir, "repeat-" + i);
            Directory.CreateDirectory(dir);
            var paths = new RuntimeStatePaths { StateDirectory = dir };
            var migrator = new SqliteRuntimeSchemaMigrator(paths);
            Assert.True((await migrator.MigrateAsync()).IsOk);

            var state = new AppState(SessionId.New("repeat-" + i));
            state.UpdateSession(s => s with
            {
                Name = "repeat-" + i,
                LifecycleState = SessionLifecycle.Ready,
            });

            var store = new SqliteRuntimeSessionStore(paths);
            var manifests = new SqliteJournalManifestStore(paths);
            Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);

            var journal = new FileRuntimeEventJournal(paths, manifests, state.SessionId.Value);
            Assert.True((await journal.RecoverAsync()).IsOk);

            var cpStore = new FileCheckpointStore(paths);
            var checkpoints = new DefaultCheckpointService(
                cpStore, new DefaultCheckpointArtifactBuilder(new CompleteGitWorkspaceProbe()));
            var cp = new ControlPlaneService(
                state,
                TestPaneFactories.Create(new PaneIntelligencePipeline()),
                new PaneIntelligencePipeline(),
                new HeuristicAgentDetector(),
                store: store,
                journal: journal,
                subscriptions: new EventSubscriptionHub(),
                redactor: new DefaultEventPayloadRedactor(),
                checkpoints: checkpoints);
            _planes.Add(cp);

            var prep = await cp.DispatchAsync(
                ProtocolMethods.RuntimeCheckpointPrepare,
                JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
                CancellationToken.None);
            var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;
            var exp = await cp.DispatchAsync(
                ProtocolMethods.RuntimeCheckpointExport,
                JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}"}""").RootElement,
                CancellationToken.None);
            Assert.Equal(CheckpointStates.Exported, exp.GetProperty("state").GetString());

            await cp.ShutdownAsync(CancellationToken.None);
            await journal.DisposeAsync();
            SqliteTestCleanup.ReleaseDatabase(paths.DatabasePath);
        }
    }

    private static async Task<bool> RunGitAsync(string cwd, params string[] args)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            proc.StartInfo.ArgumentList.Add(a);
        GitProbeEnvironment.ApplyTo(proc.StartInfo.Environment);
        if (!proc.Start())
            return false;
        await proc.WaitForExitAsync();
        return proc.ExitCode == 0;
    }

    private sealed class FakeConnection(string id) : IClientConnection
    {
        public string ConnectionId { get; } = id;
        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingPaneFactory : IPaneRuntimeFactory
    {
        private int _writes;
        private int _resizes;
        public int WriteCount => Volatile.Read(ref _writes);
        public int ResizeCount => Volatile.Read(ref _resizes);

        public IPaneRuntime Create(PaneSpawnOptions options) => new RecordingPaneRuntime(options.Id, this);

        internal void NoteWrite() => Interlocked.Increment(ref _writes);
        internal void NoteResize() => Interlocked.Increment(ref _resizes);
    }

    private sealed class RecordingPaneRuntime(PaneId id, RecordingPaneFactory factory) : IPaneRuntime
    {
        public PaneId Id { get; } = id;
        public bool IsAlive { get; private set; } = true;
        public int? ExitCode { get; private set; }
        public int? Pid { get; private set; } = 42_001;
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
            factory.NoteWrite();
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteTextAsync(string text, CancellationToken ct)
        {
            factory.NoteWrite();
            return ValueTask.CompletedTask;
        }

        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct)
        {
            factory.NoteResize();
            return ValueTask.CompletedTask;
        }
        public string ReadVisibleText() => "";
        public string ReadRecentText(int maxLines) => "";
        public string ReadRecentUnwrappedText(int maxLines) => ReadRecentText(maxLines);
        public string ReadDetectionText() => "";
        public ValueTask DisposeAsync()
        {
            IsAlive = false;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Blocks in StartAsync until released, then throws (spawn failure).</summary>
    private sealed class HoldThenFailPaneFactory(TaskCompletionSource hold) : IPaneRuntimeFactory
    {
        public IPaneRuntime Create(PaneSpawnOptions options) =>
            new HoldThenFailRuntime(options.Id, hold);
    }

    private sealed class HoldThenFailRuntime(PaneId id, TaskCompletionSource hold) : IPaneRuntime
    {
        public PaneId Id { get; } = id;
        public bool IsAlive => false;
        public int? ExitCode => null;
        public int? Pid => null;
#pragma warning disable CS0067
        public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
        public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
        public event Action<IPaneRuntime, int>? Exited;
#pragma warning restore CS0067

        public async Task StartAsync(CancellationToken ct)
        {
            await hold.Task.WaitAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException("simulated spawn failure after hold");
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask WriteTextAsync(string text, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct) => ValueTask.CompletedTask;
        public string ReadVisibleText() => "";
        public string ReadRecentText(int maxLines) => "";
        public string ReadRecentUnwrappedText(int maxLines) => ReadRecentText(maxLines);
        public string ReadDetectionText() => "";
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Blocks in StartAsync until released, then succeeds (alive pane).</summary>
    private sealed class HoldThenSucceedPaneFactory(TaskCompletionSource hold) : IPaneRuntimeFactory
    {
        public IPaneRuntime Create(PaneSpawnOptions options) =>
            new HoldThenSucceedRuntime(options.Id, hold);
    }

    private sealed class HoldThenSucceedRuntime(PaneId id, TaskCompletionSource hold) : IPaneRuntime
    {
        public PaneId Id { get; } = id;
        public bool IsAlive { get; private set; }
        public int? ExitCode { get; private set; }
        public int? Pid { get; private set; }
#pragma warning disable CS0067
        public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
        public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
        public event Action<IPaneRuntime, int>? Exited;
#pragma warning restore CS0067

        public async Task StartAsync(CancellationToken ct)
        {
            await hold.Task.WaitAsync(ct).ConfigureAwait(false);
            IsAlive = true;
            Pid = 42_042;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask WriteTextAsync(string text, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct) => ValueTask.CompletedTask;
        public string ReadVisibleText() => "";
        public string ReadRecentText(int maxLines) => "";
        public string ReadRecentUnwrappedText(int maxLines) => ReadRecentText(maxLines);
        public string ReadDetectionText() => "";
        public ValueTask DisposeAsync()
        {
            IsAlive = false;
            return ValueTask.CompletedTask;
        }
    }
}
