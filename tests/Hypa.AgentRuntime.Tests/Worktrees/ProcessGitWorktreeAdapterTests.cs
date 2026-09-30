using Hypa.AgentRuntime.Domain.Worktrees;
using Hypa.AgentRuntime.Infrastructure.Git;
using Xunit;

namespace Hypa.AgentRuntime.Tests.Worktrees;

public sealed class ProcessGitWorktreeAdapterTests
{
    [Fact]
    public void Create_list_remove_and_dirty_force_use_real_git()
    {
        using var repo = GitRepo.Create();
        var git = new ProcessGitWorktreeAdapter();
        var listed = git.List(repo.Root, trustRepository: false);
        Assert.True(listed.IsOk);
        Assert.Single(listed.Value!);

        var checkout = Path.Combine(repo.Worktrees, "feature");
        var added = git.Add(repo.Root, checkout, "feature", "HEAD", false);
        Assert.True(added.IsOk, added.IsOk ? null : added.Error.Message);
        Assert.True(Directory.Exists(checkout));

        listed = git.List(repo.Root, false);
        Assert.Equal(2, listed.Value.Count);
        Assert.Contains(listed.Value, e => e.Branch == "feature");

        File.WriteAllText(Path.Combine(checkout, "dirty.txt"), "x");
        var dirtyProbe = git.CheckoutHasDirtyFiles(checkout, false);
        Assert.True(dirtyProbe.IsOk);
        Assert.True(dirtyProbe.Value);
        var dirty = git.Remove(repo.Root, checkout, force: false, trustRepository: false);
        Assert.False(dirty.IsOk);
        Assert.Equal(WorktreeErrorCodes.DirtyRequiresForce, dirty.Error.Code);
        Assert.True(Directory.Exists(checkout));
        Assert.DoesNotContain(checkout, dirty.Error.Message, StringComparison.Ordinal);

        var forced = git.Remove(repo.Root, checkout, force: true, trustRepository: false);
        Assert.True(forced.IsOk, forced.IsOk ? null : forced.Error.Message);
        Assert.False(Directory.Exists(checkout));
    }

    [Fact]
    public void Force_remove_refuses_an_unrelated_directory()
    {
        using var repo = GitRepo.Create();
        var git = new ProcessGitWorktreeAdapter();
        var unrelated = Path.Combine(repo.Temp, "unrelated");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "keep");

        var removed = git.Remove(repo.Root, unrelated, force: true, trustRepository: false);
        Assert.False(removed.IsOk);
        Assert.Equal(WorktreeErrorCodes.RemoveFailed, removed.Error.Code);
        Assert.True(File.Exists(Path.Combine(unrelated, "keep.txt")));
        Assert.DoesNotContain(unrelated, removed.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Force_remove_fails_closed_when_list_fails()
    {
        using var repo = GitRepo.Create();
        var leftover = Path.Combine(repo.Worktrees, "leftover");
        Directory.CreateDirectory(leftover);
        var gitdir = Path.Combine(repo.Root, ".git", "worktrees", "leftover");
        Directory.CreateDirectory(gitdir);
        File.WriteAllText(Path.Combine(leftover, ".git"), "gitdir: " + gitdir + Environment.NewLine);
        File.WriteAllText(Path.Combine(leftover, "keep.txt"), "keep");

        var wrapper = Path.Combine(repo.Temp, "git-fail-list");
        File.WriteAllText(wrapper, """
            #!/usr/bin/env bash
            joined="$*"
            if [[ "$joined" == *"worktree list"* ]]; then
              echo "fatal: list failed at $joined" >&2
              exit 128
            fi
            if [[ "$joined" == *"worktree remove"* ]]; then
              echo "fatal: leftover is not a working tree" >&2
              exit 128
            fi
            exec git "$@"
            """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                wrapper,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var git = new ProcessGitWorktreeAdapter(wrapper);
        var removed = git.Remove(repo.Root, leftover, force: true, trustRepository: false);
        Assert.False(removed.IsOk);
        Assert.Equal(WorktreeErrorCodes.ListFailed, removed.Error.Code);
        Assert.True(Directory.Exists(leftover));
        Assert.True(File.Exists(Path.Combine(leftover, "keep.txt")));
        Assert.DoesNotContain(leftover, removed.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(repo.Root, removed.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_space_marks_linked_worktree()
    {
        using var repo = GitRepo.Create();
        var git = new ProcessGitWorktreeAdapter();
        var checkout = Path.Combine(repo.Worktrees, "linked");
        Assert.True(git.Add(repo.Root, checkout, "linked", "HEAD", false).IsOk);

        var parent = git.ProbeSpace(repo.Root, false);
        Assert.True(parent.IsOk);
        Assert.False(parent.Value!.IsLinkedWorktree);

        var child = git.ProbeSpace(checkout, false);
        Assert.True(child.IsOk);
        Assert.True(child.Value.IsLinkedWorktree);
        Assert.Equal(parent.Value.Key, child.Value.Key);
        Assert.False(WorktreePathRules.LooksLikeFilesystemPath(parent.Value.Key));
        Assert.DoesNotContain(repo.Root, parent.Value.Key, StringComparison.Ordinal);
    }
}

internal sealed class GitRepo : IDisposable
{
    public string Temp { get; }
    public string Root { get; }
    public string Worktrees { get; }

    private GitRepo(string temp, string root, string worktrees)
    {
        Temp = temp;
        Root = root;
        Worktrees = worktrees;
    }

    public static GitRepo Create()
    {
        var temp = Directory.CreateTempSubdirectory("hypa-wt-").FullName;
        var root = Path.Combine(temp, "repo");
        var worktrees = Path.Combine(temp, "worktrees");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(worktrees);
        RunGit(root, "init", "-b", "main");
        RunGit(root, "config", "user.email", "wt@example.com");
        RunGit(root, "config", "user.name", "Worktree Tests");
        File.WriteAllText(Path.Combine(root, "README"), "repo");
        RunGit(root, "add", "README");
        RunGit(root, "commit", "-m", "init");
        return new GitRepo(temp, root, worktrees);
    }

    public static void RunGit(string cwd, params string[] args)
    {
        using var proc = new System.Diagnostics.Process();
        proc.StartInfo.FileName = "git";
        proc.StartInfo.WorkingDirectory = cwd;
        proc.StartInfo.UseShellExecute = false;
        proc.StartInfo.RedirectStandardOutput = true;
        proc.StartInfo.RedirectStandardError = true;
        proc.StartInfo.CreateNoWindow = true;
        foreach (var arg in args)
            proc.StartInfo.ArgumentList.Add(arg);
        proc.Start();
        if (!proc.WaitForExit(15_000) || proc.ExitCode != 0)
        {
            var err = proc.StandardError.ReadToEnd();
            throw new InvalidOperationException("git " + string.Join(' ', args) + " failed: " + err);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(Temp, recursive: true); }
        catch { /* teardown */ }
    }
}
