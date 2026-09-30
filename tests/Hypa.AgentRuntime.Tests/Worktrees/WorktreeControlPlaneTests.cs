using System.Text.Json;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Worktrees;
using Hypa.AgentRuntime.Infrastructure.Git;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Xunit;

namespace Hypa.AgentRuntime.Tests.Worktrees;

public sealed class WorktreeControlPlaneTests
{
    [Fact]
    public async Task List_create_open_remove_and_reuse()
    {
        using var repo = GitRepo.Create();
        var extra = Path.Combine(repo.Worktrees, "manual");
        GitRepo.RunGit(repo.Root, "worktree", "add", "-b", "manual", extra, "HEAD");

        await using var harness = await Harness.Start(repo);
        var list = await harness.ListAsync(harness.ParentId);
        Assert.Contains(list.Worktrees, w => w.Branch == "main");
        Assert.Contains(list.Worktrees, w => w.Branch == "manual" && w.OpenWorkspaceId is null);

        var created = await harness.CreateAsync(harness.ParentId, "feature", "created");
        Assert.True(created.Created);
        Assert.False(created.AlreadyOpen);
        Assert.NotNull(created.Workspace.Worktree);
        Assert.True(created.Workspace.Worktree!.IsLinkedWorktree);
        Assert.Equal(created.Workspace.Worktree.Key, harness.Parent.Worktree!.Key);
        Assert.False(WorktreePathRules.LooksLikeFilesystemPath(created.Workspace.Worktree.Key));
        Assert.Equal("repo", created.Worktree.Label);
        Assert.Equal("created", created.Workspace.Label);

        var opened = await harness.OpenAsync(harness.ParentId, branch: "feature");
        Assert.True(opened.AlreadyOpen);
        Assert.Equal(created.Workspace.WorkspaceId, opened.Workspace.WorkspaceId);

        var listed = await harness.ListAsync(harness.ParentId);
        Assert.Contains(listed.Worktrees, w => w.Branch == "feature" && w.OpenWorkspaceId == created.Workspace.WorkspaceId);

        var removed = await harness.RemoveAsync(created.Workspace.WorkspaceId!);
        Assert.True(removed.Ok);
        Assert.Null(harness.State.GetWorkspace(new WorkspaceId(created.Workspace.WorkspaceId!)));
        Assert.True(Directory.Exists(repo.Root));
    }

    [Fact]
    public async Task Dirty_remove_refuses_until_force()
    {
        using var repo = GitRepo.Create();
        await using var harness = await Harness.Start(repo);
        var created = await harness.CreateAsync(harness.ParentId, "dirty-branch", "dirty");
        File.WriteAllText(Path.Combine(created.Worktree.Path, "dirty.txt"), "x");

        var ex = await Assert.ThrowsAsync<ControlPlaneException>(
            () => harness.RemoveAsync(created.Workspace.WorkspaceId!));
        Assert.Equal(WorktreeErrorCodes.DirtyRequiresForce, ex.ErrorCode);
        Assert.DoesNotContain(created.Worktree.Path, ex.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(created.Worktree.Path));

        var forced = await harness.RemoveAsync(created.Workspace.WorkspaceId!, force: true);
        Assert.True(forced.Ok);
        Assert.False(Directory.Exists(created.Worktree.Path));
    }

    [Fact]
    public async Task Parent_close_closes_group_and_leaves_checkouts()
    {
        using var repo = GitRepo.Create();
        await using var harness = await Harness.Start(repo, createUnrelated: true);
        var created = await harness.CreateAsync(harness.ParentId, "child-branch", "child");
        Assert.True(Directory.Exists(created.Worktree.Path));

        await harness.Dispatch(ProtocolMethods.WorkspaceClose, new WorkspaceCloseParams
        {
            WorkspaceId = harness.ParentId,
        }, ProtocolJsonContext.Default.WorkspaceCloseParams);

        Assert.Null(harness.State.GetWorkspace(new WorkspaceId(harness.ParentId)));
        Assert.Null(harness.State.GetWorkspace(new WorkspaceId(created.Workspace.WorkspaceId!)));
        Assert.NotNull(harness.State.GetWorkspace(new WorkspaceId(harness.UnrelatedId!)));
        Assert.True(Directory.Exists(repo.Root));
        Assert.True(Directory.Exists(created.Worktree.Path));
        Assert.True(harness.DisposedPanes >= 2);
    }

