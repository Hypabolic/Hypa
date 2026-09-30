using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.Worktrees;
using Xunit;

namespace Hypa.AgentRuntime.Tests.Worktrees;

public sealed class WorktreePathRulesTests
{
    [Fact]
    public void Generated_branch_slug_matches_herdr_shape()
    {
        var slug = WorktreePathRules.GeneratedBranchSlug(0);
        Assert.Equal("worktree/brave-river-0000", slug);
    }

    [Fact]
    public void Branch_to_path_slug_collapses_separators()
    {
        Assert.Equal("feat-name", WorktreePathRules.BranchToPathSlug("feat/name"));
        Assert.Equal("worktree", WorktreePathRules.BranchToPathSlug("@@@"));
    }

    [Fact]
    public void Porcelain_parse_reads_branch_and_flags()
    {
        var entries = WorktreePathRules.ParsePorcelain(
            """
            worktree /repo
            HEAD abc
            branch refs/heads/main

            worktree /repo/linked
            HEAD def
            detached
            prunable

            """);
        Assert.Equal(2, entries.Count);
        Assert.Equal("/repo", entries[0].Path);
        Assert.Equal("main", entries[0].Branch);
        Assert.True(entries[1].IsDetached);
        Assert.True(entries[1].IsPrunable);
    }

    [Fact]
    public void Dirty_and_not_worktree_messages_match_herdr()
    {
        Assert.True(WorktreePathRules.IsDirtyWorktreeRemoveError(
            "fatal: '/tmp/x' contains modified or untracked files, use --force to delete it"));
        Assert.True(WorktreePathRules.IsNotWorkingTreeRemoveError("fatal: '/tmp/x' is not a working tree"));
        Assert.False(WorktreePathRules.IsDirtyWorktreeRemoveError("fatal: other"));
    }

    [Fact]
    public void Path_and_branch_validation_reject_unsafe_values()
    {
        Assert.NotNull(WorktreePathRules.ValidateAbsoluteCheckoutPath("relative/path"));
        Assert.NotNull(WorktreePathRules.ValidateAbsoluteCheckoutPath("-oops"));
        Assert.NotNull(WorktreePathRules.ValidateBranchName("main; rm -rf /"));
        Assert.NotNull(WorktreePathRules.ValidateBranchName("-b"));
        Assert.Null(WorktreePathRules.ValidateBranchName("worktree/calm-river-0001"));
        Assert.Null(WorktreePathRules.ValidateAbsoluteCheckoutPath("/tmp/safe-checkout"));
    }

    [Fact]
    public void Remove_and_add_commands_are_argv_lists()
    {
        var remove = WorktreePathRules.BuildRemoveCommand("/repo", "/repo/wt", force: true, trustRepository: true);
        Assert.Equal("git", remove.Program);
        Assert.Equal(
            ["-c", "safe.directory=/repo", "-C", "/repo", "worktree", "remove", "--force", "/repo/wt"],
            remove.Args);
        Assert.DoesNotContain(remove.Args, a => a.Contains(' ') && a.Contains("worktree"));

        var addNew = WorktreePathRules.BuildAddNewBranchCommand("/repo", "/wt", "feat", "HEAD", false);
        Assert.Equal(["-C", "/repo", "worktree", "add", "-b", "feat", "/wt", "HEAD"], addNew.Args);

        var addExisting = WorktreePathRules.BuildAddExistingBranchCommand("/repo", "/wt", "feat", false);
        Assert.Equal(["-C", "/repo", "worktree", "add", "/wt", "feat"], addExisting.Args);
    }

    [Fact]
    public void Canonical_paths_collapse_macos_var_private()
    {
        var temp = Directory.CreateTempSubdirectory("hypa-canon-").FullName;
        try
        {
            var a = WorktreePathRules.CanonicalOrOriginal(temp);
            var b = WorktreePathRules.CanonicalOrOriginal(Path.GetFullPath(temp));
            Assert.True(WorktreePathRules.PathsEqual(temp, a));
            Assert.True(WorktreePathRules.PathsEqual(a, b));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* teardown */ }
        }
    }

    [Fact]
    public void Redact_checkout_hides_absolute_paths()
    {
        Assert.Equal("[checkout]", WorktreePathRules.RedactCheckout("/secret/repo/wt"));
        Assert.Equal("~/wt", WorktreePathRules.RedactCheckout("/home/u/wt", "/home/u"));
        var message = WorktreePathRules.RedactMessage(
            "failed at /secret/repo/wt",
            ["/secret/repo/wt"]);
        Assert.DoesNotContain("/secret/repo/wt", message);
        Assert.Contains("[checkout]", message);
    }

    [Fact]
    public void Opaque_repo_key_is_stable_and_path_free()
    {
        var key = WorktreePathRules.OpaqueRepoKey("/repo/.git");
        Assert.Equal("6b4ca2db35cfaf43d6963005a8445402c4ea6f70b038086ad0d6decda64670c0", key);
        Assert.False(WorktreePathRules.LooksLikeFilesystemPath(key));
        Assert.Equal(key, WorktreePathRules.OpaqueRepoKey("/repo/.git"));
    }

    [Fact]
    public void Leftover_recovery_fails_closed_when_list_fails()
    {
        var listed = Result<IReadOnlyList<ExistingWorktree>, WorktreeError>.Fail(
            new WorktreeError(WorktreeErrorCodes.ListFailed, "git worktree list failed at /secret/wt"));
        var decision = WorktreePathRules.DecideLeftoverRecovery(
            listed,
            "/secret/wt",
            "/secret/repo",
            directoryExists: true,
            leftoverMatchesRepo: true);
        Assert.False(decision.IsOk);
        Assert.Equal(WorktreeErrorCodes.ListFailed, decision.Error.Code);
        Assert.DoesNotContain("/secret/wt", decision.Error.Message, StringComparison.Ordinal);
    }
}
