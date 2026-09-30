using System.Diagnostics;
using Hypa.AgentRuntime.Application;

namespace Hypa.AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// Production git HEAD/dirty probe via <c>git rev-parse</c> and <c>git status --porcelain</c>.
/// Non-git directories return complete (null head). Probe failure sets Incomplete.
/// </summary>
public sealed class ProcessGitWorkspaceProbe : IGitWorkspaceProbe
{
    private readonly string _gitExecutable;
    private readonly TimeSpan _timeout;
    private readonly IGitProbeHooks _hooks;

    public ProcessGitWorkspaceProbe(
        string? gitExecutable = null,
        TimeSpan? timeout = null,
        IGitProbeHooks? hooks = null)
    {
        _gitExecutable = string.IsNullOrWhiteSpace(gitExecutable) ? "git" : gitExecutable;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
        _hooks = hooks ?? NoOpGitProbeHooks.Instance;
    }

    public async Task<RuntimeResult<GitWorkspaceProbeResult>> ProbeAsync(
        string projectRoot,
        CancellationToken ct = default,
        ulong? expectedDevice = null,
        ulong? expectedInode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        // Never Directory.Exists: that follows a swapped symlink. Kernel fd paths
        // name the held inode and must still fstat against the pin.
        if (!TryTrustProbeRoot(projectRoot, expectedDevice, expectedInode, out var trustWarning))
        {
            return RuntimeResult<GitWorkspaceProbeResult>.Ok(new GitWorkspaceProbeResult
            {
                Incomplete = true,
                Warning = trustWarning,
            });
        }

        // Last path/fd check immediately before spawn. BeforeProcessStartForTest
        // runs after this and before posix_spawn chdir.
        if (!TryTrustProbeRoot(projectRoot, expectedDevice, expectedInode, out var preSpawnWarning))
        {
            return RuntimeResult<GitWorkspaceProbeResult>.Ok(new GitWorkspaceProbeResult
            {
                Incomplete = true,
                Warning = preSpawnWarning,
            });
        }

        // Inside work tree? Prefer git's own answer over .git path heuristics.
        var inside = await RunGitAsync(
            projectRoot, ["rev-parse", "--is-inside-work-tree"], ct).ConfigureAwait(false);
        if (!inside.Ok)
        {
            // Not a git repo (or git missing) → complete without HEAD; workspace walk covers files.
            if (IsNotAGitRepository(inside.Error))
            {
                return DiscardHeadUnlessTrusted(
                    projectRoot, expectedDevice, expectedInode,
                    new GitWorkspaceProbeResult
                    {
                        Head = null,
                        Dirty = null,
                        Incomplete = false,
                    });
            }

            return DiscardHeadUnlessTrusted(
                projectRoot, expectedDevice, expectedInode,
                new GitWorkspaceProbeResult
                {
                    Incomplete = true,
                    Warning = "rev-parse --is-inside-work-tree failed: " + (inside.Error ?? "unknown"),
                });
        }

        var insideText = (inside.Stdout ?? string.Empty).Trim();
        if (!string.Equals(insideText, "true", StringComparison.OrdinalIgnoreCase))
        {
            return DiscardHeadUnlessTrusted(
                projectRoot, expectedDevice, expectedInode,
                new GitWorkspaceProbeResult
                {
                    Head = null,
                    Dirty = null,
                    Incomplete = false,
                });
        }

        var head = await RunGitAsync(projectRoot, ["rev-parse", "HEAD"], ct).ConfigureAwait(false);
        if (!head.Ok)
        {
            return DiscardHeadUnlessTrusted(
                projectRoot, expectedDevice, expectedInode,
                new GitWorkspaceProbeResult
                {
                    Incomplete = true,
                    Warning = "rev-parse HEAD failed: " + (head.Error ?? "unknown"),
                });
        }

        var headSha = head.Stdout?.Trim();
        if (string.IsNullOrWhiteSpace(headSha) || headSha.Length < 7)
        {
            return DiscardHeadUnlessTrusted(
                projectRoot, expectedDevice, expectedInode,
                new GitWorkspaceProbeResult
                {
                    Incomplete = true,
                    Warning = "rev-parse HEAD returned empty",
                });
        }

        var status = await RunGitAsync(
            projectRoot, ["status", "--porcelain"], ct).ConfigureAwait(false);
        if (!status.Ok)
        {
            return DiscardHeadUnlessTrusted(
                projectRoot, expectedDevice, expectedInode,
                new GitWorkspaceProbeResult
                {
                    Head = headSha,
                    Dirty = null,
                    Incomplete = true,
                    Warning = "status --porcelain failed: " + (status.Error ?? "unknown"),
                });
        }

        var dirty = !string.IsNullOrWhiteSpace(status.Stdout);
        return DiscardHeadUnlessTrusted(
            projectRoot, expectedDevice, expectedInode,
            new GitWorkspaceProbeResult
            {
                Head = headSha,
                Dirty = dirty,
                Incomplete = false,
            });
    }