    [Fact]
    public async Task Last_workspace_remove_keeps_checkout()
    {
        using var repo = GitRepo.Create();
        await using var harness = await Harness.Start(repo);
        var created = await harness.CreateAsync(harness.ParentId, "only-child", "child");
        Assert.Equal(CloseWorkspaceOutcome.Closed, harness.State.CloseWorkspace(new WorkspaceId(harness.ParentId)));

        var ex = await Assert.ThrowsAsync<ControlPlaneException>(
            () => harness.RemoveAsync(created.Workspace.WorkspaceId!));
        Assert.Equal(WorktreeErrorCodes.LastWorkspace, ex.ErrorCode);
        Assert.True(Directory.Exists(created.Worktree.Path));
        Assert.NotNull(harness.State.GetWorkspace(new WorkspaceId(created.Workspace.WorkspaceId!)));
    }

    [Fact]
    public async Task Concurrent_open_reuses_one_workspace()
    {
        using var repo = GitRepo.Create();
        var extra = Path.Combine(repo.Worktrees, "manual");
        GitRepo.RunGit(repo.Root, "worktree", "add", "-b", "manual", extra, "HEAD");
        await using var harness = await Harness.Start(repo);

        var first = harness.OpenPathAsync(harness.ParentId, extra);
        var second = harness.OpenPathAsync(harness.ParentId, extra);
        var results = await Task.WhenAll(TryAsync(first), TryAsync(second));
        var ok = results.Where(r => r.Ok).Select(r => r.WorkspaceId).Distinct().ToArray();
        Assert.Single(ok);
        Assert.Equal(2, harness.State.ListWorkspaces().Count);
        foreach (var fail in results.Where(r => !r.Ok))
            Assert.Equal(WorktreeErrorCodes.OperationInProgress, fail.ErrorCode);
    }

    [Fact]
    public async Task Concurrent_open_and_remove_share_checkout_lock()
    {
        using var repo = GitRepo.Create();
        await using var harness = await Harness.Start(repo, createUnrelated: true);
        var created = await harness.CreateAsync(harness.ParentId, "race-branch", "child");
        var path = created.Worktree.Path;
        var open = harness.OpenPathAsync(harness.ParentId, path);
        var remove = harness.RemoveAsync(created.Workspace.WorkspaceId!);
        var opened = await TryAsync(open);
        ControlPlaneException? removeError = null;
        WorktreeRemoveResult? removed = null;
        try
        {
            removed = await remove;
        }
        catch (ControlPlaneException ex)
        {
            removeError = ex;
        }

        var stillOpen = harness.State.ListWorkspaces()
            .Count(w => w.Worktree is { } m && WorktreePathRules.PathsEqual(m.CheckoutPath, path));
        Assert.True(stillOpen <= 1);
        if (removed is { Ok: true })
        {
            Assert.False(Directory.Exists(path));
            Assert.True(opened.Ok || opened.ErrorCode == WorktreeErrorCodes.OperationInProgress);
        }
        else
        {
            Assert.NotNull(removeError);
            Assert.True(
                removeError!.ErrorCode is WorktreeErrorCodes.OperationInProgress
                    or WorktreeErrorCodes.WorkspaceNotFound
                    or WorktreeErrorCodes.NotLinkedWorktree);
            Assert.True(Directory.Exists(path));
        }
    }

    [Fact]
    public async Task Nested_cwd_reuses_open_checkout()
    {
        using var repo = GitRepo.Create();
        await using var harness = await Harness.Start(repo);
        var created = await harness.CreateAsync(harness.ParentId, "nested-branch", "child");
        var nested = Path.Combine(created.Worktree.Path, "src");
        Directory.CreateDirectory(nested);
        var extra = await harness.Dispatch(
            ProtocolMethods.WorkspaceCreate,
            new WorkspaceCreateParams { Cwd = nested, Label = "nested", CreatePane = true },
            ProtocolJsonContext.Default.WorkspaceCreateParams);
        var nestedId = extra.GetProperty("workspace_id").GetString();
        Assert.NotNull(nestedId);

        var opened = await harness.OpenPathAsync(harness.ParentId, created.Worktree.Path);
        Assert.True(opened.AlreadyOpen);
        Assert.Equal(created.Workspace.WorkspaceId, opened.Workspace.WorkspaceId);
    }

