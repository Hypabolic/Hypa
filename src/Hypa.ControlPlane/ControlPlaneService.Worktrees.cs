using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.Worktrees;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.ControlPlane;

public sealed partial class ControlPlaneService
{
    internal Task<JsonElement> HandleWorktreeListAsync(WorktreeListParams p, CancellationToken ct) =>
        Task.FromResult(OkTyped(WorktreeList(p), ProtocolJsonContext.Default.WorktreeListResult));

    internal async Task<JsonElement> HandleWorktreeCreateAsync(
        WorktreeCreateParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        OkTyped(await WorktreeCreateAsync(p, connection, ct).ConfigureAwait(false), ProtocolJsonContext.Default.WorktreeCreateResult);

    internal async Task<JsonElement> HandleWorktreeOpenAsync(
        WorktreeOpenParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        OkTyped(await WorktreeOpenAsync(p, connection, ct).ConfigureAwait(false), ProtocolJsonContext.Default.WorktreeOpenResult);

    internal async Task<JsonElement> HandleWorktreeRemoveAsync(
        WorktreeRemoveParams p,
        IClientConnection? connection,
        CancellationToken ct) =>
        OkTyped(await WorktreeRemoveAsync(p, connection, ct).ConfigureAwait(false), ProtocolJsonContext.Default.WorktreeRemoveResult);

    private WorktreeListResult WorktreeList(WorktreeListParams p)
    {
        var source = ResolveWorktreeListSource(p.WorkspaceId, p.Cwd, p.TrustRepository);
        var listed = _gitWorktrees.List(source.RepoRoot, p.TrustRepository);
        if (!listed.IsOk)
            throw FailWorktree(ProtocolErrorCodes.InvalidState, listed.Error.Code, listed.Error.Message);
        var infos = listed.Value.Select(entry => ToWorktreeInfo(source, entry)).ToArray();
        return new WorktreeListResult
        {
            Source = ToSourceInfo(source),
            Worktrees = infos,
        };
    }

    private async Task<WorktreeCreateResult> WorktreeCreateAsync(
        WorktreeCreateParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation(ProtocolMethods.WorktreeCreate);
        var branch = string.IsNullOrWhiteSpace(p.Branch)
            ? WorktreePathRules.GeneratedBranchSlug((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            : p.Branch.Trim();
        var branchError = WorktreePathRules.ValidateBranchName(branch);
        if (branchError is not null)
            throw FailWorktree(ProtocolErrorCodes.InvalidParams, WorktreeErrorCodes.InvalidRequest, branchError);
        var @base = string.IsNullOrWhiteSpace(p.Base) ? "HEAD" : p.Base.Trim();
        var source = ResolveWorktreeCreateOpenSource(p.WorkspaceId, p.Cwd, p.TrustRepository);
        var checkout = ResolveCreateCheckoutPath(p.Path, source, branch);
        var pathError = WorktreePathRules.ValidateAbsoluteCheckoutPath(checkout);
        if (pathError is not null)
            throw FailWorktree(ProtocolErrorCodes.InvalidParams, WorktreeErrorCodes.InvalidRequest, pathError);

        var opKey = WorktreePathRules.CanonicalOrOriginal(checkout);
        BeginWorktreeOp(opKey);
        try
        {
            EnsureAttachPaneMutation(connection, ProtocolMethods.WorktreeCreate);
            var existingOpen = FindOpenWorkspaceByCheckout(checkout);
            var listed = _gitWorktrees.List(source.RepoRoot, p.TrustRepository);
            if (!listed.IsOk)
                throw FailWorktree(ProtocolErrorCodes.InvalidState, listed.Error.Code, listed.Error.Message);
            var existingEntry = listed.Value.FirstOrDefault(e => WorktreePathRules.PathsEqual(e.Path, checkout));
            if (existingOpen is null && existingEntry is null)
            {
                try
                {
                    var parent = Directory.GetParent(checkout);
                    if (parent is not null)
                        Directory.CreateDirectory(parent.FullName);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw FailWorktree(
                        ProtocolErrorCodes.InvalidState,
                        WorktreeErrorCodes.CreateFailed,
                        WorktreePathRules.RedactMessage("failed to create worktree parent directory", [checkout]));
                }

                var added = _gitWorktrees.Add(source.RepoRoot, checkout, branch, @base, p.TrustRepository);
                if (!added.IsOk)
                    throw FailWorktree(ProtocolErrorCodes.InvalidState, added.Error.Code, added.Error.Message);
            }
            else if (existingEntry is not null && !string.IsNullOrWhiteSpace(existingEntry.Branch))
            {
                branch = existingEntry.Branch;
            }

            source = await EnsureParentMembershipAsync(source, ct).ConfigureAwait(false);
            var opened = await OpenCheckoutWorkspaceAsync(
                    source,
                    checkout,
                    branch,
                    p.Label,
                    p.Focus,
                    linked: true,
                    existingEntry,
                    ct)
                .ConfigureAwait(false);
            await EmitWorktreeEventAsync(
                    ProtocolEventTypes.WorktreeCreated,
                    opened,
                    forced: null,
                    ct)
                .ConfigureAwait(false);
            return new WorktreeCreateResult
            {
                Workspace = opened.Workspace,
                Worktree = opened.Worktree,
                Created = opened.Created,
                AlreadyOpen = opened.AlreadyOpen,
            };
        }
        finally
        {
            EndWorktreeOp(opKey);
        }
    }

    private async Task<WorktreeOpenResult> WorktreeOpenAsync(
        WorktreeOpenParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation(ProtocolMethods.WorktreeOpen);
        if (string.IsNullOrWhiteSpace(p.Path) == string.IsNullOrWhiteSpace(p.Branch))
        {
            throw FailWorktree(
                ProtocolErrorCodes.InvalidParams,
                WorktreeErrorCodes.InvalidRequest,
                "exactly one of path or branch is required");
        }

        var source = ResolveWorktreeCreateOpenSource(p.WorkspaceId, p.Cwd, p.TrustRepository);
        var listed = _gitWorktrees.List(source.RepoRoot, p.TrustRepository);
        if (!listed.IsOk)
            throw FailWorktree(ProtocolErrorCodes.InvalidState, listed.Error.Code, listed.Error.Message);

        ExistingWorktree entry;
        if (!string.IsNullOrWhiteSpace(p.Path))
        {
            var pathError = WorktreePathRules.ValidateAbsoluteCheckoutPath(p.Path);
            if (pathError is not null)
                throw FailWorktree(ProtocolErrorCodes.InvalidParams, WorktreeErrorCodes.InvalidRequest, pathError);
            entry = listed.Value.FirstOrDefault(e => WorktreePathRules.PathsEqual(e.Path, p.Path))
                ?? throw FailWorktree(ProtocolErrorCodes.NotFound, WorktreeErrorCodes.WorktreeNotFound, "worktree not found");
        }
        else
        {
            var matches = listed.Value
                .Where(e =>
                    !e.IsBare
                    && !e.IsPrunable
                    && !e.IsDetached
                    && string.Equals(e.Branch, p.Branch, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length == 0)
                throw FailWorktree(ProtocolErrorCodes.NotFound, WorktreeErrorCodes.WorktreeNotFound, "worktree not found");
            if (matches.Length > 1)
            {
                throw FailWorktree(
                    ProtocolErrorCodes.InvalidParams,
                    WorktreeErrorCodes.AmbiguousBranch,
                    "branch matches more than one worktree");
            }

            entry = matches[0];
        }

        if (entry.IsBare || entry.IsPrunable)
            throw FailWorktree(ProtocolErrorCodes.NotFound, WorktreeErrorCodes.WorktreeNotFound, "worktree cannot be opened");

        var opKey = WorktreePathRules.CanonicalOrOriginal(entry.Path);
        BeginWorktreeOp(opKey);
        try
        {
            EnsureAttachPaneMutation(connection, ProtocolMethods.WorktreeOpen);
            source = await EnsureParentMembershipAsync(source, ct).ConfigureAwait(false);
            var linked = !WorktreePathRules.PathsEqual(entry.Path, source.RepoRoot);
            var opened = await OpenCheckoutWorkspaceAsync(
                    source,
                    entry.Path,
                    entry.Branch,
                    p.Label,
                    p.Focus,
                    linked,
                    entry,
                    ct)
                .ConfigureAwait(false);
            await EmitWorktreeEventAsync(
                    ProtocolEventTypes.WorktreeOpened,
                    opened,
                    forced: null,
                    ct)
                .ConfigureAwait(false);
            return new WorktreeOpenResult
            {
                Workspace = opened.Workspace,
                Worktree = opened.Worktree,
                Created = opened.Created,
                AlreadyOpen = opened.AlreadyOpen,
            };
        }
        finally
        {
            EndWorktreeOp(opKey);
        }
    }

    private async Task<WorktreeRemoveResult> WorktreeRemoveAsync(
        WorktreeRemoveParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        EnsureNotFrozenForMutation(ProtocolMethods.WorktreeRemove);
        var id = RequireField(p.WorkspaceId, "workspace_id");
        var workspaceId = new WorkspaceId(id);
        var ws = _state.GetWorkspace(workspaceId)
            ?? throw FailWorktree(ProtocolErrorCodes.NotFound, WorktreeErrorCodes.WorkspaceNotFound, "workspace not found");
        if (ws.Worktree is not { IsLinkedWorktree: true } membership)
        {
            throw FailWorktree(
                ProtocolErrorCodes.InvalidState,
                WorktreeErrorCodes.NotLinkedWorktree,
                "workspace is not a linked worktree checkout");
        }

        var opKey = WorktreePathRules.CanonicalOrOriginal(membership.CheckoutPath);
        BeginWorktreeOp(opKey);
        WorkspaceCloseCommit closeCommit;
        WorktreeInfo? pluginRow = null;
        PluginWorktreeContext? nestedWorktree = null;
        string? workspaceLabel = null;
        try
        {
            await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                EnsureAttachPaneMutation(connection, ProtocolMethods.WorktreeRemove);
                EnsureNotFrozenForMutation(ProtocolMethods.WorktreeRemove);
                ws = _state.GetWorkspace(workspaceId)
                    ?? throw FailWorktree(ProtocolErrorCodes.NotFound, WorktreeErrorCodes.WorkspaceNotFound, "workspace not found");
                if (ws.Worktree is not { IsLinkedWorktree: true } liveMembership)
                {
                    throw FailWorktree(
                        ProtocolErrorCodes.InvalidState,
                        WorktreeErrorCodes.NotLinkedWorktree,
                        "workspace is not a linked worktree checkout");
                }

                membership = liveMembership;
                RefuseLastWorkspaceClose(ws);
                pluginRow = CaptureWorktreeRow(ws, membership, p.TrustRepository);
                nestedWorktree = PluginInvocationContextWorktree.FromMembership(membership);
                workspaceLabel = ws.Label;

                var removed = _gitWorktrees.Remove(
                    membership.RepoRoot,
                    membership.CheckoutPath,
                    p.Force,
                    p.TrustRepository);
                if (!removed.IsOk)
                {
                    throw FailWorktree(
                        ProtocolErrorCodes.InvalidState,
                        removed.Error.Code,
                        removed.Error.Message);
                }

                closeCommit = WorkspaceCloseUnderGate(workspaceId);
            }
            finally
            {
                _bindingMutationGate.Release();
            }

            await FinishWorkspaceCloseAsync(closeCommit, ct).ConfigureAwait(false);
            if (pluginRow is null)
            {
                throw FailWorktree(
                    ProtocolErrorCodes.InvalidState,
                    WorktreeErrorCodes.RemoveFailed,
                    "worktree row missing after remove");
            }

            await EmitWorktreeEventAsync(
                    ProtocolEventTypes.WorktreeRemoved,
                    id,
                    branch: null,
                    membership.Label,
                    alreadyOpen: false,
                    pluginRow,
                    nestedWorktree,
                    workspaceLabel,
                    forced: p.Force,
                    ct)
                .ConfigureAwait(false);
            return new WorktreeRemoveResult { Ok = true, WorkspaceId = id };
        }
        finally
        {
            EndWorktreeOp(opKey);
        }
    }

    private void RefuseLastWorkspaceClose(WorkspaceState ws)
    {
        var group = WorktreeCloseGroup(ws);
        if (_state.ListWorkspaces().Count <= group.Count)
        {
            throw FailWorktree(
                ProtocolErrorCodes.InvalidState,
                WorktreeErrorCodes.LastWorkspace,
                "Cannot close the last workspace");
        }
    }

    private WorktreeSource ResolveWorktreeCreateOpenSource(string? workspaceId, string? cwd, bool trust)
    {
        var source = ResolveWorktreeSourceCore(workspaceId, cwd, trust, allowLinked: false);
        if (source.IsLinked)
        {
            throw FailWorktree(
                ProtocolErrorCodes.InvalidState,
                WorktreeErrorCodes.LinkedWorktreeSource,
                "New and open worktree actions start from the repo parent workspace.");
        }

        return source;
    }

    private WorktreeSource ResolveWorktreeListSource(string? workspaceId, string? cwd, bool trust) =>
        ResolveWorktreeSourceCore(workspaceId, cwd, trust, allowLinked: true);

    private WorktreeSource ResolveWorktreeSourceCore(
        string? workspaceId, string? cwd, bool trust, bool allowLinked)
    {
        if (!string.IsNullOrWhiteSpace(workspaceId) && !string.IsNullOrWhiteSpace(cwd))
        {
            throw FailWorktree(
                ProtocolErrorCodes.InvalidParams,
                WorktreeErrorCodes.InvalidRequest,
                "only one of workspace_id or cwd may be supplied");
        }

        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            var ws = _state.GetWorkspace(new WorkspaceId(workspaceId))
                ?? throw FailWorktree(ProtocolErrorCodes.NotFound, WorktreeErrorCodes.WorkspaceNotFound, "workspace not found");
            return SourceFromWorkspace(ws, trust, allowLinked);
        }

        if (!string.IsNullOrWhiteSpace(cwd))
        {
            var absolute = ExpandUserPath(cwd);
            if (!Path.IsPathRooted(absolute))
            {
                throw FailWorktree(
                    ProtocolErrorCodes.InvalidParams,
                    WorktreeErrorCodes.InvalidRequest,
                    "worktree path must be absolute");
            }

            return SourceFromCheckout(absolute, trust, allowLinked, workspaceId: null);
        }

        var focused = _state.Snapshot().FocusedWorkspaceId
            ?? throw FailWorktree(
                ProtocolErrorCodes.InvalidParams,
                WorktreeErrorCodes.InvalidRequest,
                "workspace_id or cwd is required when no workspace is focused");
        var focusedWs = _state.GetWorkspace(focused)
            ?? throw FailWorktree(ProtocolErrorCodes.NotFound, WorktreeErrorCodes.WorkspaceNotFound, "workspace not found");
        return SourceFromWorkspace(focusedWs, trust, allowLinked);
    }

    private WorktreeSource SourceFromWorkspace(WorkspaceState ws, bool trust, bool allowLinked)
    {
        if (ws.Worktree is { } membership)
        {
            if (membership.IsLinkedWorktree && !allowLinked)
            {
                throw FailWorktree(
                    ProtocolErrorCodes.InvalidState,
                    WorktreeErrorCodes.LinkedWorktreeSource,
                    "New and open worktree actions start from the repo parent workspace.");
            }

            var parentCheckout = membership.IsLinkedWorktree ? membership.RepoRoot : membership.CheckoutPath;
            var parentWs = FindOpenWorkspaceByCheckout(parentCheckout)
                ?? FindParentWorkspaceByKey(membership.Key);
            return new WorktreeSource(
                parentWs?.Id.Value ?? (membership.IsLinkedWorktree ? null : ws.Id.Value),
                parentCheckout,
                membership.RepoRoot,
                membership.Key,
                membership.Label,
                IsLinked: false);
        }

        return SourceFromCheckout(ws.Cwd, trust, allowLinked, ws.Id.Value);
    }

    private WorktreeSource SourceFromCheckout(string cwd, bool trust, bool allowLinked, string? workspaceId)
    {
        var probe = _gitWorktrees.ProbeSpace(cwd, trust);
        if (!probe.IsOk)
        {
            throw FailWorktree(
                ProtocolErrorCodes.InvalidState,
                probe.Error.Code,
                "worktree actions require a workspace inside a Git work tree");
        }

        var space = probe.Value;
        if (space.IsLinkedWorktree && !allowLinked)
        {
            throw FailWorktree(
                ProtocolErrorCodes.InvalidState,
                WorktreeErrorCodes.LinkedWorktreeSource,
                "New and open worktree actions start from the repo parent workspace.");
        }

        var parentCheckout = space.IsLinkedWorktree
            ? ParentCheckoutPath(space, trust)
            : space.RepoRoot;
        var sourceWs = FindOpenWorkspaceByCheckout(parentCheckout)
            ?? FindParentWorkspaceByKey(space.Key);
        return new WorktreeSource(
            sourceWs?.Id.Value ?? (space.IsLinkedWorktree ? null : workspaceId),
            parentCheckout,
            parentCheckout,
            space.Key,
            space.RepoName,
            IsLinked: false);
    }

    private string ParentCheckoutPath(GitSpaceMetadata space, bool trust)
    {
        var listed = _gitWorktrees.List(space.RepoRoot, trust);
        if (!listed.IsOk)
            return space.RepoRoot;
        foreach (var entry in listed.Value)
        {
            var probe = _gitWorktrees.ProbeSpace(entry.Path, trust);
            if (probe.IsOk
                && string.Equals(probe.Value.Key, space.Key, StringComparison.Ordinal)
                && !probe.Value.IsLinkedWorktree)
            {
                return probe.Value.RepoRoot;
            }
        }

        return space.RepoRoot;
    }

    private async Task<WorktreeSource> EnsureParentMembershipAsync(WorktreeSource source, CancellationToken ct)
    {
        await _parentMembershipGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = FindOpenWorkspaceByCheckout(source.SourceCheckoutPath)
                ?? FindParentWorkspaceByKey(source.RepoKey);
            if (existing is not null)
                source = source with { WorkspaceId = existing.Id.Value };
            else if (string.IsNullOrWhiteSpace(source.WorkspaceId))
            {
                var createdJson = await WorkspaceCreateAsync(
                        new WorkspaceCreateParams
                        {
                            Cwd = source.SourceCheckoutPath,
                            Label = source.RepoName,
                            CreatePane = true,
                        },
                        connection: null,
                        ct)
                    .ConfigureAwait(false);
                var id = createdJson["workspace_id"]?.GetValue<string>()
                    ?? throw FailWorktree(ProtocolErrorCodes.InvalidState, WorktreeErrorCodes.OpenFailed, "workspace create failed");
                source = source with { WorkspaceId = id };
            }

            var ws = _state.GetWorkspace(new WorkspaceId(source.WorkspaceId));
            if (ws is null)
                return source;

            var membership = new WorktreeSpaceMembership
            {
                Key = source.RepoKey,
                Label = source.RepoName,
                RepoRoot = WorktreePathRules.CanonicalOrOriginal(source.RepoRoot),
                CheckoutPath = WorktreePathRules.CanonicalOrOriginal(source.SourceCheckoutPath),
                IsLinkedWorktree = false,
            };
            if (ws.Worktree is null
                || !string.Equals(ws.Worktree.Key, membership.Key, StringComparison.Ordinal)
                || ws.Worktree.IsLinkedWorktree != membership.IsLinkedWorktree
                || !WorktreePathRules.PathsEqual(ws.Worktree.CheckoutPath, membership.CheckoutPath))
            {
                _state.SetWorktreeMembership(ws.Id, membership);
                await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
            }

            return source;
        }
        finally
        {
            _parentMembershipGate.Release();
        }
    }

    private async Task<(WorkspaceResult Workspace, WorktreeInfo Worktree, bool Created, bool AlreadyOpen)>
        OpenCheckoutWorkspaceAsync(
            WorktreeSource source,
            string checkout,
            string? branch,
            string? label,
            bool focus,
            bool linked,
            ExistingWorktree? listing,
            CancellationToken ct)
    {
        var existing = FindOpenWorkspaceByCheckout(checkout);
        WorkspaceState ws;
        var created = false;
        var alreadyOpen = existing is not null;
        if (existing is not null)
        {
            ws = existing;
            if (focus)
                ws = _state.FocusWorkspace(ws.Id) ?? ws;
        }
        else
        {
            var createdJson = await WorkspaceCreateAsync(
                    new WorkspaceCreateParams
                    {
                        Cwd = checkout,
                        Label = string.IsNullOrWhiteSpace(label)
                            ? Path.GetFileName(checkout.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                            : label,
                        CreatePane = true,
                    },
                    connection: null,
                    ct)
                .ConfigureAwait(false);
            var id = createdJson["workspace_id"]?.GetValue<string>()
                ?? throw FailWorktree(ProtocolErrorCodes.InvalidState, WorktreeErrorCodes.OpenFailed, "workspace create failed");
            ws = _state.GetWorkspace(new WorkspaceId(id))
                ?? throw FailWorktree(ProtocolErrorCodes.InvalidState, WorktreeErrorCodes.OpenFailed, "workspace create failed");
            created = true;
            if (focus)
                ws = _state.FocusWorkspace(ws.Id) ?? ws;
        }

        if (!string.IsNullOrWhiteSpace(label) && !string.Equals(ws.Label, label, StringComparison.Ordinal))
            ws = _state.RenameWorkspace(ws.Id, RejectUnsafeLabel(label)!) ?? ws;

        ws = _state.SetWorktreeMembership(ws.Id, new WorktreeSpaceMembership
        {
            Key = source.RepoKey,
            Label = source.RepoName,
            RepoRoot = WorktreePathRules.CanonicalOrOriginal(source.RepoRoot),
            CheckoutPath = WorktreePathRules.CanonicalOrOriginal(checkout),
            IsLinkedWorktree = linked,
        }) ?? ws;
        await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);

        var info = new WorktreeInfo
        {
            Path = checkout,
            Branch = branch,
            IsBare = listing?.IsBare ?? false,
            IsDetached = listing?.IsDetached ?? false,
            IsPrunable = listing?.IsPrunable ?? false,
            IsLinkedWorktree = linked,
            OpenWorkspaceId = ws.Id.Value,
            Label = source.RepoName,
        };
        return (ToWorkspaceResult(ws), info, created, alreadyOpen);
    }

    /// <summary>
    /// probe checkout key, then identity cwd.
    /// </summary>
    private WorkspaceState? FindOpenWorkspaceByCheckout(string checkout)
    {
        var canonical = WorktreePathRules.CanonicalOrOriginal(checkout);
        foreach (var workspace in _state.ListWorkspaces())
        {
            if (workspace.Worktree is { } membership
                && WorktreePathRules.PathsEqual(membership.CheckoutPath, canonical))
            {
                return workspace;
            }

            var probe = _gitWorktrees.ProbeSpace(workspace.Cwd, trustRepository: false);
            if (probe.IsOk && WorktreePathRules.PathsEqual(probe.Value.CheckoutKey, canonical))
                return workspace;

            if (WorktreePathRules.PathsEqual(workspace.Cwd, canonical))
                return workspace;
        }

        return null;
    }

    private WorkspaceState? FindParentWorkspaceByKey(string repoKey) =>
        _state.ListWorkspaces().FirstOrDefault(w =>
            w.Worktree is { IsLinkedWorktree: false } membership
            && string.Equals(membership.Key, repoKey, StringComparison.Ordinal));

    private string ResolveCreateCheckoutPath(string? requested, WorktreeSource source, string branch)
    {
        if (!string.IsNullOrWhiteSpace(requested))
            return ExpandUserPath(requested);
        return WorktreePathRules.DefaultCheckoutPath(ResolveWorktreeDirectory(), source.RepoName, branch);
    }

    private string ResolveWorktreeDirectory()
    {
        var configured = AttachConfig.Worktrees.Directory;
        return WorktreePathRules.ExpandTilde(
            string.IsNullOrWhiteSpace(configured) ? "~/.config/hypa/worktrees" : configured,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    private string ExpandUserPath(string path) =>
        WorktreePathRules.ExpandTilde(
            path,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private static WorktreeSourceInfo ToSourceInfo(WorktreeSource source) => new()
    {
        RepoKey = source.RepoKey,
        RepoName = source.RepoName,
        RepoRoot = source.RepoRoot,
        SourceCheckoutPath = source.SourceCheckoutPath,
        SourceWorkspaceId = source.WorkspaceId,
    };

    private WorktreeInfo ToWorktreeInfo(WorktreeSource source, ExistingWorktree entry)
    {
        var open = FindOpenWorkspaceByCheckout(entry.Path);
        var linked = !WorktreePathRules.PathsEqual(entry.Path, source.RepoRoot);
        return new WorktreeInfo
        {
            Path = entry.Path,
            Branch = entry.Branch,
            IsBare = entry.IsBare,
            IsDetached = entry.IsDetached,
            IsPrunable = entry.IsPrunable,
            IsLinkedWorktree = linked,
            OpenWorkspaceId = open?.Id.Value,
            Label = source.RepoName,
        };
    }

    private WorkspaceResult ToWorkspaceResult(WorkspaceState ws)
    {
        var tokens = _metadata.Get("workspace", ws.Id.Value);
        return new WorkspaceResult
        {
            WorkspaceId = ws.Id.Value,
            Label = ws.Label,
            Cwd = ws.Cwd,
            Ordinal = ws.Ordinal,
            FocusedTabId = ws.FocusedTabId?.Value,
            Tokens = tokens.Count == 0
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(tokens, StringComparer.Ordinal),
            Worktree = ToWorktreeChrome(ws.Worktree),
        };
    }

    private static WorkspaceWorktreeChrome? ToWorktreeChrome(WorktreeSpaceMembership? membership) =>
        membership is null
            ? null
            : new WorkspaceWorktreeChrome
            {
                Key = membership.Key,
                Label = membership.Label,
                IsLinkedWorktree = membership.IsLinkedWorktree,
            };

    internal IReadOnlyList<WorkspaceId> WorktreeCloseGroup(WorkspaceState ws)
    {
        if (ws.Worktree is not { IsLinkedWorktree: false } parent)
            return [ws.Id];
        var group = _state.WorkspacesSharingKey(parent.Key);
        var children = group.Where(w => w.Worktree?.IsLinkedWorktree == true).Select(w => w.Id).ToArray();
        return children.Length == 0 ? [ws.Id] : [.. children, ws.Id];
    }

    private void BeginWorktreeOp(string key)
    {
        lock (_worktreeOpGate)
        {
            if (!_worktreeOpsInProgress.Add(key))
            {
                throw FailWorktree(
                    ProtocolErrorCodes.InvalidState,
                    WorktreeErrorCodes.OperationInProgress,
                    "worktree operation is already in progress for this checkout");
            }
        }
    }

    private void EndWorktreeOp(string key)
    {
        lock (_worktreeOpGate)
        {
            _worktreeOpsInProgress.Remove(key);
        }
    }

    private WorktreeInfo CaptureWorktreeRow(
        WorkspaceState ws,
        WorktreeSpaceMembership membership,
        bool trustRepository)
    {
        ExistingWorktree? entry = null;
        var listed = _gitWorktrees.List(membership.RepoRoot, trustRepository);
        if (listed.IsOk)
        {
            foreach (var item in listed.Value)
            {
                if (WorktreePathRules.PathsEqual(item.Path, membership.CheckoutPath))
                {
                    entry = item;
                    break;
                }
            }
        }

        return new WorktreeInfo
        {
            Path = membership.CheckoutPath,
            Branch = entry?.Branch,
            IsBare = entry?.IsBare ?? false,
            IsDetached = entry?.IsDetached ?? false,
            IsPrunable = entry?.IsPrunable ?? false,
            IsLinkedWorktree = membership.IsLinkedWorktree,
            OpenWorkspaceId = ws.Id.Value,
            Label = membership.Label,
        };
    }

    private Task EmitWorktreeEventAsync(
        string eventType,
        (WorkspaceResult Workspace, WorktreeInfo Worktree, bool Created, bool AlreadyOpen) opened,
        bool? forced,
        CancellationToken ct)
    {
        var workspaceId = opened.Workspace.WorkspaceId ?? "";
        var live = string.IsNullOrWhiteSpace(workspaceId)
            ? null
            : _state.GetWorkspace(new WorkspaceId(workspaceId));
        return EmitWorktreeEventAsync(
            eventType,
            workspaceId,
            opened.Worktree.Branch,
            opened.Worktree.Label,
            opened.AlreadyOpen,
            opened.Worktree,
            PluginInvocationContextWorktree.FromMembership(live?.Worktree),
            live?.Label ?? opened.Workspace.Label,
            forced,
            ct);
    }

    internal async Task EmitWorktreeEventAsync(
        string eventType,
        string workspaceId,
        string? branch,
        string label,
        bool alreadyOpen,
        WorktreeInfo pluginRow,
        PluginWorktreeContext? nestedWorktree,
        string? workspaceLabel,
        bool? forced,
        CancellationToken ct)
    {
        if (_journal is null)
            return;
        var payload = RuntimeEventPayloadJson.WriteWorktreeLifecycle(
            eventType, workspaceId, branch, label, alreadyOpen);
        payload = _redactor.RedactJsonPayload(eventType, payload);
        var pluginPayload = PluginWorktreeEventJson.Write(
            workspaceId,
            workspaceLabel,
            nestedWorktree,
            pluginRow,
            alreadyOpen: string.Equals(eventType, ProtocolEventTypes.WorktreeRemoved, StringComparison.Ordinal)
                ? null
                : alreadyOpen,
            forced);
        await PublishReliableAsync(
            EventClass.Lifecycle,
            eventType,
            payload,
            ct,
            notifyPlugin: true,
            pluginPayload: pluginPayload).ConfigureAwait(false);
    }

    private static ControlPlaneException FailWorktree(int code, string errorCode, string? message)
    {
        var text = string.IsNullOrWhiteSpace(message) ? errorCode : message;
        return new ControlPlaneException(code, text, errorCode);
    }

    private sealed record WorktreeSource(
        string? WorkspaceId,
        string SourceCheckoutPath,
        string RepoRoot,
        string RepoKey,
        string RepoName,
        bool IsLinked);
}
