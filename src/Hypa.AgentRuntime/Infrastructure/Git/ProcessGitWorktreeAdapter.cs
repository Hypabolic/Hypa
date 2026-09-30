using System.Diagnostics;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.Worktrees;

namespace Hypa.AgentRuntime.Infrastructure.Git;

/// <summary>
/// Local git worktree adapter. Commands use <see cref="ProcessStartInfo.ArgumentList"/>.
/// </summary>
public sealed class ProcessGitWorktreeAdapter : IGitWorktreePort
{
    private readonly string _gitExecutable;
    private readonly TimeSpan _timeout;

    public ProcessGitWorktreeAdapter(string? gitExecutable = null, TimeSpan? timeout = null)
    {
        _gitExecutable = string.IsNullOrWhiteSpace(gitExecutable) ? "git" : gitExecutable;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public Result<IReadOnlyList<ExistingWorktree>, WorktreeError> List(string repoRoot, bool trustRepository)
    {
        var args = new List<string>(WorktreePathRules.RepositoryGitArgs(repoRoot, trustRepository))
        {
            "worktree",
            "list",
            "--porcelain",
        };
        var run = Run(new WorktreeCommand { Program = _gitExecutable, Args = args }, repoRoot);
        if (!run.Ok)
        {
            return Fail<IReadOnlyList<ExistingWorktree>>(
                WorktreeErrorCodes.ListFailed,
                run.Error ?? "git worktree list failed",
                repoRoot);
        }

        return Ok<IReadOnlyList<ExistingWorktree>>(
            WorktreePathRules.ParsePorcelain(run.Stdout ?? string.Empty));
    }

    public Result<GitSpaceMetadata, WorktreeError> ProbeSpace(string cwd, bool trustRepository)
    {
        var toplevel = RunRevParse(cwd, trustRepository, "--show-toplevel");
        if (!toplevel.Ok)
        {
            return Fail<GitSpaceMetadata>(
                WorktreeErrorCodes.NotGitWorktree,
                "worktree actions require a path inside a Git work tree",
                cwd);
        }

        var gitDir = RunRevParse(cwd, trustRepository, "--git-dir");
        var commonDir = RunRevParse(cwd, trustRepository, "--git-common-dir");
        if (!gitDir.Ok || !commonDir.Ok)
        {
            return Fail<GitSpaceMetadata>(
                WorktreeErrorCodes.NotGitWorktree,
                "worktree actions require a path inside a Git work tree",
                cwd);
        }

        var checkout = ResolveGitPath(cwd, (toplevel.Stdout ?? string.Empty).Trim());
        var gitDirPath = ResolveGitPath(cwd, (gitDir.Stdout ?? string.Empty).Trim());
        var commonDirPath = ResolveGitPath(cwd, (commonDir.Stdout ?? string.Empty).Trim());
        var canonicalGit = WorktreePathRules.CanonicalOrOriginal(gitDirPath);
        var canonicalCommon = WorktreePathRules.CanonicalOrOriginal(commonDirPath);
        var isLinked = !WorktreePathRules.PathsEqual(canonicalGit, canonicalCommon);
        var labelPath = Path.GetFileName(canonicalCommon) is ".git" or ".bare"
            ? Directory.GetParent(canonicalCommon)?.FullName ?? checkout
            : canonicalCommon;
        var repoName = Path.GetFileName(
            labelPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(repoName))
            repoName = "repo";

        return Ok(new GitSpaceMetadata
        {
            Key = WorktreePathRules.OpaqueRepoKey(canonicalCommon),
            CheckoutKey = WorktreePathRules.CanonicalOrOriginal(checkout),
            RepoName = repoName,
            RepoRoot = checkout,
            IsLinkedWorktree = isLinked,
        });
    }

    public Result<bool, WorktreeError> LocalBranchExists(string repoRoot, string branch, bool trustRepository)
    {
        var args = new List<string>(WorktreePathRules.RepositoryGitArgs(repoRoot, trustRepository))
        {
            "show-ref",
            "--verify",
            "--quiet",
            "refs/heads/" + branch,
        };
        var run = Run(new WorktreeCommand { Program = _gitExecutable, Args = args }, repoRoot);
        if (run.Ok)
            return Ok(true);
        if (run.ExitCode == 1)
            return Ok(false);
        return Fail<bool>(WorktreeErrorCodes.CreateFailed, run.Error ?? "git show-ref failed", repoRoot);
    }

    public Result<bool, WorktreeError> CheckoutHasDirtyFiles(string checkout, bool trustRepository)
    {
        var args = new List<string>(WorktreePathRules.RepositoryGitArgs(checkout, trustRepository))
        {
            "status",
            "--porcelain",
            "--untracked-files=all",
        };
        var run = Run(new WorktreeCommand { Program = _gitExecutable, Args = args }, checkout);
        if (!run.Ok)
        {
            return Fail<bool>(
                WorktreeErrorCodes.RemoveFailed,
                run.Error ?? "git status failed",
                checkout);
        }

        return Ok(!string.IsNullOrWhiteSpace(run.Stdout));
    }

    public Result<RuntimeUnit, WorktreeError> Add(
        string repoRoot,
        string path,
        string branch,
        string @base,
        bool trustRepository)
    {
        var exists = LocalBranchExists(repoRoot, branch, trustRepository);
        if (!exists.IsOk)
            return Fail<RuntimeUnit>(exists.Error.Code, exists.Error.Message, path, repoRoot);
        var command = exists.Value
            ? WorktreePathRules.BuildAddExistingBranchCommand(repoRoot, path, branch, trustRepository)
            : WorktreePathRules.BuildAddNewBranchCommand(repoRoot, path, branch, @base, trustRepository);
        command = command with { Program = _gitExecutable };
        var run = Run(command, repoRoot, path);
        if (!run.Ok)
        {
            return Fail<RuntimeUnit>(
                WorktreeErrorCodes.CreateFailed,
                run.Error ?? "git worktree add failed",
                path,
                repoRoot);
        }

        return Ok(RuntimeUnit.Value);
    }

    public Result<RuntimeUnit, WorktreeError> Remove(
        string repoRoot,
        string path,
        bool force,
        bool trustRepository)
    {
        if (OperatingSystem.IsWindows() && !force)
        {
            var dirty = CheckoutHasDirtyFiles(path, trustRepository);
            if (!dirty.IsOk)
                return Fail<RuntimeUnit>(dirty.Error.Code, dirty.Error.Message, path, repoRoot);
            if (dirty.Value)
            {
                return Fail<RuntimeUnit>(
                    WorktreeErrorCodes.DirtyRequiresForce,
                    "checkout contains modified or untracked files",
                    path,
                    repoRoot);
            }
        }

        var command = WorktreePathRules.BuildRemoveCommand(repoRoot, path, force, trustRepository)
            with
        { Program = _gitExecutable };
        var run = Run(command, repoRoot, path);
        if (run.Ok)
            return Ok(RuntimeUnit.Value);

        var message = run.Error ?? "git worktree remove failed";
        if (WorktreePathRules.IsDirtyWorktreeRemoveError(message) && !force)
        {
            return Fail<RuntimeUnit>(
                WorktreeErrorCodes.DirtyRequiresForce,
                "checkout contains modified or untracked files",
                path,
                repoRoot);
        }

        if (force && WorktreePathRules.IsNotWorkingTreeRemoveError(message))
            return TryRecoverLeftover(repoRoot, path, trustRepository, message);

        return Fail<RuntimeUnit>(WorktreeErrorCodes.RemoveFailed, message, path, repoRoot);
    }

    public Result<string?, WorktreeError> CommonWorktreesDir(string repoRoot, bool trustRepository)
    {
        var run = RunRevParse(repoRoot, trustRepository, "--git-common-dir");
        if (!run.Ok)
            return Ok<string?>(null);
        var common = ResolveGitPath(repoRoot, (run.Stdout ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(common))
            return Ok<string?>(null);
        return Ok<string?>(Path.Combine(common, "worktrees"));
    }

    // Never delete an unrelated directory.</summary>
    private Result<RuntimeUnit, WorktreeError> TryRecoverLeftover(
        string repoRoot,
        string path,
        bool trustRepository,
        string originalError)
    {
        var listed = List(repoRoot, trustRepository);
        if (!listed.IsOk)
        {
            return Fail<RuntimeUnit>(
                listed.Error.Code,
                listed.Error.Message,
                path,
                repoRoot);
        }

        if (listed.Value.Any(e => WorktreePathRules.PathsEqual(e.Path, path)))
        {
            return Fail<RuntimeUnit>(
                WorktreeErrorCodes.RemoveFailed,
                originalError,
                path,
                repoRoot);
        }

        if (!Directory.Exists(path))
            return Ok(RuntimeUnit.Value);

        if (!LeftoverMatchesRepo(repoRoot, path, trustRepository))
        {
            return Fail<RuntimeUnit>(
                WorktreeErrorCodes.RemoveFailed,
                "refused to delete an unrelated directory",
                path,
                repoRoot);
        }

        try
        {
            Directory.Delete(path, recursive: true);
            return Ok(RuntimeUnit.Value);
        }
        catch (Exception)
        {
            return Fail<RuntimeUnit>(
                WorktreeErrorCodes.RemoveFailed,
                "failed to remove leftover checkout",
                path,
                repoRoot);
        }
    }

    private bool LeftoverMatchesRepo(string repoRoot, string path, bool trustRepository)
    {
        var gitFile = Path.Combine(path, ".git");
        if (!File.Exists(gitFile) || Directory.Exists(gitFile))
            return false;
        string content;
        try
        {
            content = File.ReadAllText(gitFile);
        }
        catch (Exception)
        {
            return false;
        }

        const string prefix = "gitdir:";
        var trimmed = content.Trim();
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var gitdir = trimmed[prefix.Length..].Trim();
        var gitdirPath = Path.IsPathRooted(gitdir) ? gitdir : Path.GetFullPath(Path.Combine(path, gitdir));
        var worktrees = CommonWorktreesDir(repoRoot, trustRepository);
        if (!worktrees.IsOk || string.IsNullOrWhiteSpace(worktrees.Value))
            return false;
        return WorktreePathRules.IsUnderDirectory(gitdirPath, worktrees.Value);
    }

    private GitRun RunRevParse(string cwd, bool trustRepository, string flag)
    {
        var args = new List<string>(WorktreePathRules.RepositoryGitArgs(cwd, trustRepository))
        {
            "rev-parse",
            flag,
        };
        return Run(new WorktreeCommand { Program = _gitExecutable, Args = args }, cwd);
    }

    private static string ResolveGitPath(string cwd, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return cwd;
        return Path.IsPathRooted(value) ? value : Path.GetFullPath(Path.Combine(cwd, value));
    }

    private GitRun Run(WorktreeCommand command, params string[] redactPaths)
    {
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = command.Program,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in command.Args)
                proc.StartInfo.ArgumentList.Add(arg);

            if (!proc.Start())
                return new GitRun { Ok = false, Error = "failed to start git", ExitCode = -1 };

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit((int)_timeout.TotalMilliseconds))
            {
                try
                {
                    if (!proc.HasExited)
                        proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // best-effort
                }

                return new GitRun { Ok = false, Error = "git timed out", ExitCode = -1 };
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (proc.ExitCode != 0)
            {
                var msg = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                if (string.IsNullOrWhiteSpace(msg))
                    msg = "exit " + proc.ExitCode;
                return new GitRun
                {
                    Ok = false,
                    Error = WorktreePathRules.RedactMessage(msg.Trim(), redactPaths),
                    Stdout = stdout,
                    ExitCode = proc.ExitCode,
                };
            }

            return new GitRun { Ok = true, Stdout = stdout, ExitCode = 0 };
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or IOException)
        {
            return new GitRun { Ok = false, Error = "git process failed", ExitCode = -1 };
        }
    }

    private static Result<T, WorktreeError> Ok<T>(T value) => Result<T, WorktreeError>.Ok(value);

    private static Result<T, WorktreeError> Fail<T>(string code, string message, params string[] paths) =>
        Result<T, WorktreeError>.Fail(
            new WorktreeError(code, WorktreePathRules.RedactMessage(message, paths)));

    private sealed class GitRun
    {
        public bool Ok { get; init; }
        public string? Stdout { get; init; }
        public string? Error { get; init; }
        public int ExitCode { get; init; }
    }
}
