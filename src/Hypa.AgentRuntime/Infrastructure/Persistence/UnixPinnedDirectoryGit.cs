using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Hypa.AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// Spawn git with <c>posix_spawn_file_actions_addfchdir</c> so cwd is the pinned
/// directory fd. macOS <c>/dev/fd/N</c> is not a chdir target (ENOTDIR).
/// </summary>
internal static class UnixPinnedDirectoryGit
{
    internal static async Task<(bool Ok, string? Stdout, string? Error)> RunAsync(
        string gitExecutable,
        int directoryFd,
        string[] args,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (directoryFd < 0)
            return (false, null, "invalid directory fd");

        if (!TryCreatePipe(out var stdoutRead, out var stdoutWrite, out var pipeDetail))
            return (false, null, pipeDetail ?? "pipe failed");
        if (!TryCreatePipe(out var stderrRead, out var stderrWrite, out pipeDetail))
        {
            stdoutRead.Dispose();
            stdoutWrite.Dispose();
            return (false, null, pipeDetail ?? "pipe failed");
        }

        var actions = IntPtr.Zero;
        var argv = Array.Empty<IntPtr>();
        var envp = Array.Empty<IntPtr>();
        var pid = 0;
        var spawned = false;
        try
        {
            if (posix_spawn_file_actions_init(ref actions) != 0)
                return (false, null, "posix_spawn_file_actions_init failed");

            if (!TryAddFchdir(ref actions, directoryFd, out var fchdirDetail))
                return (false, null, fchdirDetail);

            var stdoutReadFd = stdoutRead.DangerousGetHandle().ToInt32();
            var stdoutWriteFd = stdoutWrite.DangerousGetHandle().ToInt32();
            var stderrReadFd = stderrRead.DangerousGetHandle().ToInt32();
            var stderrWriteFd = stderrWrite.DangerousGetHandle().ToInt32();
            if (posix_spawn_file_actions_adddup2(ref actions, stdoutWriteFd, 1) != 0
                || posix_spawn_file_actions_adddup2(ref actions, stderrWriteFd, 2) != 0
                || posix_spawn_file_actions_addclose(ref actions, stdoutReadFd) != 0
                || posix_spawn_file_actions_addclose(ref actions, stderrReadFd) != 0
                || posix_spawn_file_actions_addclose(ref actions, stdoutWriteFd) != 0
                || posix_spawn_file_actions_addclose(ref actions, stderrWriteFd) != 0)
            {
                return (false, null, "posix_spawn file actions failed");
            }

            argv = BuildArgv(gitExecutable, args);
            envp = BuildEnvp();
            var rc = posix_spawnp(out pid, gitExecutable, ref actions, IntPtr.Zero, argv, envp);
            if (rc != 0)
                return (false, null, "posix_spawnp failed errno=" + rc);
            spawned = true;
        }
        finally
        {
            if (actions != IntPtr.Zero)
                _ = posix_spawn_file_actions_destroy(ref actions);
            FreeBlock(argv);
            FreeBlock(envp);
            stdoutWrite.Dispose();
            stderrWrite.Dispose();
            if (!spawned)
            {
                stdoutRead.Dispose();
                stderrRead.Dispose();
            }
        }

        if (!spawned)
            return (false, null, "posix_spawnp failed");

        try
        {
            await using var stdoutStream = new FileStream(stdoutRead, FileAccess.Read);
            await using var stderrStream = new FileStream(stderrRead, FileAccess.Read);
            using var stdoutReader = new StreamReader(stdoutStream, Encoding.UTF8, leaveOpen: false);
            using var stderrReader = new StreamReader(stderrStream, Encoding.UTF8, leaveOpen: false);

            var stdoutTask = stdoutReader.ReadToEndAsync(ct);
            var stderrTask = stderrReader.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            var exit = await WaitPidAsync(pid, timeoutCts.Token).ConfigureAwait(false);
            if (exit is null)
            {
                Reap(pid);
                return (false, null, "git timed out");
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (exit.Value != 0)
            {
                var msg = string.IsNullOrWhiteSpace(stderr) ? $"exit {exit.Value}" : stderr.Trim();
                return (false, stdout, msg);
            }

            return (true, stdout, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Reap(pid);
            return (false, null, "git timed out");
        }
        finally
        {
            if (pid > 0)
            {
                var left = waitpid(pid, out _, WNoHang);
                if (left == 0)
                    Reap(pid);
            }
        }
    }

    private static bool TryAddFchdir(ref IntPtr actions, int fd, out string detail)
    {
        detail = "";
        try
        {
            if (posix_spawn_file_actions_addfchdir(ref actions, fd) == 0)
                return true;
        }
        catch (EntryPointNotFoundException)
        {
            // older macOS exports only the _np symbol
        }

        try
        {
            if (posix_spawn_file_actions_addfchdir_np(ref actions, fd) == 0)
                return true;
        }
        catch (EntryPointNotFoundException)
        {
            detail = "posix_spawn_file_actions_addfchdir unavailable";
            return false;
        }

        detail = "posix_spawn_file_actions_addfchdir failed";
        return false;
    }

    private static void Reap(int pid)
    {
        _ = kill(pid, SigKill);
        _ = waitpid(pid, out _, 0);
    }

    private static bool TryCreatePipe(
        out SafeFileHandle readHandle, out SafeFileHandle writeHandle, out string? detail)
    {
        readHandle = new SafeFileHandle(new IntPtr(-1), ownsHandle: true);
        writeHandle = new SafeFileHandle(new IntPtr(-1), ownsHandle: true);
        detail = null;
        var fds = new int[2];
        if (pipe(fds) != 0)
        {
            detail = "pipe errno=" + Marshal.GetLastPInvokeError();
            return false;
        }

        _ = fcntl(fds[0], F_SetFd, FdCloexec);
        _ = fcntl(fds[1], F_SetFd, FdCloexec);
        readHandle = new SafeFileHandle((IntPtr)fds[0], ownsHandle: true);
        writeHandle = new SafeFileHandle((IntPtr)fds[1], ownsHandle: true);
        return true;
    }

    private static async Task<int?> WaitPidAsync(int pid, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var waited = waitpid(pid, out var status, WNoHang);
            if (waited == pid)
                return DecodeExit(status);
            if (waited < 0)
                return null;
            try
            {
                await Task.Delay(20, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        return null;
    }

    private static int DecodeExit(int status)
    {
        if ((status & 0x7f) == 0)
            return (status >> 8) & 0xff;
        return 128 + (status & 0x7f);
    }

    private static IntPtr[] BuildArgv(string file, string[] args)
    {
        var argv = new IntPtr[args.Length + 2];
        argv[0] = Marshal.StringToHGlobalAnsi(file);
        for (var i = 0; i < args.Length; i++)
            argv[i + 1] = Marshal.StringToHGlobalAnsi(args[i]);
        argv[^1] = IntPtr.Zero;
        return argv;
    }

    private static IntPtr[] BuildEnvp()
    {
        var pairs = GitProbeEnvironment.BuildParentPairs(Environment.GetEnvironmentVariables());
        var envp = new IntPtr[pairs.Count + 1];
        for (var i = 0; i < pairs.Count; i++)
            envp[i] = Marshal.StringToHGlobalAnsi(pairs[i]);
        envp[^1] = IntPtr.Zero;
        return envp;
    }

    private static void FreeBlock(IntPtr[] block)
    {
        foreach (var p in block)
        {
            if (p != IntPtr.Zero)
                Marshal.FreeHGlobal(p);
        }
    }

    private const int F_SetFd = 2;
    private const int FdCloexec = 1;
    private const int WNoHang = 1;
    private const int SigKill = 9;

    [DllImport("libc", SetLastError = true)]
    private static extern int pipe([In, Out] int[] fds);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_init(ref IntPtr fileActions);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_destroy(ref IntPtr fileActions);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_addfchdir(ref IntPtr fileActions, int fd);

    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addfchdir_np", SetLastError = true)]
    private static extern int posix_spawn_file_actions_addfchdir_np(ref IntPtr fileActions, int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_adddup2(
        ref IntPtr fileActions, int fildes, int newFildes);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_addclose(ref IntPtr fileActions, int fildes);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnp(
        out int pid,
        string file,
        ref IntPtr fileActions,
        IntPtr attrp,
        IntPtr[] argv,
        IntPtr[] envp);
}