    private static RuntimeResult<GitWorkspaceProbeResult> DiscardHeadUnlessTrusted(
        string projectRoot,
        ulong? expectedDevice,
        ulong? expectedInode,
        GitWorkspaceProbeResult result)
    {
        if (TryTrustProbeRoot(projectRoot, expectedDevice, expectedInode, out var postWarning))
            return RuntimeResult<GitWorkspaceProbeResult>.Ok(result);

        return RuntimeResult<GitWorkspaceProbeResult>.Ok(new GitWorkspaceProbeResult
        {
            Head = null,
            Dirty = null,
            Incomplete = true,
            Warning = postWarning,
        });
    }

    private static bool TryTrustProbeRoot(
        string projectRoot,
        ulong? expectedDevice,
        ulong? expectedInode,
        out string warning)
    {
        warning = "project_root is not a directory";

        // Kernel fd paths are magic symlinks. lstat would refuse them. fstat the
        // held descriptor and compare the pin. Never trust the path string alone.
        if (NoFollowWorkspaceWalker.IsKernelFdDirectoryPath(projectRoot))
        {
            if (!NoFollowWorkspaceWalker.TryFstatKernelFdDirectoryPath(
                    projectRoot, out var fdId, out var fdDetail))
            {
                warning = "project_root kernel fd fstat failed"
                    + (string.IsNullOrEmpty(fdDetail) ? "" : ": " + fdDetail);
                return false;
            }

            if (expectedDevice is ulong kdev && expectedInode is ulong kino
                && (fdId.Device != kdev || fdId.Inode != kino))
            {
                warning = "project_root is not the prepared inode";
                return false;
            }

            return true;
        }

        if (!NoFollowWorkspaceWalker.TryClassifyWalkRoot(projectRoot, out var isLink, out var detail))
        {
            warning = "project_root is not a directory"
                + (string.IsNullOrEmpty(detail) ? "" : ": " + detail);
            return false;
        }

        if (isLink)
        {
            warning = "project_root is a symlink; refuse follow";
            return false;
        }

        if (!NoFollowWorkspaceWalker.TryLstatIdentity(projectRoot, out var id, out var nowLink, out detail))
        {
            warning = "project_root lstat failed"
                + (string.IsNullOrEmpty(detail) ? "" : ": " + detail);
            return false;
        }

        if (nowLink)
        {
            warning = "project_root is a symlink; refuse follow";
            return false;
        }

        if (expectedDevice is ulong dev && expectedInode is ulong ino
            && (id.Device != dev || id.Inode != ino))
        {
            warning = "project_root is not the prepared inode";
            return false;
        }

        return true;
    }

    private static bool IsNotAGitRepository(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return false;
        // git writes "not a git repository" to stderr when cwd is outside a work tree.
        // Missing git binary is Incomplete (handled by the non-matching path above).
        return error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<GitCommandResult> RunGitAsync(
        string workingDirectory,
        string[] args,
        CancellationToken ct)
    {
        try
        {
            _hooks.BeforeProcessStart(workingDirectory);

            // macOS /dev/fd/N is not a chdir target. fchdir the pinned fd in the child.
            if ((OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
                && NoFollowWorkspaceWalker.TryParseKernelFdDirectoryPath(workingDirectory, out var dirFd))
            {
                var spawned = await UnixPinnedDirectoryGit.RunAsync(
                    _gitExecutable, dirFd, args, _timeout, ct).ConfigureAwait(false);
                return new GitCommandResult
                {
                    Ok = spawned.Ok,
                    Stdout = spawned.Stdout,
                    Error = spawned.Error,
                };
            }

            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = _gitExecutable,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
                proc.StartInfo.ArgumentList.Add(a);

            // Probe only: rev-parse / status. Never -c or --git-dir.
            // Drop GIT_DIR and related overrides so cwd/pin wins.
            GitProbeEnvironment.ApplyTo(proc.StartInfo.Environment);

            if (!proc.Start())
            {
                return new GitCommandResult
                {
                    Ok = false,
                    Error = "failed to start git",
                };
            }

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
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

                return new GitCommandResult
                {
                    Ok = false,
                    Error = "git timed out",
                };
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (proc.ExitCode != 0)
            {
                var msg = string.IsNullOrWhiteSpace(stderr) ? $"exit {proc.ExitCode}" : stderr.Trim();
                return new GitCommandResult { Ok = false, Error = msg, Stdout = stdout };
            }

            return new GitCommandResult { Ok = true, Stdout = stdout };
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or IOException)
        {
            return new GitCommandResult
            {
                Ok = false,
                Error = ex.Message,
            };
        }
    }

    private sealed class GitCommandResult
    {
        public bool Ok { get; init; }
        public string? Stdout { get; init; }
        public string? Error { get; init; }
    }
}
