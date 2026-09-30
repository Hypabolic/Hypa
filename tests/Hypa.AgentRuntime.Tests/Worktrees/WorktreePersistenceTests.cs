using System.Text.Json;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Worktrees;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.ControlPlane;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hypa.AgentRuntime.Tests.Worktrees;

public sealed class WorktreePersistenceTests : IDisposable
{
    private readonly string _dir;
    private readonly RuntimeStatePaths _paths;

    public WorktreePersistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hypa-wt-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _paths = new RuntimeStatePaths { StateDirectory = _dir };
    }

    public void Dispose()
    {
        SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);
    }

    [Fact]
    public async Task Store_restores_membership_and_parent_close_still_groups()
    {
        using var repo = GitRepo.Create();
        await using var harness = await WorktreeControlPlaneTests.Harness.Start(repo, createUnrelated: true);
        var created = await harness.CreateAsync(harness.ParentId, "persist-child", "child");
        var parentKey = harness.Parent.Worktree!.Key;
        Assert.False(WorktreePathRules.LooksLikeFilesystemPath(parentKey));

        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        var migrated = await migrator.MigrateAsync();
        Assert.True(migrated.IsOk, migrated.IsOk ? null : migrated.Error.Message);
        await using var store = new SqliteRuntimeSessionStore(_paths);
        var snapshot = harness.State.Snapshot();
        var save = await store.SaveAsync(snapshot);
        Assert.True(save.IsOk, save.IsOk ? null : save.Error.Message);

        var load = await store.TryLoadAsync(snapshot.Name);
        Assert.True(load.IsOk);
        Assert.NotNull(load.Value);
        var parent = load.Value!.Workspaces[harness.ParentId];
        var child = load.Value.Workspaces[created.Workspace.WorkspaceId!];
        Assert.NotNull(parent.Worktree);
        Assert.NotNull(child.Worktree);
        Assert.Equal(parentKey, parent.Worktree!.Key);
        Assert.Equal(parentKey, child.Worktree!.Key);
        Assert.False(parent.Worktree.IsLinkedWorktree);
        Assert.True(child.Worktree.IsLinkedWorktree);
        Assert.True(WorktreePathRules.PathsEqual(created.Worktree.Path, child.Worktree.CheckoutPath));

        var restored = new AppState(SessionId.New("restored-worktrees"));
        restored.Replace(load.Value);
        restored.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var config = AttachClientConfig.Default with
        {
            Worktrees = new AttachWorktreesConfig { Directory = repo.Worktrees },
        };
        var service = new ControlPlaneService(
            restored,
            new RestorePaneFactory(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            attachConfig: config);
        try
        {
            await service.DispatchAsync(
                ProtocolMethods.WorkspaceClose,
                JsonSerializer.SerializeToElement(
                    new WorkspaceCloseParams { WorkspaceId = harness.ParentId },
                    ProtocolJsonContext.Default.WorkspaceCloseParams),
                CancellationToken.None);

            Assert.Null(restored.GetWorkspace(new WorkspaceId(harness.ParentId)));
            Assert.Null(restored.GetWorkspace(new WorkspaceId(created.Workspace.WorkspaceId!)));
            Assert.NotNull(restored.GetWorkspace(new WorkspaceId(harness.UnrelatedId!)));
            Assert.True(Directory.Exists(repo.Root));
            Assert.True(Directory.Exists(created.Worktree.Path));
        }
        finally
        {
            await service.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Corrupt_worktree_json_fails_closed()
    {
        var migrator = new SqliteRuntimeSchemaMigrator(_paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        await using var store = new SqliteRuntimeSessionStore(_paths);

        var sessionId = SessionId.New("corrupt-wt");
        var wsId = WorkspaceId.New();
        var original = new SessionState
        {
            Id = sessionId,
            Name = "corrupt-wt",
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
            Workspaces = new Dictionary<string, WorkspaceState>
            {
                [wsId.Value] = new WorkspaceState
                {
                    Id = wsId,
                    Label = "ws",
                    Cwd = "/workspace",
                    Worktree = new WorktreeSpaceMembership
                    {
                        Key = WorktreePathRules.OpaqueRepoKey("/repo/.git"),
                        Label = "repo",
                        RepoRoot = "/repo",
                        CheckoutPath = "/repo",
                        IsLinkedWorktree = false,
                    },
                },
            },
        };
        Assert.True((await store.SaveAsync(original)).IsOk);

        await using var conn = new SqliteConnection("Data Source=" + _paths.DatabasePath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE workspaces SET worktree_json = '{not-json' WHERE workspace_id = @id";
        cmd.Parameters.AddWithValue("@id", wsId.Value);
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync());

        var load = await store.TryLoadAsync("corrupt-wt");
        Assert.False(load.IsOk);
        Assert.Contains("worktree_json", load.Error.Message, StringComparison.Ordinal);
    }

    private sealed class RestorePaneFactory : IPaneRuntimeFactory
    {
        public IPaneRuntime Create(PaneSpawnOptions options) => new RestoredPane(options.Id);
    }

    private sealed class RestoredPane(PaneId id) : IPaneRuntime
    {
        public PaneId Id { get; } = id;
        public bool IsAlive => true;
        public int? ExitCode => null;
        public int? Pid => 1;
#pragma warning disable CS0067
        public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
        public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
        public event Action<IPaneRuntime, int>? Exited;
#pragma warning restore CS0067
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask WriteTextAsync(string text, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct) => ValueTask.CompletedTask;
        public string ReadVisibleText() => "";
        public string ReadRecentText(int maxLines) => "";
        public string ReadRecentUnwrappedText(int maxLines) => "";
        public string ReadDetectionText() => "";
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
