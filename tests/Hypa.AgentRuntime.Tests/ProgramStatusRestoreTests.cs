using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.ProgramStatus;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class ProgramStatusRestoreTests : IDisposable
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-12T00:00:00Z");

    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public ProgramStatusRestoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-psr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose() => SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);

    private sealed class FixedProbe(bool alive) : IProcessLivenessProbe
    {
        public bool IsAlive(int pid) => alive;
    }

    /// <summary>Answers from a list, one per call, then repeats the last answer.</summary>
    private sealed class SequenceProbe(params bool[] answers) : IProcessLivenessProbe
    {
        public int Calls { get; private set; }

        public bool IsAlive(int pid) => answers[Math.Min(Calls++, answers.Length - 1)];
    }

    private static ProgramStatusReport Report(
        ProgramStatusState state, string? id = null, string? msg = null, string? app = null,
        ProgramStatusKind? kind = null, int? progress = null) =>
        new() { State = state, Id = id, Msg = msg, App = app, Kind = kind, Progress = progress };

    private static SessionState Session(string name, Func<WorkspaceId, TabId, PaneId, PaneState> pane)
    {
        var wsId = WorkspaceId.New();
        var tabId = TabId.New();
        var paneId = PaneId.New();
        return new SessionState
        {
            Id = SessionId.New(name),
            Name = name,
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
            ReplayComplete = true,
            StartedAt = At,
            UpdatedAt = At,
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
            Panes = new Dictionary<string, PaneState> { [paneId.Value] = pane(wsId, tabId, paneId) },
        };
    }

    private static PaneState PaneWith(
        WorkspaceId ws, TabId tab, PaneId id, ProgramStatusStore store, int? pid = null,
        PaneAgentAuthority? authority = null) => new()
        {
            Id = id,
            TabId = tab,
            WorkspaceId = ws,
            Label = "p",
            Cwd = "/tmp",
            Command = "/bin/sh",
            LifecycleState = PaneLifecycle.Running,
            OccupantGeneration = 1,
            Pid = pid,
            ProgramStatusStore = store,
            AgentAuthority = authority,
            AgentStatus = AgentStatus.Working,
        };

    private async Task<SessionState> SaveAndLoadAsync(SessionState state)
    {
        var migrated = await new SqliteRuntimeSchemaMigrator(_paths).MigrateAsync();
        Assert.True(migrated.IsOk);
        await using var store = new SqliteRuntimeSessionStore(_paths);
        Assert.True((await store.SaveAsync(state)).IsOk);
        var load = await store.TryLoadAsync(state.Name);
        Assert.True(load.IsOk);
        return load.Value!;
    }

    private static PaneState Only(SessionState state) => Assert.Single(state.Panes).Value;

    [Fact]
    public void Restore_probes_each_pid_once_so_the_records_and_the_pid_agree()
    {
        // The process answers alive, then dead. One probe must decide both the
        // record lifecycle and the PID, so the two cannot disagree.
        var store = ProgramStatusStore.Empty.Apply(
            Report(ProgramStatusState.Working, msg: "build", app: "make"));
        var state = Session("psr-probe", (w, t, p) => PaneWith(w, t, p, store, pid: 4242));
        var probe = new SequenceProbe(true, false);

        var pane = Only(SessionGraphRestorer.ReconcileDeadPids(state, probe));

        Assert.Equal(1, probe.Calls);
        Assert.Equal(4242, pane.Pid);
        Assert.Single(pane.ProgramStatusStore.Records);
    }

    [Fact]
    public async Task Store_round_trips_records_last_seq_and_real_report_flag()
    {
        var store = ProgramStatusStore.Empty
            .Apply(Report(ProgramStatusState.Working, msg: "root", app: "brew", progress: 40))
            .Apply(Report(ProgramStatusState.Blocked, "build", "ask", kind: ProgramStatusKind.Question))
            .Apply(Report(ProgramStatusState.Done, "build/test", "ok"));
        Assert.True(store.RealReportSeen);

        var loaded = await SaveAndLoadAsync(
            Session("psr-round", (w, t, p) => PaneWith(w, t, p, store)));

        var back = Only(loaded).ProgramStatusStore;
        Assert.Equal(store.LastSeq, back.LastSeq);
        Assert.True(back.RealReportSeen);
        Assert.Equal(store.Records.Length, back.Records.Length);
        foreach (var record in store.Records)
            Assert.Contains(back.Records, r => r == record);
        // The derived authority is not stored.
        Assert.Null(Only(loaded).AgentAuthority);
    }

    [Fact]
    public async Task Mapped_progress_store_keeps_the_real_report_flag_false()
    {
        var store = ProgramStatusStore.Empty.Apply(
            Report(ProgramStatusState.Working, progress: 10), ProgramStatusReportSource.ConEmuProgress);
        Assert.False(store.RealReportSeen);

        var loaded = await SaveAndLoadAsync(
            Session("psr-mapped", (w, t, p) => PaneWith(w, t, p, store)));

        Assert.False(Only(loaded).ProgramStatusStore.RealReportSeen);
        Assert.Single(Only(loaded).ProgramStatusStore.Records);
    }

    [Fact]
    public async Task Session_saved_without_program_status_loads_as_an_empty_store()
    {
        var loaded = await SaveAndLoadAsync(
            Session("psr-old", (w, t, p) => PaneWith(w, t, p, ProgramStatusStore.Empty)));
        Assert.True(Only(loaded).ProgramStatusStore.IsEmpty);

        // An older build wrote an authority payload with no program_status field.
        var paneId = Only(loaded).Id.Value;
        await using (var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE panes SET agent_authority_json = @json WHERE pane_id = @id";
            cmd.Parameters.AddWithValue("@json", """{"sequences":{"plugin:claude":3}}""");
            cmd.Parameters.AddWithValue("@id", paneId);
            await cmd.ExecuteNonQueryAsync();
        }

        await using var store = new SqliteRuntimeSessionStore(_paths);
        var old = await store.TryLoadAsync("psr-old");
        Assert.True(old.IsOk);
        var pane = Only(old.Value!);
        Assert.True(pane.ProgramStatusStore.IsEmpty);
        Assert.Equal(0, pane.ProgramStatusStore.LastSeq);
        Assert.Equal(3, pane.AgentAuthoritySequences["plugin:claude"]);
    }

    [Fact]
    public async Task Bad_saved_records_are_dropped_and_the_load_succeeds()
    {
        var loaded = await SaveAndLoadAsync(
            Session("psr-bad", (w, t, p) => PaneWith(w, t, p, ProgramStatusStore.Empty)));
        var paneId = Only(loaded).Id.Value;
        await using (var conn = new SqliteConnection($"Data Source={_paths.DatabasePath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE panes SET agent_authority_json = @json WHERE pane_id = @id";
            cmd.Parameters.AddWithValue("@json", """
                {"program_status":{"last_seq":1,"real_report_seen":true,"records":[
                  {"id":"ok","state":"done","seq":5},
                  {"id":"bad id","state":"done","seq":2},
                  {"id":"x","state":"nonsense","seq":3},
                  {"id":"y","state":"clear","seq":4}]}}
                """);
            cmd.Parameters.AddWithValue("@id", paneId);
            await cmd.ExecuteNonQueryAsync();
        }

        await using var store = new SqliteRuntimeSessionStore(_paths);
        var load = await store.TryLoadAsync("psr-bad");
        Assert.True(load.IsOk);
        var restored = Only(load.Value!).ProgramStatusStore;
        Assert.Equal("ok", Assert.Single(restored.Records).Id);
        Assert.Equal(5, restored.LastSeq);
    }

    [Fact]
    public void Restore_caps_records_and_keeps_the_most_recently_updated()
    {
        var records = Enumerable.Range(1, ProgramStatusStore.MaxRecords + 10)
            .Select(i => new ProgramStatusRecord
            {
                Id = "r" + i,
                State = ProgramStatusState.Done,
                UpdatedSeq = i,
            });
        var restored = ProgramStatusStore.Restore(records, 0, true);
        Assert.Equal(ProgramStatusStore.MaxRecords, restored.Records.Length);
        Assert.DoesNotContain(restored.Records, r => r.Id == "r1");
        Assert.Contains(restored.Records, r => r.Id == "r" + (ProgramStatusStore.MaxRecords + 10));
        Assert.Equal(ProgramStatusStore.MaxRecords + 10, restored.LastSeq);
    }

    [Fact]
    public async Task Surviving_pane_keeps_blocked_and_gets_its_authority_again()
    {
        var store = ProgramStatusStore.Empty
            .Apply(Report(ProgramStatusState.Blocked, msg: "approve?", app: "brew", kind: ProgramStatusKind.Permission));
        var loaded = await SaveAndLoadAsync(
            Session("psr-alive", (w, t, p) => PaneWith(w, t, p, store, pid: 4242)));

        var restored = Only(SessionGraphRestorer.ReconcileDeadPids(loaded, new FixedProbe(alive: true)));

        Assert.Equal(ProgramStatusState.Blocked, Assert.Single(restored.ProgramStatusStore.Records).State);
        Assert.Equal(AgentStatus.Blocked, restored.AgentStatus);
        Assert.Equal("brew", restored.AgentKind);
        Assert.Equal("approve?", restored.AgentMessage);
        Assert.True(restored.AgentAuthority!.IsProgramStatus);
    }

    [Fact]
    public async Task Pane_whose_process_is_gone_keeps_done_and_loses_working()
    {
        var store = ProgramStatusStore.Empty
            .Apply(Report(ProgramStatusState.Working, "build", app: "brew"))
            .Apply(Report(ProgramStatusState.Done, "lint", "clean", app: "brew"))
            .Apply(Report(ProgramStatusState.Blocked, "ask"));
        var loaded = await SaveAndLoadAsync(
            Session("psr-gone", (w, t, p) => PaneWith(w, t, p, store, pid: 4242)));

        var restored = Only(SessionGraphRestorer.ReconcileDeadPids(loaded, new FixedProbe(alive: false)));

        var kept = Assert.Single(restored.ProgramStatusStore.Records);
        Assert.Equal("lint", kept.Id);
        Assert.Equal(ProgramStatusState.Done, kept.State);
        Assert.Equal(AgentStatus.Done, restored.AgentStatus);
        Assert.True(restored.AgentAuthority!.IsProgramStatus);
        Assert.Equal(store.LastSeq, restored.ProgramStatusStore.LastSeq);
    }

    [Fact]
    public async Task Pane_without_a_pid_loses_working_and_blocked_and_has_no_program_authority()
    {
        var store = ProgramStatusStore.Empty.Apply(Report(ProgramStatusState.Working, msg: "go"));
        var loaded = await SaveAndLoadAsync(
            Session("psr-nopid", (w, t, p) => PaneWith(w, t, p, store, pid: null)));

        var restored = Only(SessionGraphRestorer.ReconcileDeadPids(loaded, new FixedProbe(alive: true)));

        Assert.True(restored.ProgramStatusStore.IsEmpty);
        Assert.Null(restored.AgentAuthority);
        Assert.Equal(AgentStatus.Done, restored.AgentStatus);
    }

    [Fact]
    public async Task Report_agent_authority_is_saved_and_records_do_not_replace_it()
    {
        var authority = new PaneAgentAuthority
        {
            Source = "plugin:claude",
            Agent = "claude",
            State = AgentStatus.Blocked,
            Message = "explicit",
            ReportedAt = At,
        };
        var store = ProgramStatusStore.Empty.Apply(Report(ProgramStatusState.Done, msg: "records", app: "brew"));
        var loaded = await SaveAndLoadAsync(
            Session("psr-report", (w, t, p) => PaneWith(w, t, p, store, pid: 4242, authority: authority)));
        Assert.Equal("plugin:claude", Only(loaded).AgentAuthority!.Source);

        var restored = Only(SessionGraphRestorer.ReconcileDeadPids(loaded, new FixedProbe(alive: true)));

        Assert.Equal("plugin:claude", restored.AgentAuthority!.Source);
        Assert.Equal(AgentStatus.Blocked, restored.AgentStatus);
        Assert.Equal("explicit", restored.AgentMessage);
        Assert.Equal(ProgramStatusState.Done, Assert.Single(restored.ProgramStatusStore.Records).State);
    }
}
