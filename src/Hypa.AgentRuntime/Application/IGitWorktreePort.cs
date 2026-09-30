using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.Worktrees;

namespace Hypa.AgentRuntime.Application;

/// <summary>Local git worktree operations. Argv only. No shell wrap.</summary>
public interface IGitWorktreePort
{
    Result<IReadOnlyList<ExistingWorktree>, WorktreeError> List(string repoRoot, bool trustRepository);

    Result<GitSpaceMetadata, WorktreeError> ProbeSpace(string cwd, bool trustRepository);

    Result<bool, WorktreeError> LocalBranchExists(string repoRoot, string branch, bool trustRepository);

    Result<bool, WorktreeError> CheckoutHasDirtyFiles(string checkout, bool trustRepository);

    Result<RuntimeUnit, WorktreeError> Add(
        string repoRoot,
        string path,
        string branch,
        string @base,
        bool trustRepository);

    Result<RuntimeUnit, WorktreeError> Remove(
        string repoRoot,
        string path,
        bool force,
        bool trustRepository);

    Result<string?, WorktreeError> CommonWorktreesDir(string repoRoot, bool trustRepository);
}
