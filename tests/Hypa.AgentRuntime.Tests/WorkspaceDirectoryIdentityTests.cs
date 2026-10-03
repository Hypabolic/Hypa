using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.ControlPlane;
using Hypa.Terminal.Pty;
using Hypa.Terminal.Vt;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class WorkspaceDirectoryIdentityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hypa-directory-" + Guid.NewGuid().ToString("N"));

    public WorkspaceDirectoryIdentityTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => SqliteTestCleanup.ReleaseAndDelete(_directory, Path.Combine(_directory, "hypa.db"));

    [Fact]
    public void Different_repositories_and_focus_do_not_change_identity_source()
    {
        var state = new AppState(SessionId.New("directory-source"));
        var ws = state.CreateWorkspace("/seed");
        var first = AddPane(state, ws, ws.TabIds[0], "/repo-a");
        var second = AddPane(state, ws, ws.TabIds[0], "/repo-b");
        var tab = state.CreateTab(ws.Id, focus: true);
        var third = AddPane(state, ws, tab.Id, "/repo-c");
        state.FocusPane(second.Id);
        Assert.Equal(first.Id, Source(state, ws)!.Id);
        state.FocusPane(third.Id);
        Assert.Equal(first.Id, Source(state, ws)!.Id);
        state.UpdatePane(first.Id, pane => pane with { Cwd = "/repo-d/subdirectory" });
        Assert.Equal("/repo-d/subdirectory", Source(state, ws)!.Cwd);
        Assert.Equal("/seed", state.GetWorkspace(ws.Id)!.Cwd);
    }

    [Fact]
    public void Root_survives_layout_reordering_and_promoted_root_survives_new_splits()
    {
        var state = new AppState(SessionId.New("directory-root"));
        var ws = state.CreateWorkspace("/seed");
        var root = AddPane(state, ws, ws.TabIds[0], "/root");
        var second = AddPane(state, ws, ws.TabIds[0], "/second");
        ReverseLayout(state, ws.TabIds[0], root, second);
        Assert.Equal(root.Id, Source(state, ws)!.Id);
        state.RemovePane(root.Id);
        Assert.Equal(second.Id, Source(state, ws)!.Id);
        var newPane = AddPane(state, ws, ws.TabIds[0], "/new");
        ReverseLayout(state, ws.TabIds[0], second, newPane);
        Assert.Equal(second.Id, Source(state, ws)!.Id);
    }

    [Fact]
    public void Moving_root_promotes_source_and_closing_first_tab_uses_next_tab()
    {
        var state = new AppState(SessionId.New("directory-move"));
        var ws = state.CreateWorkspace("/seed");
        var root = AddPane(state, ws, ws.TabIds[0], "/root");
        var second = AddPane(state, ws, ws.TabIds[0], "/second");
        var next = state.CreateTab(ws.Id);
        var nextRoot = AddPane(state, ws, next.Id, "/next");
        state.MovePaneToTab(root.Id, next.Id);
        Assert.Equal(second.Id, Source(state, ws)!.Id);
        Assert.Equal(nextRoot.Id, state.GetTab(next.Id)!.IdentityPaneId);
        state.CloseTab(ws.TabIds[0]);
        Assert.Equal(nextRoot.Id, Source(state, ws)!.Id);
    }

    [Fact]
    public void Hidden_panes_do_not_supply_identity()
    {
        var state = new AppState(SessionId.New("directory-hidden"));
        var ws = state.CreateWorkspace("/seed");
        var hidden = new PaneState
        {
            Id = PaneId.New(),
            TabId = ws.TabIds[0],
            WorkspaceId = ws.Id,
            Cwd = "/hidden",
            Placement = PanePlacement.Hidden
        };
        state.RegisterPane(hidden);
        Assert.Null(Source(state, ws));
        var root = AddPane(state, ws, ws.TabIds[0], "/root");
        Assert.Equal(root.Id, Source(state, ws)!.Id);
        var other = AddPane(state, ws, ws.TabIds[0], "/other");
        Assert.True(state.TryHideTiled(root.Id).IsOk);
        Assert.Equal(other.Id, Source(state, ws)!.Id);
    }

    [Fact]
    public async Task Identity_and_explicit_label_ownership_survive_sqlite_reload()
    {
        var paths = new RuntimeStatePaths { StateDirectory = _directory };
        Assert.True((await new SqliteRuntimeSchemaMigrator(paths).MigrateAsync()).IsOk);
        await using var store = new SqliteRuntimeSessionStore(paths);
        var state = new AppState(SessionId.New("directory-persistence"));
        var ws = state.CreateWorkspace(_directory, "My project");
        var root = AddPane(state, ws, ws.TabIds[0], _directory);
        var second = AddPane(state, ws, ws.TabIds[0], _directory);
        ReverseLayout(state, ws.TabIds[0], root, second);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
        var loaded = await store.TryLoadAsync(state.Snapshot().Name);
        Assert.True(loaded.IsOk);
        var restored = new AppState(state.SessionId);
        restored.Load(loaded.Value!);
        Assert.Equal(root.Id, Source(restored, ws)!.Id);
        Assert.True(restored.GetWorkspace(ws.Id)!.CustomLabel);
        Assert.Equal("My project", restored.GetWorkspace(ws.Id)!.Label);
        var auto = restored.CreateWorkspace(_directory);
        Assert.False(auto.CustomLabel);
        Assert.True(restored.RenameWorkspace(auto.Id, "Named")!.CustomLabel);
    }

    [Fact]
    public async Task Legacy_labels_are_inferred_without_overwriting_user_names()
    {
        var paths = new RuntimeStatePaths { StateDirectory = _directory };
        Assert.True((await new SqliteRuntimeSchemaMigrator(paths).MigrateAsync()).IsOk);
        await using var store = new SqliteRuntimeSessionStore(paths);
        var state = new AppState(SessionId.New("directory-legacy"));
        var auto = state.CreateWorkspace(_directory);
        var named = state.CreateWorkspace(_directory, "User label");
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
        await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE workspaces SET custom_label = NULL";
            await command.ExecuteNonQueryAsync();
        }
        var loaded = await store.TryLoadAsync(state.Snapshot().Name);
        Assert.True(loaded.IsOk);
        Assert.False(loaded.Value!.Workspaces[auto.Id.Value].CustomLabel);
        Assert.True(loaded.Value.Workspaces[named.Id.Value].CustomLabel);
        Assert.Equal("User label", loaded.Value.Workspaces[named.Id.Value].Label);
    }

    [Theory]
    [InlineData("\a")]
    [InlineData("\u001b\\")]
    public void Fragmented_osc7_retains_decoded_path_and_rejects_invalid_reports(string terminator)
    {
        var path = Path.Combine(_directory, "space ü");
        Directory.CreateDirectory(path);
        var tracker = new AgentOscStateTracker();
        var bytes = Encoding.UTF8.GetBytes("\u001b]7;" + new Uri(path).AbsoluteUri + terminator);
        foreach (var b in bytes)
            tracker.Observe(new[] { b });
        Assert.Equal(path, tracker.WorkingDirectory);
        foreach (var report in new[] { "file://foreign.invalid" + _directory,
            "file:///missing-hypa-directory", "file://" + _directory + "%XX",
            "file://" + _directory + "?query", "file://" + _directory + "%00", "https://localhost/" })
        {
            tracker.Observe(Encoding.UTF8.GetBytes("\u001b]7;" + report + terminator));
            Assert.Equal(path, tracker.WorkingDirectory);
        }
        tracker.ClearRetained();
        Assert.Equal(path, tracker.WorkingDirectory);
    }

    [Fact]
    public async Task Process_directory_probe_observes_shell_cd_without_osc()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            return;
        var start = new ProcessStartInfo("/bin/sh") { RedirectStandardInput = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-c", "cd \"$1\"; printf 'ready\\n'; read answer", "sh", _directory })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        try
        {
            Assert.Equal("ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            var observed = new UnixPaneProcessInfoProbe().TryGetWorkingDirectory(process.Id);
            Assert.NotNull(observed);
            // macOS /var is a symlink; compare the actual directory identity.
            Assert.Equal(Path.GetFileName(_directory), Path.GetFileName(observed));
        }
        finally
        {
            await process.StandardInput.WriteLineAsync("exit");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Host_navigation_updates_path_repo_branch_and_preserves_custom_name()
    {
        var first = Path.Combine(_directory, "repo-one");
        var second = Path.Combine(_directory, "repo-two");
        await InitRepo(first, "main");
        await InitRepo(second, "other");
        var nested = Path.Combine(first, "nested");
        Directory.CreateDirectory(nested);
        var state = new AppState(SessionId.New("directory-host"));
        var factory = TestPaneFactories.Scripted();
        var intel = new PaneIntelligencePipeline();
        var probe = new DirectoryProbe { Cwd = nested };
        var cp = new ControlPlaneService(state, factory, intel, new HeuristicAgentDetector(), processInfoProbe: probe);
        try
        {
            await cp.DispatchAsync("workspace.create", JsonSerializer.SerializeToElement(new { cwd = first, command = "sh" }), CancellationToken.None);
            var ws = Assert.Single(state.ListWorkspaces());
            var result = await AwaitWorkspace(cp, "repo-one", "main");
            Assert.Equal(nested, result.GetProperty("resolved_cwd").GetString());
            Assert.Equal(first, result.GetProperty("cwd").GetString());
            probe.Cwd = second;
            result = await AwaitWorkspace(cp, "repo-two", "other");
            Assert.Equal(second, result.GetProperty("resolved_cwd").GetString());
            state.RenameWorkspace(ws.Id, "Custom name");
            probe.Cwd = _directory;
            await cp.RefreshWorkspaceDirectoriesForTestsAsync();
            result = await GetWorkspace(cp);
            Assert.Equal("Custom name", result.GetProperty("label").GetString());
            Assert.Equal("", result.GetProperty("branch").GetString());
            Assert.Equal("", result.GetProperty("repository_name").GetString());
            state.UpdateSession(session => session with { LifecycleState = SessionLifecycle.FrozenReadOnly });
            probe.Cwd = first;
            await cp.RefreshWorkspaceDirectoriesForTestsAsync();
            Assert.Equal(_directory, Assert.Single(state.ListPanes()).Cwd);
            state.UpdateSession(session => session with { LifecycleState = SessionLifecycle.Ready });
            // Replace the generation while a directory observation is in progress.
            probe.BeforeRead = () => state.UpdatePane(factory.Created[0].Id,
                pane => pane with { OccupantGeneration = pane.OccupantGeneration + 1, Cwd = second });
            await cp.RefreshWorkspaceDirectoriesForTestsAsync();
            Assert.Equal(second, Assert.Single(state.ListPanes()).Cwd);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Restored_paneless_workspace_tracks_git_metadata_on_the_timer()
    {
        var repo = Path.Combine(_directory, "restored");
        await InitRepo(repo, "main");
        var state = new AppState(SessionId.New("directory-restore"));
        state.CreateWorkspace(repo);
        var cp = new ControlPlaneService(state, TestPaneFactories.Stub(), new PaneIntelligencePipeline(), new HeuristicAgentDetector());
        try
        {
            await cp.RestoreSpawnAsync(CancellationToken.None);
            var deadline = Stopwatch.StartNew();
            JsonElement result;
            do
            {
                result = await GetWorkspace(cp);
                if (result.GetProperty("branch").GetString() == "main")
                    break;
                await Task.Delay(50);
            } while (deadline.Elapsed < TimeSpan.FromSeconds(10));
            Assert.Equal("main", result.GetProperty("branch").GetString());
            Assert.Equal("restored", result.GetProperty("repository_name").GetString());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private static PaneState AddPane(AppState state, WorkspaceState ws, TabId tab, string cwd) =>
        state.RegisterPane(new PaneState { Id = PaneId.New(), WorkspaceId = ws.Id, TabId = tab, Cwd = cwd });

    private static PaneState? Source(AppState state, WorkspaceState ws) =>
        WorkspaceDirectoryIdentity.Source(state.Snapshot(), state.GetWorkspace(ws.Id)!);

    private static void ReverseLayout(AppState state, TabId tab, PaneState first, PaneState second) =>
        state.UpdateTab(tab, t => t with
        {
            LayoutRoot = new LayoutSplitNode
            {
                Direction = LayoutNode.DirectionRight,
                Ratio = 0.5,
                First = LayoutTreeOperations.FromPane(second),
                Second = LayoutTreeOperations.FromPane(first),
            },
            PaneIds = [second.Id, first.Id]
        });

    private static async Task InitRepo(string path, string branch)
    {
        Directory.CreateDirectory(path);
        var start = new ProcessStartInfo("git") { WorkingDirectory = path, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "init", "-b", branch })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, process.ExitCode);
    }

    private static async Task<JsonElement> GetWorkspace(ControlPlaneService cp) =>
        (await cp.DispatchAsync("workspace.list", JsonSerializer.SerializeToElement(new { }), CancellationToken.None))
            .EnumerateArray().Single().Clone();

    private static async Task<JsonElement> AwaitWorkspace(ControlPlaneService cp, string label, string branch)
    {
        var deadline = Stopwatch.StartNew();
        JsonElement result;
        do
        {
            await cp.RefreshWorkspaceDirectoriesForTestsAsync();
            result = await GetWorkspace(cp);
            if (result.GetProperty("label").GetString() == label && result.GetProperty("branch").GetString() == branch)
                return result;
            await Task.Delay(20);
        } while (deadline.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(label, result.GetProperty("label").GetString());
        Assert.Equal(branch, result.GetProperty("branch").GetString());
        return result;
    }

    private sealed class DirectoryProbe : IPaneProcessInfoProbe
    {
        public string? Cwd { get; set; }
        public Action? BeforeRead { get; set; }
        public string? TryGetWorkingDirectory(int shellPid)
        {
            var callback = BeforeRead;
            BeforeRead = null;
            callback?.Invoke();
            return Cwd;
        }
        public int? TryGetForegroundGroup(int shellPid) => null;
        public PaneForegroundInfo? TryGetForegroundInfo(int shellPid) => null;
    }
}
