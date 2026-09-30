using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.Worktrees;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.Cli.Attach.Worktrees;

public enum WorktreeDialogKind
{
    None,
    Create,
    Open,
    Remove,
}

public sealed record WorktreeOpenEntry(
    string Path,
    string? Branch,
    bool IsLinkedWorktree,
    bool IsDetached,
    string? OpenWorkspaceId,
    string Label)
{
    public string StatusLabel
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(OpenWorkspaceId))
                return "open";
            if (!string.IsNullOrWhiteSpace(Branch))
                return "";
            if (IsDetached && IsLinkedWorktree)
                return "detached";
            return "root";
        }
    }

    public bool MatchesQuery(string query)
    {
        var needle = query.Trim().ToLowerInvariant();
        if (needle.Length == 0)
            return true;
        var haystack = $"{Label} {Branch} {Path} {StatusLabel}".ToLowerInvariant();
        return haystack.Contains(needle, StringComparison.Ordinal);
    }

    public static WorktreeOpenEntry FromInfo(WorktreeInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new WorktreeOpenEntry(
            info.Path,
            info.Branch,
            info.IsLinkedWorktree,
            info.IsDetached,
            info.OpenWorkspaceId,
            info.Branch ?? info.Label);
    }
}

public sealed class WorktreeCreateState
{
    public required string SourceWorkspaceId { get; init; }
    public required string RepoName { get; init; }
    public required string WorktreeDirectory { get; init; }
    public string Branch { get; set; } = "";
    public string CheckoutPath { get; set; } = "";
    public bool ReplaceOnType { get; set; } = true;
    public string? Error { get; set; }
    public bool Creating { get; set; }
}

public sealed class WorktreeOpenState
{
    public required string SourceWorkspaceId { get; init; }
    public required IReadOnlyList<WorktreeOpenEntry> Entries { get; init; }
    public int Selected { get; set; }
    public string Query { get; set; } = "";
    public bool SearchFocused { get; set; }
    public string? Error { get; set; }
    public bool Opening { get; set; }

    public IReadOnlyList<int> FilteredIndices()
    {
        var list = new List<int>();
        for (var i = 0; i < Entries.Count; i++)
        {
            if (Entries[i].MatchesQuery(Query))
                list.Add(i);
        }

        return list;
    }

    public int? SelectedEntryIndex()
    {
        var filtered = FilteredIndices();
        if (filtered.Contains(Selected))
            return Selected;
        return filtered.Count == 0 ? null : filtered[0];
    }
}

public sealed class WorktreeRemoveState
{
    public required string WorkspaceId { get; init; }
    public required string Path { get; init; }
    public string? Error { get; set; }
    public bool Removing { get; set; }
    public bool ForceConfirmation { get; set; }
}

/// <summary>
// / Client worktree dialogs.
/// and <c>worktree_overlays.rs</c>.
/// </summary>
public sealed class WorktreeDialogModel
{
    public const string DirtyRequiresForce = "dirty_worktree_requires_force";
    public const string LinkedSourceMessage = "New and open worktree actions start from the repo parent workspace.";
    public const string NotLinkedMessage = "This workspace is not a Hypa-managed worktree checkout.";
    public const string NoWorktreesMessage = "No Git worktrees found for this repo.";
    public const string BranchRequired = "branch is required";

    public WorktreeDialogKind Kind { get; private set; }

    public WorktreeCreateState? Create { get; private set; }

    public WorktreeOpenState? Open { get; private set; }

    public WorktreeRemoveState? Remove { get; private set; }

    public string? EndpointError { get; set; }

    public WorktreeDialogLayout? Layout { get; set; }

    public bool IsOpen => Kind is not WorktreeDialogKind.None;

    public bool IsBusy =>
        Create is { Creating: true }
        || Open is { Opening: true }
        || Remove is { Removing: true };

    public static string CheckoutPathPreview(string root, string repo, string branch)
    {
        var separator = !root.StartsWith('/') && root.Contains('\\') ? '\\' : '/';
        return string.Concat(
            root.TrimEnd(separator),
            separator,
            repo,
            separator,
            WorktreePathRules.BranchToPathSlug(branch));
    }

    public void ShowCreate(string sourceWorkspaceId, string repoName, string worktreeDirectory, ulong seed)
    {
        var branch = WorktreePathRules.GeneratedBranchSlug(seed);
        Kind = WorktreeDialogKind.Create;
        Create = new WorktreeCreateState
        {
            SourceWorkspaceId = sourceWorkspaceId,
            RepoName = repoName,
            WorktreeDirectory = worktreeDirectory,
            Branch = branch,
            CheckoutPath = CheckoutPathPreview(worktreeDirectory, repoName, branch),
            ReplaceOnType = true,
        };
        Open = null;
        Remove = null;
        EndpointError = null;
    }

    public void ShowOpen(string sourceWorkspaceId, IReadOnlyList<WorktreeInfo> worktrees)
    {
        ArgumentNullException.ThrowIfNull(worktrees);
        var entries = worktrees
            .Where(entry => !entry.IsBare && !entry.IsPrunable)
            .Select(WorktreeOpenEntry.FromInfo)
            .ToArray();
        if (entries.Length == 0)
        {
            Close();
            EndpointError = NoWorktreesMessage;
            return;
        }

        Kind = WorktreeDialogKind.Open;
        Open = new WorktreeOpenState
        {
            SourceWorkspaceId = sourceWorkspaceId,
            Entries = entries,
        };
        Create = null;
        Remove = null;
        EndpointError = null;
    }