    [Fact]
    public async Task Concurrent_cwd_create_reuses_one_parent()
    {
        using var repo = GitRepo.Create();
        await using var harness = await Harness.StartEmpty(repo);

        var first = harness.CreateFromCwdAsync(repo.Root, "race-a", "child-a");
        var second = harness.CreateFromCwdAsync(repo.Root, "race-b", "child-b");
        var created = await Task.WhenAll(first, second);

        Assert.All(created, result => Assert.True(result.Workspace.Worktree!.IsLinkedWorktree));
        var workspaces = harness.State.ListWorkspaces();
        Assert.Equal(3, workspaces.Count);
        var parents = workspaces.Where(w => w.Worktree is { IsLinkedWorktree: false }).ToArray();
        Assert.Single(parents);
        Assert.True(WorktreePathRules.PathsEqual(parents[0].Worktree!.CheckoutPath, repo.Root));
        Assert.Equal(2, workspaces.Count(w => w.Worktree is { IsLinkedWorktree: true }));
        Assert.Single(created.Select(result => result.Workspace.Worktree!.Key).Distinct());
    }

    [Fact]
    public async Task Create_from_cwd_opens_parent_workspace()
    {
        using var repo = GitRepo.Create();
        await using var harness = await Harness.StartEmpty(repo);
        var created = await harness.CreateFromCwdAsync(repo.Root, "from-cwd", "child");
        Assert.True(created.Created);
        Assert.True(created.Workspace.Worktree!.IsLinkedWorktree);
        Assert.Equal(2, harness.State.ListWorkspaces().Count);
        Assert.Contains(harness.State.ListWorkspaces(), w => w.Worktree is { IsLinkedWorktree: false });
    }

    [Fact]
    public async Task Create_reuses_already_open_checkout()
    {
        using var repo = GitRepo.Create();
        await using var harness = await Harness.Start(repo);
        var created = await harness.CreateAsync(harness.ParentId, "reuse-branch", "child");
        var again = await harness.Dispatch(
            ProtocolMethods.WorktreeCreate,
            new WorktreeCreateParams
            {
                WorkspaceId = harness.ParentId,
                Branch = "reuse-branch",
                Path = created.Worktree.Path,
                Focus = true,
            },
            ProtocolJsonContext.Default.WorktreeCreateParams);
        var reused = again.Deserialize(ProtocolJsonContext.Default.WorktreeCreateResult)!;
        Assert.True(reused.AlreadyOpen);
        Assert.Equal(created.Workspace.WorkspaceId, reused.Workspace.WorkspaceId);
    }

    [Fact]
    public async Task Open_by_branch_skips_prunable_duplicate()
    {
        using var repo = GitRepo.Create();
        var live = Path.Combine(repo.Worktrees, "live");
        GitRepo.RunGit(repo.Root, "worktree", "add", "-b", "shared", live, "HEAD");
        await using var harness = await Harness.Start(repo, git: new PrunableDuplicateGit(live, "shared"));
        var opened = await harness.OpenAsync(harness.ParentId, branch: "shared");
        Assert.False(opened.AlreadyOpen);
        Assert.True(WorktreePathRules.PathsEqual(live, opened.Worktree.Path));
    }

    [Fact]
    public async Task Linked_source_cannot_create()
    {
        using var repo = GitRepo.Create();
        await using var harness = await Harness.Start(repo);
        var created = await harness.CreateAsync(harness.ParentId, "from-parent", "child");
        var ex = await Assert.ThrowsAsync<ControlPlaneException>(
            () => harness.CreateAsync(created.Workspace.WorkspaceId!, "other", "nope"));
        Assert.Equal(WorktreeErrorCodes.LinkedWorktreeSource, ex.ErrorCode);
    }

