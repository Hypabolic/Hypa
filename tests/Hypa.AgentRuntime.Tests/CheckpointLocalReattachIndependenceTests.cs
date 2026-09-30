using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// A4: Local P0 reattach continues via SQLite + HYJR only — no prepare/export required.
/// </summary>
[Collection("CheckpointTests")]
public class CheckpointLocalReattachIndependenceTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public CheckpointLocalReattachIndependenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-h10-reattach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose()
    {
        SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);
    }

    [Fact]
    public async Task Restart_restore_does_not_require_checkpoint_methods_or_directory()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);

        var sessionId = SessionId.New("reattach");
        var wsId = WorkspaceId.New();
        var tabId = TabId.New();
        var paneId = PaneId.New();

        var original = new SessionState
        {
            Id = sessionId,
            Name = "reattach",
            LifecycleState = SessionLifecycle.Ready,
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
                    Cwd = "/tmp",
                    Command = "bash",
                    LifecycleState = PaneLifecycle.Running,
                    Pid = 12345,
                    IsAlive = true,
                    OccupantGeneration = 1,
                },
            },
        };

        var store = new SqliteRuntimeSessionStore(_paths);
        Assert.True((await store.SaveAsync(original)).IsOk);

        // No checkpoints/ directory and no prepare call.
        Assert.False(Directory.Exists(_paths.CheckpointsDirectory));

        // Simulate restart: load + reconcile.
        var loaded = await store.TryLoadAsync("reattach");
        Assert.True(loaded.IsOk);
        Assert.NotNull(loaded.Value);

        var restored = SessionGraphRestorer.ReconcileDeadPids(
            loaded.Value!, new AlwaysDeadProbe());

        Assert.Equal(SessionLifecycle.Ready, restored.LifecycleState);
        Assert.False(SessionLifecycle.IsFrozen(restored.LifecycleState));
        Assert.True(restored.Panes.ContainsKey(paneId.Value));
        Assert.False(restored.Panes[paneId.Value].IsAlive);
        Assert.Equal(SessionLifecycle.Ready, restored.LifecycleState);

        // Still no checkpoint tree required.
        Assert.False(Directory.Exists(_paths.CheckpointsDirectory));
    }

    [Fact]
    public void SessionGraphRestorer_has_no_prepare_or_checkpoint_dependency()
    {
        var methods = typeof(SessionGraphRestorer)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(m => m.Name)
            .ToList();
        Assert.DoesNotContain(methods, m => m.Contains("Checkpoint", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(methods, m => m.Contains("Prepare", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class AlwaysDeadProbe : IProcessLivenessProbe
    {
        public bool IsAlive(int pid) => false;
    }
}