    public void ShowRemove(string workspaceId, IReadOnlyList<WorktreeInfo> worktrees)
    {
        ArgumentNullException.ThrowIfNull(worktrees);
        var path = worktrees
            .FirstOrDefault(entry => string.Equals(entry.OpenWorkspaceId, workspaceId, StringComparison.Ordinal))
            ?.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            Close();
            EndpointError = NotLinkedMessage;
            return;
        }

        Kind = WorktreeDialogKind.Remove;
        Remove = new WorktreeRemoveState
        {
            WorkspaceId = workspaceId,
            Path = path,
        };
        Create = null;
        Open = null;
        EndpointError = null;
    }

    public bool Cancel()
    {
        if (!IsOpen || IsBusy)
            return false;
        Close();
        return true;
    }

    public bool InsertText(string text)
    {
        if (string.IsNullOrEmpty(text) || IsBusy)
            return false;
        if (Create is { } create)
        {
            if (create.ReplaceOnType)
            {
                create.Branch = "";
                create.ReplaceOnType = false;
            }

            create.Branch += text;
            SyncCreatePath();
            return true;
        }

        if (Open is { SearchFocused: true } open)
        {
            open.Query += text;
            SelectFirstFiltered();
            return true;
        }

        return false;
    }

    public bool Backspace()
    {
        if (IsBusy)
            return false;
        if (Create is { } create)
        {
            if (create.ReplaceOnType)
            {
                create.Branch = "";
                create.ReplaceOnType = false;
            }
            else if (create.Branch.Length > 0)
                create.Branch = create.Branch[..^1];
            SyncCreatePath();
            return true;
        }

        if (Open is { SearchFocused: true } open)
        {
            if (open.Query.Length > 0)
                open.Query = open.Query[..^1];
            SelectFirstFiltered();
            return true;
        }

        return false;
    }

    public bool FocusOpenSearch()
    {
        if (Open is not { } open || open.Opening || open.SearchFocused)
            return false;
        open.SearchFocused = true;
        return true;
    }

    public bool MoveOpenSelection(int delta)
    {
        if (Open is not { } open || open.Opening)
            return false;
        var filtered = open.FilteredIndices();
        if (filtered.Count == 0)
        {
            open.Selected = 0;
            return true;
        }

        var current = filtered.ToList().IndexOf(open.Selected);
        if (current < 0)
            current = 0;
        var next = Math.Clamp(current + delta, 0, filtered.Count - 1);
        open.Selected = filtered[next];
        return true;
    }

    public bool SelectOpenEntry(int index)
    {
        if (Open is not { } open || open.Opening)
            return false;
        if (index < 0 || index >= open.Entries.Count)
            return false;
        if (!open.FilteredIndices().Contains(index))
            return false;
        open.Selected = index;
        return true;
    }

    public bool TryBeginCreate(out WorktreeCreateParams? parameters)
    {
        parameters = null;
        if (Create is not { } create || create.Creating)
            return false;
        var branch = create.Branch.Trim();
        if (branch.Length == 0)
        {
            create.Error = BranchRequired;
            return false;
        }

        create.Branch = branch;
        create.ReplaceOnType = false;
        SyncCreatePath();
        create.Creating = true;
        create.Error = null;
        parameters = new WorktreeCreateParams
        {
            WorkspaceId = create.SourceWorkspaceId,
            Branch = branch,
            Base = "HEAD",
            Focus = true,
        };
        return true;
    }

    public bool TryBeginOpen(out WorktreeOpenParams? parameters)
    {
        parameters = null;
        if (Open is not { } open || open.Opening)
            return false;
        var index = open.SelectedEntryIndex();
        if (index is null)
            return false;
        var entry = open.Entries[index.Value];
        open.Selected = index.Value;
        open.Opening = true;
        open.Error = null;
        parameters = new WorktreeOpenParams
        {
            WorkspaceId = open.SourceWorkspaceId,
            Path = entry.Path,
            Focus = true,
        };
        return true;
    }

    public bool TryBeginRemove(out WorktreeRemoveParams? parameters)
    {
        parameters = null;
        if (Remove is not { } remove || remove.Removing)
            return false;
        remove.Removing = true;
        remove.Error = null;
        parameters = new WorktreeRemoveParams
        {
            WorkspaceId = remove.WorkspaceId,
            Force = remove.ForceConfirmation,
        };
        return true;
    }

    public void ApplySuccess() => Close();

    public void ApplyCreateError(string message)
    {
        if (Create is not { } create)
            return;
        create.Creating = false;
        create.Error = message;
    }

    public void ApplyOpenError(string message)
    {
        if (Open is not { } open)
            return;
        open.Opening = false;
        open.Error = message;
    }

    public void ApplyRemoveError(string? errorCode, string message)
    {
        if (Remove is not { } remove)
            return;
        remove.Removing = false;
        if (!remove.ForceConfirmation
            && string.Equals(errorCode, DirtyRequiresForce, StringComparison.Ordinal))
        {
            remove.ForceConfirmation = true;
            remove.Error = null;
            return;
        }

        remove.Error = message;
    }

    public void Close()
    {
        Kind = WorktreeDialogKind.None;
        Create = null;
        Open = null;
        Remove = null;
    }

    private void SyncCreatePath()
    {
        if (Create is not { } create)
            return;
        create.CheckoutPath = CheckoutPathPreview(create.WorktreeDirectory, create.RepoName, create.Branch);
        create.Error = null;
    }

    private void SelectFirstFiltered()
    {
        if (Open is not { } open)
            return;
        var first = open.FilteredIndices();
        if (first.Count > 0)
            open.Selected = first[0];
    }
}
