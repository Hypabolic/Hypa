using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

[Collection("CheckpointTests")]
public class CheckpointPrepareTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;
    private readonly List<ControlPlaneService> _planes = [];

    public CheckpointPrepareTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-h10-prep-" + Guid.NewGuid().ToString("N"));
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

    public void Dispose()
    {
        SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);
    }

    private async Task<(ControlPlaneService Cp, AppState State, FileRuntimeEventJournal Journal, ICheckpointService Checkpoints)>
        CreatePlaneAsync(string sessionName)
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
        var builder = new DefaultCheckpointArtifactBuilder();
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

        return (cp, state, journal, checkpoints);
    }

    [Fact]
    public async Task Prepare_freezes_session_and_writes_barrier_seq()
    {
        var (cp, state, journal, _) = await CreatePlaneAsync("prep1");

        // Seed one durable event so barrier is non-zero.
        Assert.True((await journal.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            ProtocolEventTypes.SessionLifecycle, """{"state":"ready"}""")).IsOk);
        var nextBefore = journal.NextSeq;
        Assert.True(nextBefore > 1);

        var result = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"reason":"test","include_workspace_files":false}""").RootElement,
            CancellationToken.None);

        Assert.Equal("prepared", result.GetProperty("state").GetString());
        Assert.Equal(nextBefore - 1, result.GetProperty("barrier_seq").GetInt64());
        Assert.Equal(SessionLifecycle.FrozenReadOnly, result.GetProperty("session_state").GetString());
        Assert.Equal(state.SessionId.Value, result.GetProperty("runtime_session_id").GetString());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("checkpoint_id").GetString()));

        Assert.Equal(SessionLifecycle.FrozenReadOnly, state.Snapshot().LifecycleState);

        // Lifecycle event journaled after barrier.
        var range = await journal.ReadRangeAsync(0, classes: null, budget: 50, CancellationToken.None);
        Assert.True(range.IsOk);
        Assert.Contains(range.Value, r =>
            r.Type == ProtocolEventTypes.CheckpointLifecycle &&
            r.PayloadJson.Contains("prepared", StringComparison.Ordinal));

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Prepare_empty_journal_barrier_is_zero()
    {
        var (cp, _, journal, _) = await CreatePlaneAsync("prep0");
        Assert.Equal(1, journal.NextSeq);

        var result = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
            CancellationToken.None);

        Assert.Equal(0, result.GetProperty("barrier_seq").GetInt64());
        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Mutations_while_frozen_return_invalid_state()
    {
        var (cp, _, journal, _) = await CreatePlaneAsync("prep-freeze");

        await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
            CancellationToken.None);

        var createEx = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            cp.DispatchAsync(
                "workspace.create",
                JsonDocument.Parse("""{"cwd":"/tmp","create_pane":false}""").RootElement,
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.InvalidState, createEx.Code);

        var bindEx = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            cp.DispatchAsync(
                ProtocolMethods.RuntimeBindingSet,
                JsonDocument.Parse("""{"binding":{"run_id":"r1"}}""").RootElement,
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.InvalidState, bindEx.Code);

        var promptEx = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            cp.DispatchAsync(
                ProtocolMethods.AgentPrompt,
                JsonDocument.Parse("""{"pane_id":"p1","message":"hi"}""").RootElement,
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.InvalidState, promptEx.Code);

        var keysEx = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            cp.DispatchAsync(
                ProtocolMethods.PaneSendKeys,
                JsonDocument.Parse("""{"pane_id":"p1","lease_id":"l1","encoding":"utf8","data":"x"}""").RootElement,
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.InvalidState, keysEx.Code);

        var resizeEx = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            cp.DispatchAsync(
                "pane.resize",
                JsonDocument.Parse("""{"pane_id":"p1","cols":80,"rows":24}""").RootElement,
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.InvalidState, resizeEx.Code);

        var wsFocusEx = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            cp.DispatchAsync(
                ProtocolMethods.WorkspaceFocus,
                JsonDocument.Parse("""{"workspace_id":"w1"}""").RootElement,
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.InvalidState, wsFocusEx.Code);

        var wsRenameEx = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            cp.DispatchAsync(
                ProtocolMethods.WorkspaceRename,
                JsonDocument.Parse("""{"workspace_id":"w1","label":"x"}""").RootElement,
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.InvalidState, wsRenameEx.Code);

        var wsCloseEx = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            cp.DispatchAsync(
                ProtocolMethods.WorkspaceClose,
                JsonDocument.Parse("""{"workspace_id":"w1"}""").RootElement,
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.InvalidState, wsCloseEx.Code);

        // Reads / health still allowed.
        var health = await cp.DispatchAsync(
            ProtocolMethods.RuntimeHealth, parameters: null, CancellationToken.None);
        Assert.True(health.GetProperty("ready").GetBoolean());
        var caps = health.GetProperty("capabilities").EnumerateArray()
            .Select(e => e.GetString()).ToArray();
        Assert.Contains(ProtocolCapabilities.Checkpoint, caps);

        var snap = await cp.DispatchAsync("session.snapshot", parameters: null, CancellationToken.None);
        Assert.Equal(SessionLifecycle.FrozenReadOnly, snap.GetProperty("session_state").GetString());

        var renameEx = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            cp.DispatchAsync(
                ProtocolMethods.PaneRename,
                JsonDocument.Parse("""{"pane_id":"p1","label":"x"}""").RootElement,
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.InvalidState, renameEx.Code);

        var focusEx = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            cp.DispatchAsync(
                ProtocolMethods.PaneFocus,
                JsonDocument.Parse("""{"pane_id":"p1"}""").RootElement,
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.InvalidState, focusEx.Code);

        var inputSetEx = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            cp.DispatchAsync(
                ProtocolMethods.PaneInputSet,
                JsonDocument.Parse("""{"pane_id":"p1","right_click":"pane"}""").RootElement,
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.InvalidState, inputSetEx.Code);

        var sendInputEx = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            cp.DispatchAsync(
                ProtocolMethods.PaneSendInput,
                JsonDocument.Parse("""{"pane_id":"p1","keys":["l"]}""").RootElement,
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.InvalidState, sendInputEx.Code);

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Reads_while_frozen_are_allowed()
    {
        var (cp, state, journal, _) = await CreatePlaneAsync("prep-h53-read");
        var created = await cp.DispatchAsync(
            ProtocolMethods.WorkspaceCreate,
            JsonDocument.Parse("""{"cwd":"/tmp/h53-freeze","create_pane":false,"label":"ws"}""").RootElement,
            CancellationToken.None);
        var workspaceId = created.GetProperty("workspace_id").GetString()!;
        var tabId = created.GetProperty("focused_tab_id").GetString()!;
        var pane = state.RegisterPane(new PaneState
        {
            Id = PaneId.New(),
            TabId = new TabId(tabId),
            WorkspaceId = new WorkspaceId(workspaceId),
            Label = "shell",
            Cwd = "/tmp/h53-freeze",
            Command = "/bin/sh",
            IsAlive = true,
        });
        var paneId = pane.Id.Value;

        await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
            CancellationToken.None);

        var current = await cp.DispatchAsync(
            ProtocolMethods.PaneCurrent, parameters: null, CancellationToken.None);
        Assert.Equal(paneId, current.GetProperty("pane_id").GetString());

        var neighbor = await cp.DispatchAsync(
            ProtocolMethods.PaneNeighbor,
            JsonDocument.Parse("""{"direction":"right"}""").RootElement,
            CancellationToken.None);
        Assert.Equal(paneId, neighbor.GetProperty("pane_id").GetString());

        var edges = await cp.DispatchAsync(
            ProtocolMethods.PaneEdges, parameters: null, CancellationToken.None);
        Assert.Equal(paneId, edges.GetProperty("pane_id").GetString());

        var info = await cp.DispatchAsync(
            ProtocolMethods.PaneProcessInfo,
            JsonDocument.Parse(new JsonObject { ["pane_id"] = paneId }.ToJsonString()).RootElement,
            CancellationToken.None);
        Assert.Equal(paneId, info.GetProperty("pane_id").GetString());

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Second_prepare_while_frozen_is_invalid_state()
    {
        var (cp, _, journal, _) = await CreatePlaneAsync("prep-twice");

        await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            cp.DispatchAsync(
                ProtocolMethods.RuntimeCheckpointPrepare,
                JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.InvalidState, ex.Code);

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Abort_unfreezes_session_and_allows_mutations()
    {
        var (cp, state, journal, checkpoints) = await CreatePlaneAsync("prep-abort");

        var prep = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
            CancellationToken.None);
        var checkpointId = prep.GetProperty("checkpoint_id").GetString()!;
        Assert.Equal(SessionLifecycle.FrozenReadOnly, state.Snapshot().LifecycleState);

        var aborted = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointAbort,
            JsonDocument.Parse($$"""{"checkpoint_id":"{{checkpointId}}","reason":"test-abort"}""").RootElement,
            CancellationToken.None);

        Assert.Equal(CheckpointStates.Aborted, aborted.GetProperty("state").GetString());
        Assert.Equal(SessionLifecycle.Ready, aborted.GetProperty("session_state").GetString());
        Assert.Equal(SessionLifecycle.Ready, state.Snapshot().LifecycleState);

        var loaded = await checkpoints.GetAsync(checkpointId);
        Assert.True(loaded.IsOk);
        Assert.Equal(CheckpointStates.Aborted, loaded.Value!.State);

        // Mutations work again.
        var ws = await cp.DispatchAsync(
            "workspace.create",
            JsonDocument.Parse("""{"cwd":"/tmp","create_pane":false}""").RootElement,
            CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(ws.GetProperty("workspace_id").GetString()));

        // Prepare can run again.
        var prep2 = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
            CancellationToken.None);
        Assert.Equal("prepared", prep2.GetProperty("state").GetString());

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_workspace_create_with_prepare_fails_closed_or_exports()
    {
        var (cp, state, journal, _) = await CreatePlaneAsync("prep-race");

        // Seed graph with one workspace so pane/workspace creates are meaningful.
        await cp.DispatchAsync(
            "workspace.create",
            JsonDocument.Parse("""{"cwd":"/tmp/race-a","create_pane":false,"label":"a"}""").RootElement,
            CancellationToken.None);

        var mutationHits = 0;
        var mutationInvalid = 0;
        var prepareOk = 0;
        var prepareInvalid = 0;

        var tasks = new List<Task>();
        for (var i = 0; i < 8; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    if (idx % 2 == 0)
                    {
                        await cp.DispatchAsync(
                            "workspace.create",
                            JsonDocument.Parse(
                                $$"""{"cwd":"/tmp/race-{{idx}}","create_pane":false,"label":"r{{idx}}"}""")
                                .RootElement,
                            CancellationToken.None);
                        Interlocked.Increment(ref mutationHits);
                    }
                    else
                    {
                        await cp.DispatchAsync(
                            ProtocolMethods.RuntimeCheckpointPrepare,
                            JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
                            CancellationToken.None);
                        Interlocked.Increment(ref prepareOk);
                    }
                }
                catch (ControlPlaneException ex) when (ex.Code == ProtocolErrorCodes.InvalidState)
                {
                    if (idx % 2 == 0)
                        Interlocked.Increment(ref mutationInvalid);
                    else
                        Interlocked.Increment(ref prepareInvalid);
                }
            }));
        }

        await Task.WhenAll(tasks);

        // At most one prepare succeeds; mutations either land before freeze or fail invalid_state.
        Assert.True(prepareOk <= 1, $"expected at most one prepare success, got {prepareOk}");
        Assert.True(prepareOk + prepareInvalid == 4, "all prepare attempts accounted for");
        Assert.True(mutationHits + mutationInvalid == 4, "all mutation attempts accounted for");

        if (prepareOk == 1)
        {
            Assert.Equal(SessionLifecycle.FrozenReadOnly, state.Snapshot().LifecycleState);
            // Abort recovers: proves freeze is real and not sticky after recovery.
            await cp.DispatchAsync(
                ProtocolMethods.RuntimeCheckpointAbort,
                JsonDocument.Parse("""{}""").RootElement,
                CancellationToken.None);
            Assert.Equal(SessionLifecycle.Ready, state.Snapshot().LifecycleState);
        }

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Prepare_store_failure_does_not_leave_session_frozen()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        var state = new AppState(SessionId.New("prep-failstore"));
        state.UpdateSession(s => s with
        {
            Name = "prep-failstore",
            LifecycleState = SessionLifecycle.Ready,
        });

        var store = new SqliteRuntimeSessionStore(_paths);
        var manifests = new SqliteJournalManifestStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);

        var journal = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);

        var failStore = new FailingCheckpointStore();
        var builder = new DefaultCheckpointArtifactBuilder(new CompleteGitWorkspaceProbe());
        var checkpoints = new DefaultCheckpointService(failStore, builder);

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

        var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            cp.DispatchAsync(
                ProtocolMethods.RuntimeCheckpointPrepare,
                JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
                CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.PersistenceUnavailable, ex.Code);
        Assert.Equal(SessionLifecycle.Ready, state.Snapshot().LifecycleState);

        // Mutations still work.
        var ws = await cp.DispatchAsync(
            "workspace.create",
            JsonDocument.Parse("""{"cwd":"/tmp","create_pane":false}""").RootElement,
            CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(ws.GetProperty("workspace_id").GetString()));

        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Prepare_samples_barrier_under_journal_exclusive()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        var state = new AppState(SessionId.New("prep-excl"));
        state.UpdateSession(s => s with
        {
            Name = "prep-excl",
            LifecycleState = SessionLifecycle.Ready,
        });

        var store = new SqliteRuntimeSessionStore(_paths);
        var manifests = new SqliteJournalManifestStore(_paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);

        var inner = new FileRuntimeEventJournal(_paths, manifests, state.SessionId.Value);
        Assert.True((await inner.RecoverAsync()).IsOk);
        Assert.True((await inner.AppendAsync(
            EventClass.Lifecycle, EventReliability.Reliable,
            ProtocolEventTypes.SessionLifecycle, """{"state":"ready"}""")).IsOk);

        var probe = new ExclusiveProbeJournal(inner);
        var cpStore = new FileCheckpointStore(_paths);
        var checkpoints = new DefaultCheckpointService(cpStore, new DefaultCheckpointArtifactBuilder());
        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Create(new PaneIntelligencePipeline()),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: probe,
            subscriptions: new EventSubscriptionHub(),
            redactor: new DefaultEventPayloadRedactor(),
            checkpoints: checkpoints);
        _planes.Add(cp);

        var nextBefore = inner.NextSeq;
        Assert.True(nextBefore > 1);

        var result = await cp.DispatchAsync(
            ProtocolMethods.RuntimeCheckpointPrepare,
            JsonDocument.Parse("""{"include_workspace_files":false}""").RootElement,
            CancellationToken.None);

        Assert.True(probe.RunExclusiveCalled, "prepare must sample NextSeq inside journal.RunExclusive");
        Assert.False(
            probe.AllocationCompletedDuringExclusive,
            "A reliable reserve must not allocate while prepare holds journal exclusive");
        Assert.Equal(nextBefore, probe.NextSeqDuringExclusive);
        Assert.Equal(nextBefore - 1, result.GetProperty("barrier_seq").GetInt64());
        Assert.Equal(SessionLifecycle.FrozenReadOnly, state.Snapshot().LifecycleState);

        if (probe.ProbeAppend is not null)
            await probe.ProbeAppend.WaitAsync(TimeSpan.FromSeconds(5));

        await inner.DisposeAsync();
    }

    /// <summary>Store that always fails SaveAsync (forces prepare unfreeze path).</summary>
    private sealed class FailingCheckpointStore : ICheckpointStore
    {
        public Task<RuntimeResult<CheckpointRecord>> SaveAsync(
            CheckpointRecord record, CancellationToken ct = default) =>
            Task.FromResult(RuntimeResult<CheckpointRecord>.Fail(
                RuntimePersistenceError.Io("forced store failure")));

        public Task<RuntimeResult<CheckpointRecord?>> GetAsync(
            string checkpointId, CancellationToken ct = default) =>
            Task.FromResult(RuntimeResult<CheckpointRecord?>.Ok(null));

        public Task<RuntimeResult<IReadOnlyList<CheckpointRecord>>> ListBySessionAsync(
            string sessionId, CancellationToken ct = default) =>
            Task.FromResult(RuntimeResult<IReadOnlyList<CheckpointRecord>>.Ok(
                Array.Empty<CheckpointRecord>()));

        public Task<RuntimeResult<CheckpointManifestWriteResult>> WriteManifestAsync(
            string checkpointId, string manifestJson, CancellationToken ct = default) =>
            Task.FromResult(RuntimeResult<CheckpointManifestWriteResult>.Fail(
                RuntimePersistenceError.Io("forced store failure")));

        public string GetCheckpointDirectory(string checkpointId) =>
            Path.Combine(Path.GetTempPath(), "hypa-fail-cp", checkpointId);

        public string GetCheckpointRelativeDirectory(string checkpointId) =>
            "checkpoints/" + checkpointId;
    }

    /// <summary>
    /// Delegates to <see cref="FileRuntimeEventJournal"/> and probes that Output
    /// cannot advance <see cref="IRuntimeEventJournal.NextSeq"/> inside
    /// <see cref="IRuntimeEventJournal.RunExclusive"/>.
    /// </summary>
    private sealed class ExclusiveProbeJournal : IRuntimeEventJournal
    {
        private readonly FileRuntimeEventJournal _inner;

        public ExclusiveProbeJournal(FileRuntimeEventJournal inner) => _inner = inner;

        public bool RunExclusiveCalled { get; private set; }
        public bool AllocationCompletedDuringExclusive { get; private set; }
        public long NextSeqDuringExclusive { get; private set; }
        public Task? ProbeAppend { get; private set; }

        public long NextSeq => _inner.NextSeq;

        public JournalHealth GetHealth() => _inner.GetHealth();

        public Task<RuntimeResult<JournalHealth>> RecoverAsync(CancellationToken ct = default) =>
            _inner.RecoverAsync(ct);

        public Task<RuntimeResult<RuntimeEventRecord>> AppendAsync(
            EventClass @class,
            EventReliability reliability,
            string type,
            string payloadJson,
            DateTimeOffset? occurredAt = null,
            CancellationToken ct = default) =>
            _inner.AppendAsync(@class, reliability, type, payloadJson, occurredAt, ct);

        public Task<RuntimeResult<IReadOnlyList<RuntimeEventRecord>>> ReadRangeAsync(
            long fromSeqExclusive,
            IReadOnlySet<EventClass>? classes,
            int budget,
            CancellationToken ct = default) =>
            _inner.ReadRangeAsync(fromSeqExclusive, classes, budget, ct);

        public Task<RuntimeResult<RuntimeUnit>> CloseOpenSegmentAsync(CancellationToken ct = default) =>
            _inner.CloseOpenSegmentAsync(ct);

        public void RunExclusive(Action action)
        {
            RunExclusiveCalled = true;
            _inner.RunExclusive(() =>
            {
                // Terminal output is live-only, so the probe is a reliable reserve:
                // it must not take a sequence while prepare samples the barrier.
                ProbeAppend = Task.Run(() =>
                {
                    _inner.ReserveReliable(
                        EventClass.Lifecycle,
                        ProtocolEventTypes.PaneLifecycle,
                        """{"pane_id":"p_probe"}""",
                        DateTimeOffset.UtcNow);
                });
                AllocationCompletedDuringExclusive = ProbeAppend.Wait(TimeSpan.FromMilliseconds(200));
                NextSeqDuringExclusive = _inner.NextSeq;
                action();
            });
        }
    }
}