    [Fact]
    public async Task Fixtures_round_trip()
    {
        foreach (var method in ProtocolMethods.Worktrees)
        {
            var reqJson = FixtureCatalog.Load(FixtureCatalog.MethodRequestPath(method));
            var resJson = FixtureCatalog.Load(FixtureCatalog.MethodResponsePath(method));
            var req = JsonSerializer.Deserialize(reqJson, ProtocolJsonContext.Default.RpcRequest);
            var res = JsonSerializer.Deserialize(resJson, ProtocolJsonContext.Default.RpcResponse);
            Assert.Equal(method, req!.Method);
            Assert.NotNull(res!.Result);
            switch (method)
            {
                case ProtocolMethods.WorktreeList:
                    Assert.NotNull(JsonSerializer.Deserialize(req.Params!.Value, ProtocolJsonContext.Default.WorktreeListParams));
                    var list = JsonSerializer.Deserialize(res.Result.Value, ProtocolJsonContext.Default.WorktreeListResult);
                    Assert.False(WorktreePathRules.LooksLikeFilesystemPath(list!.Source.RepoKey));
                    break;
                case ProtocolMethods.WorktreeCreate:
                    Assert.NotNull(JsonSerializer.Deserialize(req.Params!.Value, ProtocolJsonContext.Default.WorktreeCreateParams));
                    var created = JsonSerializer.Deserialize(res.Result.Value, ProtocolJsonContext.Default.WorktreeCreateResult);
                    Assert.False(WorktreePathRules.LooksLikeFilesystemPath(created!.Workspace.Worktree!.Key));
                    Assert.Equal("repo", created.Worktree.Label);
                    break;
                case ProtocolMethods.WorktreeOpen:
                    Assert.NotNull(JsonSerializer.Deserialize(req.Params!.Value, ProtocolJsonContext.Default.WorktreeOpenParams));
                    var opened = JsonSerializer.Deserialize(res.Result.Value, ProtocolJsonContext.Default.WorktreeOpenResult);
                    Assert.False(WorktreePathRules.LooksLikeFilesystemPath(opened!.Workspace.Worktree!.Key));
                    Assert.Equal("repo", opened.Worktree.Label);
                    break;
                case ProtocolMethods.WorktreeRemove:
                    Assert.NotNull(JsonSerializer.Deserialize(req.Params!.Value, ProtocolJsonContext.Default.WorktreeRemoveParams));
                    Assert.NotNull(JsonSerializer.Deserialize(res.Result.Value, ProtocolJsonContext.Default.WorktreeRemoveResult));
                    break;
            }
        }

        foreach (var type in ProtocolEventTypes.Worktrees)
        {
            var json = FixtureCatalog.Load(FixtureCatalog.EventPath(type));
            Assert.Contains(type, json, StringComparison.Ordinal);
            Assert.Contains("[checkout]", json, StringComparison.Ordinal);
            Assert.DoesNotContain("/repo", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"branch\":null", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"already_open\":null", json, StringComparison.Ordinal);
        }

        var createdPayload = RuntimeEventPayloadJson.WriteWorktreeLifecycle(
            ProtocolEventTypes.WorktreeCreated, "w_2", "worktree/calm-river-0001", "repo", alreadyOpen: false);
        Assert.DoesNotContain("already_open", createdPayload, StringComparison.Ordinal);
        var removedPayload = RuntimeEventPayloadJson.WriteWorktreeLifecycle(
            ProtocolEventTypes.WorktreeRemoved, "w_2", branch: null, "repo", alreadyOpen: false);
        Assert.DoesNotContain("branch", removedPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("already_open", removedPayload, StringComparison.Ordinal);
    }

    private static async Task<(bool Ok, string? WorkspaceId, string? ErrorCode)> TryAsync(Task<WorktreeOpenResult> task)
    {
        try
        {
            var result = await task.ConfigureAwait(false);
            return (true, result.Workspace.WorkspaceId, null);
        }
        catch (ControlPlaneException ex)
        {
            return (false, null, ex.ErrorCode);
        }
    }

    internal sealed class Harness : IAsyncDisposable
    {
        private readonly StubPaneFactory _panes;

        private Harness(
            AppState state,
            ControlPlaneService service,
            StubPaneFactory panes,
            string parentId,
            string? unrelatedId)
        {
            State = state;
            Service = service;
            _panes = panes;
            ParentId = parentId;
            UnrelatedId = unrelatedId;
        }

        public AppState State { get; }
        public ControlPlaneService Service { get; }
        public string ParentId { get; }
        public string? UnrelatedId { get; }
        public int DisposedPanes => _panes.Disposed;
        public WorkspaceState Parent => State.GetWorkspace(new WorkspaceId(ParentId))!;

        public static Task<Harness> Start(
            GitRepo repo,
            bool createUnrelated = false,
            IGitWorktreePort? git = null) =>
            StartCore(repo, createParent: true, createUnrelated, git);

        public static Task<Harness> StartEmpty(GitRepo repo) =>
            StartCore(repo, createParent: false, createUnrelated: false, git: null);

        private static async Task<Harness> StartCore(
            GitRepo repo,
            bool createParent,
            bool createUnrelated,
            IGitWorktreePort? git)
        {
            var state = new AppState(SessionId.New("worktrees"));
            state.UpdateSession(s => s with
            {
                LifecycleState = SessionLifecycle.Ready,
                Placement = "local",
            });
            var config = AttachClientConfig.Default with
            {
                Worktrees = new AttachWorktreesConfig { Directory = repo.Worktrees },
            };
            var panes = new StubPaneFactory();
            var service = new ControlPlaneService(
                state,
                panes,
                new PaneIntelligencePipeline(),
                new HeuristicAgentDetector(),
                attachConfig: config,
                gitWorktrees: git);
            var parentId = "";
            if (createParent)
            {
                var created = await service.DispatchAsync(
                    ProtocolMethods.WorkspaceCreate,
                    JsonSerializer.SerializeToElement(
                        new WorkspaceCreateParams { Cwd = repo.Root, Label = "parent", CreatePane = true },
                        ProtocolJsonContext.Default.WorkspaceCreateParams),
                    CancellationToken.None);
                parentId = created.GetProperty("workspace_id").GetString()!;
            }

            string? unrelatedId = null;
            if (createUnrelated)
            {
                var other = Path.Combine(repo.Temp, "other");
                Directory.CreateDirectory(other);
                var extra = await service.DispatchAsync(
                    ProtocolMethods.WorkspaceCreate,
                    JsonSerializer.SerializeToElement(
                        new WorkspaceCreateParams { Cwd = other, Label = "other", CreatePane = true },
                        ProtocolJsonContext.Default.WorkspaceCreateParams),
                    CancellationToken.None);
                unrelatedId = extra.GetProperty("workspace_id").GetString();
            }

            return new Harness(state, service, panes, parentId, unrelatedId);
        }

        public Task<JsonElement> Dispatch<T>(string method, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
            where T : class =>
            Service.DispatchAsync(method, JsonSerializer.SerializeToElement(value, info), CancellationToken.None);

        public async Task<WorktreeListResult> ListAsync(string workspaceId)
        {
            var json = await Dispatch(
                ProtocolMethods.WorktreeList,
                new WorktreeListParams { WorkspaceId = workspaceId },
                ProtocolJsonContext.Default.WorktreeListParams);
            return json.Deserialize(ProtocolJsonContext.Default.WorktreeListResult)!;
        }

        public async Task<WorktreeCreateResult> CreateAsync(string workspaceId, string branch, string label)
        {
            var json = await Dispatch(
                ProtocolMethods.WorktreeCreate,
                new WorktreeCreateParams
                {
                    WorkspaceId = workspaceId,
                    Branch = branch,
                    Base = "HEAD",
                    Label = label,
                    Focus = true,
                },
                ProtocolJsonContext.Default.WorktreeCreateParams);
            return json.Deserialize(ProtocolJsonContext.Default.WorktreeCreateResult)!;
        }

        public async Task<WorktreeOpenResult> OpenAsync(string workspaceId, string branch)
        {
            var json = await Dispatch(
                ProtocolMethods.WorktreeOpen,
                new WorktreeOpenParams
                {
                    WorkspaceId = workspaceId,
                    Branch = branch,
                    Focus = true,
                },
                ProtocolJsonContext.Default.WorktreeOpenParams);
            return json.Deserialize(ProtocolJsonContext.Default.WorktreeOpenResult)!;
        }

        public async Task<WorktreeOpenResult> OpenPathAsync(string workspaceId, string path)
        {
            var json = await Dispatch(
                ProtocolMethods.WorktreeOpen,
                new WorktreeOpenParams
                {
                    WorkspaceId = workspaceId,
                    Path = path,
                    Focus = true,
                },
                ProtocolJsonContext.Default.WorktreeOpenParams);
            return json.Deserialize(ProtocolJsonContext.Default.WorktreeOpenResult)!;
        }

        public async Task<WorktreeCreateResult> CreateFromCwdAsync(string cwd, string branch, string label)
        {
            var json = await Dispatch(
                ProtocolMethods.WorktreeCreate,
                new WorktreeCreateParams
                {
                    Cwd = cwd,
                    Branch = branch,
                    Base = "HEAD",
                    Label = label,
                    Focus = true,
                },
                ProtocolJsonContext.Default.WorktreeCreateParams);
            return json.Deserialize(ProtocolJsonContext.Default.WorktreeCreateResult)!;
        }

        public async Task<WorktreeRemoveResult> RemoveAsync(string workspaceId, bool force = false)
        {
            var json = await Dispatch(
                ProtocolMethods.WorktreeRemove,
                new WorktreeRemoveParams { WorkspaceId = workspaceId, Force = force },
                ProtocolJsonContext.Default.WorktreeRemoveParams);
            return json.Deserialize(ProtocolJsonContext.Default.WorktreeRemoveResult)!;
        }

        public async ValueTask DisposeAsync() =>
            await Service.ShutdownAsync(CancellationToken.None);
    }

    private sealed class StubPaneFactory : IPaneRuntimeFactory
    {
        public int Disposed;

        public IPaneRuntime Create(PaneSpawnOptions options) => new StubPane(options.Id, this);
    }

    private sealed class StubPane(PaneId id, StubPaneFactory factory) : IPaneRuntime
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
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref factory.Disposed);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PrunableDuplicateGit(string livePath, string branch) : IGitWorktreePort
    {
        private readonly ProcessGitWorktreeAdapter _inner = new();

        public Result<IReadOnlyList<ExistingWorktree>, WorktreeError> List(string repoRoot, bool trustRepository)
        {
            var listed = _inner.List(repoRoot, trustRepository);
            if (!listed.IsOk)
                return listed;
            var extra = new ExistingWorktree
            {
                Path = livePath + "-prunable",
                Branch = branch,
                IsPrunable = true,
            };
            return Result<IReadOnlyList<ExistingWorktree>, WorktreeError>.Ok([.. listed.Value, extra]);
        }

        public Result<GitSpaceMetadata, WorktreeError> ProbeSpace(string cwd, bool trustRepository) =>
            _inner.ProbeSpace(cwd, trustRepository);

        public Result<bool, WorktreeError> LocalBranchExists(string repoRoot, string branchName, bool trustRepository) =>
            _inner.LocalBranchExists(repoRoot, branchName, trustRepository);

        public Result<bool, WorktreeError> CheckoutHasDirtyFiles(string checkout, bool trustRepository) =>
            _inner.CheckoutHasDirtyFiles(checkout, trustRepository);

        public Result<RuntimeUnit, WorktreeError> Add(
            string repoRoot, string path, string branchName, string @base, bool trustRepository) =>
            _inner.Add(repoRoot, path, branchName, @base, trustRepository);

        public Result<RuntimeUnit, WorktreeError> Remove(
            string repoRoot, string path, bool force, bool trustRepository) =>
            _inner.Remove(repoRoot, path, force, trustRepository);

        public Result<string?, WorktreeError> CommonWorktreesDir(string repoRoot, bool trustRepository) =>
            _inner.CommonWorktreesDir(repoRoot, trustRepository);
    }
}
