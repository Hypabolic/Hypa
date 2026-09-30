using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Metadata;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class SqliteRuntimeSessionStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public SqliteRuntimeSessionStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-h02-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose()
    {
        SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);
    }

    private async Task PrepareAsync()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        var m = await migrator.MigrateAsync();
        Assert.True(m.IsOk, m.IsOk ? null : m.Error.Message);
    }

    [Fact]
    public async Task Store_round_trips_session_workspace_tab_pane_and_binding_json()
    {
        await PrepareAsync();
        var store = new SqliteRuntimeSessionStore(_paths);

        var sessionId = SessionId.New("roundtrip");
        var wsId = WorkspaceId.New();
        var tabId = TabId.New();
        var paneId = PaneId.New();
        var binding = new AtomicBinding
        {
            AgentSessionId = "as_1",
            RunId = "run_1",
            StepId = "step_1",
            MemoryId = "mem_1",
            ProjectRoot = "/proj",
        };

        var original = new SessionState
        {
            Id = sessionId,
            Name = "roundtrip",
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
            PlacementGeneration = 2,
            ReplayComplete = true,
            ProcessTenantId = "tenant_rt",
            ProcessRunId = "run_rt",
            StartedAt = DateTimeOffset.Parse("2026-08-12T00:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-08-12T01:00:00Z"),
            FocusedWorkspaceId = wsId,
            Workspaces = new Dictionary<string, WorkspaceState>
            {
                [wsId.Value] = new WorkspaceState
                {
                    Id = wsId,
                    Label = "ws",
                    Cwd = "/workspace",
                    FocusedTabId = tabId,
                    TabIds = [tabId],
                    Binding = binding,
                },
            },
            Tabs = new Dictionary<string, TabState>
            {
                [tabId.Value] = new TabState
                {
                    Id = tabId,
                    WorkspaceId = wsId,
                    Label = "main",
                    Ordinal = 0,
                    PaneIds = [paneId],
                    FocusedPaneId = paneId,
                },
            },
            Panes = new Dictionary<string, PaneState>
            {
                [paneId.Value] = new PaneState
                {
                    Id = paneId,
                    TabId = tabId,
                    WorkspaceId = wsId,
                    Label = "shell",
                    Cwd = "/workspace",
                    Command = "bash",
                    Cols = 80,
                    Rows = 24,
                    LifecycleState = PaneLifecycle.Running,
                    OccupantGeneration = 0,
                    Pid = 4242,
                    AgentStatus = AgentStatus.Working,
                    AgentKind = "pi",
                    AgentName = "reviewer",
                    AgentMessage = "thinking about the next step",
                    Binding = binding,
                    Seen = false,
                    RightClick = "pane",
                    IsAlive = true,
                    CreatedAt = DateTimeOffset.Parse("2026-08-12T00:05:00Z"),
                    UpdatedAt = DateTimeOffset.Parse("2026-08-12T00:06:00Z"),
                },
            },
        };

        var save = await store.SaveAsync(original);
        Assert.True(save.IsOk, save.IsOk ? null : save.Error.Message);

        var load = await store.TryLoadAsync("roundtrip");
        Assert.True(load.IsOk);
        Assert.NotNull(load.Value);
        var loaded = load.Value!;

        Assert.Equal(sessionId.Value, loaded.Id.Value);
        Assert.Equal("roundtrip", loaded.Name);
        Assert.Equal(SessionLifecycle.Ready, loaded.LifecycleState);
        Assert.Equal("local", loaded.Placement);
        Assert.Equal(2, loaded.PlacementGeneration);
        Assert.True(loaded.ReplayComplete);
        Assert.Single(loaded.Workspaces);
        Assert.Single(loaded.Tabs);
        Assert.Single(loaded.Panes);

        var pane = loaded.Panes[paneId.Value];
        Assert.Equal("shell", pane.Label);
        Assert.Equal("reviewer", pane.AgentName);
        Assert.Equal("bash", pane.Command);
        Assert.Equal(80, pane.Cols);
        Assert.Equal(4242, pane.Pid);
        Assert.Equal(PaneLifecycle.Running, pane.LifecycleState);
        Assert.False(pane.IsAlive); // never load as alive from disk
        // stored generation 0 (DEFAULT / legacy) clamps to first-occupant identity 1.
        Assert.Equal(1, pane.OccupantGeneration);
        Assert.Equal("run_1", pane.Binding?.RunId);
        Assert.Equal("as_1", pane.Binding?.AgentSessionId);
        Assert.Equal("step_1", pane.Binding?.StepId);
        Assert.Equal("/proj", pane.Binding?.ProjectRoot);
        Assert.False(pane.Seen);
        Assert.Equal("pane", pane.RightClick);

        var ws = loaded.Workspaces[wsId.Value];
        Assert.Equal("/workspace", ws.Cwd);
        Assert.False(ws.DefaultPanePending);
        Assert.Equal("run_1", ws.Binding?.RunId);
        Assert.Equal(wsId.Value, loaded.FocusedWorkspaceId?.Value);
        Assert.Equal("tenant_rt", loaded.ProcessTenantId);
        Assert.Equal("run_rt", loaded.ProcessRunId);
        Assert.Equal(paneId.Value, loaded.Tabs[tabId.Value].FocusedPaneId?.Value);
        Assert.Equal("thinking about the next step", pane.AgentMessage);
    }

    [Fact]
    public async Task Save_removeMissing_deletes_absent_workspace()
    {
        await PrepareAsync();
        var store = new SqliteRuntimeSessionStore(_paths);

        var sessionId = SessionId.New("ws-remove");
        var keepId = WorkspaceId.New();
        var dropId = WorkspaceId.New();
        var keepTab = TabId.New();
        var dropTab = TabId.New();

        var original = new SessionState
        {
            Id = sessionId,
            Name = "ws-remove",
            LifecycleState = SessionLifecycle.Ready,
            FocusedWorkspaceId = keepId,
            Workspaces = new Dictionary<string, WorkspaceState>
            {
                [keepId.Value] = new WorkspaceState
                {
                    Id = keepId,
                    Label = "keep",
                    Cwd = "/keep",
                    FocusedTabId = keepTab,
                    TabIds = [keepTab],
                },
                [dropId.Value] = new WorkspaceState
                {
                    Id = dropId,
                    Label = "drop",
                    Cwd = "/drop",
                    FocusedTabId = dropTab,
                    TabIds = [dropTab],
                },
            },
            Tabs = new Dictionary<string, TabState>
            {
                [keepTab.Value] = new TabState
                {
                    Id = keepTab,
                    WorkspaceId = keepId,
                    Label = "main",
                    Ordinal = 0,
                },
                [dropTab.Value] = new TabState
                {
                    Id = dropTab,
                    WorkspaceId = dropId,
                    Label = "gone",
                    Ordinal = 0,
                },
            },
            Panes = new Dictionary<string, PaneState>(),
        };

        Assert.True((await store.SaveAsync(original)).IsOk);

        var reduced = original with
        {
            Workspaces = new Dictionary<string, WorkspaceState>
            {
                [keepId.Value] = original.Workspaces[keepId.Value],
            },
            Tabs = new Dictionary<string, TabState>
            {
                [keepTab.Value] = original.Tabs[keepTab.Value],
            },
            FocusedWorkspaceId = keepId,
        };

        Assert.True((await store.SaveAsync(reduced, removeMissingPanes: true)).IsOk);

        var loaded = (await store.TryLoadAsync("ws-remove")).Value;
        Assert.NotNull(loaded);
        Assert.True(loaded!.Workspaces.ContainsKey(keepId.Value));
        Assert.False(loaded.Workspaces.ContainsKey(dropId.Value));
        Assert.False(loaded.Tabs.ContainsKey(dropTab.Value));
        Assert.Equal(keepId.Value, loaded.FocusedWorkspaceId?.Value);
    }

    [Fact]
    public async Task Store_does_not_overwrite_event_cursor_written_by_journal()
    {
        await PrepareAsync();
        var store = new SqliteRuntimeSessionStore(_paths);
        var manifests = new SqliteJournalManifestStore(_paths);

        var sessionId = SessionId.New("cursor");
        var state = new SessionState
        {
            Id = sessionId,
            Name = "cursor",
            NextEventSeq = 1, // stale graph snapshot
            Workspaces = new Dictionary<string, WorkspaceState>(),
            Tabs = new Dictionary<string, TabState>(),
            Panes = new Dictionary<string, PaneState>(),
        };

        Assert.True((await store.SaveAsync(state)).IsOk);

        // Journal is sole writer of event_cursor / runtime_meta.event_seq.
        Assert.True((await manifests.WriteEventCursorAsync(sessionId.Value, 42)).IsOk);

        // Concurrent graph save with stale NextEventSeq must not regress the cursor.
        Assert.True((await store.SaveAsync(state with { NextEventSeq = 1 })).IsOk);

        await using var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}");
        await conn.OpenAsync();
        await using var j = conn.CreateCommand();
        j.CommandText = "SELECT COUNT(*) FROM journal_segments";
        Assert.Equal(0L, (long)(await j.ExecuteScalarAsync())!);
        await using var s = conn.CreateCommand();
        s.CommandText = "SELECT COUNT(*) FROM scrollback_segments";
        Assert.Equal(0L, (long)(await s.ExecuteScalarAsync())!);

        await using var meta = conn.CreateCommand();
        meta.CommandText = "SELECT value FROM runtime_meta WHERE key='event_seq'";
        Assert.Equal("42", (string?)await meta.ExecuteScalarAsync());
        await using var cursor = conn.CreateCommand();
        cursor.CommandText = "SELECT next_seq FROM event_cursor WHERE session_id=@sid";
        cursor.Parameters.AddWithValue("@sid", sessionId.Value);
        Assert.Equal(42L, Convert.ToInt64(await cursor.ExecuteScalarAsync()));

        // Load still reads the journal cursor (not the stale graph field write).
        var loaded = (await store.TryLoadAsync("cursor")).Value;
        Assert.NotNull(loaded);
        Assert.Equal(42, loaded!.NextEventSeq);
    }

    [Fact]
    public async Task Persist_then_new_store_instance_restores_graph_metadata_ids_and_labels()
    {
        await PrepareAsync();
        var sessionId = SessionId.New("persist-kill");
        var wsId = WorkspaceId.New();
        var tabId = TabId.New();
        var paneId = PaneId.New();

        var state = new SessionState
        {
            Id = sessionId,
            Name = "persist-kill",
            FocusedWorkspaceId = wsId,
            Workspaces = new Dictionary<string, WorkspaceState>
            {
                [wsId.Value] = new WorkspaceState
                {
                    Id = wsId,
                    Label = "project",
                    Cwd = "/tmp/proj",
                    FocusedTabId = tabId,
                    TabIds = [tabId],
                },
            },
            Tabs = new Dictionary<string, TabState>
            {
                [tabId.Value] = new TabState
                {
                    Id = tabId,
                    WorkspaceId = wsId,
                    Label = "main",
                    Ordinal = 0,
                    PaneIds = [paneId],
                },
            },
            Panes = new Dictionary<string, PaneState>
            {
                [paneId.Value] = new PaneState
                {
                    Id = paneId,
                    TabId = tabId,
                    WorkspaceId = wsId,
                    Label = "agent",
                    Cwd = "/tmp/proj",
                    Command = "pi",
                    LifecycleState = PaneLifecycle.Exited,
                    ExitCode = 0,
                    AgentStatus = AgentStatus.Done,
                },
            },
        };

        await using (var store1 = new SqliteRuntimeSessionStore(_paths))
        {
            Assert.True((await store1.SaveAsync(state)).IsOk);
        }

        SqliteTestCleanup.ReleaseDatabase(_paths.DatabasePath);

        await using var store2 = new SqliteRuntimeSessionStore(_paths);
        var loaded = (await store2.TryLoadAsync("persist-kill")).Value;
        Assert.NotNull(loaded);
        Assert.Equal(wsId.Value, loaded!.Workspaces.Keys.Single());
        Assert.Equal("project", loaded.Workspaces[wsId.Value].Label);
        Assert.Equal(paneId.Value, loaded.Panes.Keys.Single());
        Assert.Equal("agent", loaded.Panes[paneId.Value].Label);
        Assert.Equal(0, loaded.Panes[paneId.Value].ExitCode);
    }

    [Fact]
    public async Task Corrupt_agent_authority_json_fails_closed()
    {
        await PrepareAsync();
        var sessionId = SessionId.New("bad-authority");
        var wsId = WorkspaceId.New();
        var tabId = TabId.New();
        var paneId = PaneId.New();
        var state = new SessionState
        {
            Id = sessionId,
            Name = "bad-authority",
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
            ReplayComplete = true,
            StartedAt = DateTimeOffset.Parse("2026-08-12T00:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-08-12T00:00:00Z"),
            FocusedWorkspaceId = wsId,
            Workspaces = new Dictionary<string, WorkspaceState>
            {
                [wsId.Value] = new WorkspaceState
                {
                    Id = wsId,
                    Label = "ws",
                    Cwd = "/tmp",
                    FocusedTabId = tabId,
                    TabIds = [tabId],
                },
            },
            Tabs = new Dictionary<string, TabState>
            {
                [tabId.Value] = new TabState
                {
                    Id = tabId,
                    WorkspaceId = wsId,
                    Label = "main",
                    Ordinal = 0,
                    PaneIds = [paneId],
                    FocusedPaneId = paneId,
                },
            },
            Panes = new Dictionary<string, PaneState>
            {
                [paneId.Value] = new PaneState
                {
                    Id = paneId,
                    TabId = tabId,
                    WorkspaceId = wsId,
                    Label = "agent",
                    Cwd = "/tmp",
                    Command = "/bin/sh",
                    LifecycleState = PaneLifecycle.Exited,
                    OccupantGeneration = 1,
                    AgentStatus = AgentStatus.Blocked,
                    AgentKind = "claude",
                    AgentAuthority = new PaneAgentAuthority
                    {
                        Source = "plugin:claude",
                        Agent = "claude",
                        State = AgentStatus.Blocked,
                        ReportedAt = DateTimeOffset.Parse("2026-08-12T00:00:00Z"),
                    },
                },
            },
        };

        await using var store = new SqliteRuntimeSessionStore(_paths);
        Assert.True((await store.SaveAsync(state)).IsOk);

        await using (var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE panes SET agent_authority_json = 'not-json {' WHERE pane_id = @id";
            cmd.Parameters.AddWithValue("@id", paneId.Value);
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        }

        var load = await store.TryLoadAsync("bad-authority");
        Assert.False(load.IsOk);
        Assert.Contains("agent_authority_json", load.Error.Message, StringComparison.Ordinal);

        var tooManySources = "{\"sequences\":{"
            + string.Join(",", Enumerable.Range(0, MetadataTokenLimits.MaxSequencedSources + 1)
                .Select(i => $"\"src{i}\":{i + 1}"))
            + "}}";
        string[] invalidPayloads =
        [
            """{"authority":{"source":"plugin:claude","agent":"claude","state":"bogus","reported_at":"2026-08-12T00:00:00Z"}}""",
            """{"authority":{"source":"","agent":"claude","state":"blocked","reported_at":"2026-08-12T00:00:00Z"}}""",
            """{"authority":{"source":"plugin:claude","agent":"","state":"blocked","reported_at":"2026-08-12T00:00:00Z"}}""",
            """{"agent_session":{"kind":"bogus","value":"sess_1","source":"plugin:claude","agent":"claude"}}""",
            """{"agent_session":{"kind":"id","value":"","source":"plugin:claude","agent":"claude"}}""",
            """{"agent_session":{"kind":"id","value":"sess_1","source":"plugin:claude","agent":"claude","session_start_source":"nope"}}""",
            """{"sequences":{"bad source!":2}}""",
            """{"sequences":{"":2}}""",
            """{"sequences":{"plugin:claude":"2"}}""",
            tooManySources,
        ];
        for (var i = 0; i < invalidPayloads.Length; i++)
        {
            var sessionName = "bad-authority-" + i;
            var caseSessionId = SessionId.New(sessionName);
            var caseWs = WorkspaceId.New();
            var caseTab = TabId.New();
            var casePane = PaneId.New();
            var caseState = state with
            {
                Id = caseSessionId,
                Name = sessionName,
                FocusedWorkspaceId = caseWs,
                Workspaces = new Dictionary<string, WorkspaceState>
                {
                    [caseWs.Value] = state.Workspaces[wsId.Value] with
                    {
                        Id = caseWs,
                        FocusedTabId = caseTab,
                        TabIds = [caseTab],
                    },
                },
                Tabs = new Dictionary<string, TabState>
                {
                    [caseTab.Value] = state.Tabs[tabId.Value] with
                    {
                        Id = caseTab,
                        WorkspaceId = caseWs,
                        PaneIds = [casePane],
                        FocusedPaneId = casePane,
                    },
                },
                Panes = new Dictionary<string, PaneState>
                {
                    [casePane.Value] = state.Panes[paneId.Value] with
                    {
                        Id = casePane,
                        TabId = caseTab,
                        WorkspaceId = caseWs,
                    },
                },
            };
            Assert.True((await store.SaveAsync(caseState)).IsOk);
            await using (var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}"))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE panes SET agent_authority_json = @json WHERE pane_id = @id";
                cmd.Parameters.AddWithValue("@json", invalidPayloads[i]);
                cmd.Parameters.AddWithValue("@id", casePane.Value);
                Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
            }

            var caseLoad = await store.TryLoadAsync(sessionName);
            Assert.False(caseLoad.IsOk);
            Assert.Contains("agent_authority_json", caseLoad.Error.Message, StringComparison.Ordinal);
        }

        var seqSessionName = "authority-seq-reload";
        var seqSessionId = SessionId.New(seqSessionName);
        var seqWs = WorkspaceId.New();
        var seqTab = TabId.New();
        var seqPane = PaneId.New();
        var seqState = state with
        {
            Id = seqSessionId,
            Name = seqSessionName,
            FocusedWorkspaceId = seqWs,
            Workspaces = new Dictionary<string, WorkspaceState>
            {
                [seqWs.Value] = state.Workspaces[wsId.Value] with
                {
                    Id = seqWs,
                    FocusedTabId = seqTab,
                    TabIds = [seqTab],
                },
            },
            Tabs = new Dictionary<string, TabState>
            {
                [seqTab.Value] = state.Tabs[tabId.Value] with
                {
                    Id = seqTab,
                    WorkspaceId = seqWs,
                    PaneIds = [seqPane],
                    FocusedPaneId = seqPane,
                },
            },
            Panes = new Dictionary<string, PaneState>
            {
                [seqPane.Value] = state.Panes[paneId.Value] with
                {
                    Id = seqPane,
                    TabId = seqTab,
                    WorkspaceId = seqWs,
                    AgentStatus = AgentStatus.Working,
                    AgentAuthority = new PaneAgentAuthority
                    {
                        Source = "plugin:claude",
                        Agent = "claude",
                        State = AgentStatus.Working,
                        ReportedAt = DateTimeOffset.Parse("2026-08-12T00:00:00Z"),
                    },
                    AgentAuthoritySequences = new Dictionary<string, long>(StringComparer.Ordinal)
                    {
                        ["plugin:claude"] = 2,
                    },
                },
            },
        };
        Assert.True((await store.SaveAsync(seqState)).IsOk);

        var seqLoad = await store.TryLoadAsync(seqSessionName);
        Assert.True(seqLoad.IsOk);
        Assert.NotNull(seqLoad.Value);
        Assert.Equal(2, seqLoad.Value!.Panes[seqPane.Value].AgentAuthoritySequences["plugin:claude"]);

        var restored = new AppState(seqSessionId);
        restored.Replace(seqLoad.Value);
        var cp = new ControlPlaneService(
            restored,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store);
        try
        {
            var stale = await cp.DispatchAsync(
                ProtocolMethods.PaneReportAgent,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = seqPane.Value,
                    ["source"] = "plugin:claude",
                    ["agent"] = "claude",
                    ["state"] = "blocked",
                    ["seq"] = 1,
                }.ToJsonString()).RootElement,
                CancellationToken.None);
            Assert.True(stale.GetProperty("ok").GetBoolean());
            var got = await cp.DispatchAsync(
                ProtocolMethods.AgentGet,
                JsonDocument.Parse(new JsonObject { ["pane_id"] = seqPane.Value }.ToJsonString()).RootElement,
                CancellationToken.None);
            Assert.Equal("working", got.GetProperty("state").GetString());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }
}
