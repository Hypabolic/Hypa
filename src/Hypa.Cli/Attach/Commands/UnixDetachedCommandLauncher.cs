using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hypa.AgentRuntime.Application;
using Hypa.Cli.Attach.Keys;

namespace Hypa.Cli.Attach.Commands;

/// <summary>
/// Unix detached custom-command spawn. Stdio is <c>/dev/null</c>.
/// The child is a new session (<c>setsid</c>) so attach hangup does not signal it.
/// </summary>
public sealed class UnixDetachedCommandLauncher : IDetachedCommandLauncher
{
    // Linux: 0x80. macOS 0x80 is POSIX_SPAWN_START_SUSPENDED; SETSID is 0x0400.
    private static short SetsidFlag() =>
        OperatingSystem.IsMacOS() ? (short)0x0400 : (short)0x0080;

    private const int ORdwr = 2;
    private const int ORdonly = 0;
    private const int FSetFd = 2;
    private const int FdCloexec = 1;
    private const int WNoHang = 1;
    private const int Eintr = 4;
    private const int Echild = 10;

    public bool TryStart(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        string? cwd,
        out string? error)
    {
        error = null;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            error = AttachCommandDispatcher.CustomCommandFailed;
            return false;
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            error = AttachCommandDispatcher.CustomCommandFailed;
            return false;
        }

        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);

        return TrySpawnDetached(fileName, arguments, environment, cwd, out error);
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static bool TrySpawnDetached(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        string? cwd,
        out string? error)
    {
        error = null;
        // Linux file_actions/attr are structs. macOS types are opaque pointers.
        // A zeroed blob is valid for both: Darwin writes the pointer at offset 0.
        var actions = Marshal.AllocHGlobal(256);
        var attr = Marshal.AllocHGlobal(512);
        var argv = Array.Empty<IntPtr>();
        var envp = Array.Empty<IntPtr>();
        var nullFd = -1;
        var dirFd = -1;
        var actionsInited = false;
        var attrInited = false;
        try
        {
            Zero(actions, 256);
            Zero(attr, 512);
            if (posix_spawn_file_actions_init(actions) != 0)
            {
                error = AttachCommandDispatcher.CustomCommandFailed;
                return false;
            }

            actionsInited = true;
            if (posix_spawnattr_init(attr) != 0)
            {
                error = AttachCommandDispatcher.CustomCommandFailed;
                return false;
            }

            attrInited = true;

            if (posix_spawnattr_setflags(attr, SetsidFlag()) != 0)
            {
                error = AttachCommandDispatcher.CustomCommandFailed;
                return false;
            }

            nullFd = open("/dev/null", ORdwr);
            if (nullFd < 0)
            {
                error = AttachCommandDispatcher.CustomCommandFailed;
                return false;
            }

            _ = fcntl(nullFd, FSetFd, FdCloexec);
            if (posix_spawn_file_actions_adddup2(actions, nullFd, 0) != 0
                || posix_spawn_file_actions_adddup2(actions, nullFd, 1) != 0
                || posix_spawn_file_actions_adddup2(actions, nullFd, 2) != 0
                || posix_spawn_file_actions_addclose(actions, nullFd) != 0)
            {
                error = AttachCommandDispatcher.CustomCommandFailed;
                return false;
            }

            if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
            {
                dirFd = open(cwd, ORdonly | DirectoryOpenFlag());
                if (dirFd < 0)
                {
                    error = AttachCommandDispatcher.CustomCommandFailed;
                    return false;
                }

                _ = fcntl(dirFd, FSetFd, FdCloexec);
                if (!TryAddFchdir(actions, dirFd)
                    || posix_spawn_file_actions_addclose(actions, dirFd) != 0)
                {
                    error = AttachCommandDispatcher.CustomCommandFailed;
                    return false;
                }
            }

            argv = BuildArgv(fileName, arguments);
            envp = BuildEnvp(environment);
            var rc = posix_spawnp(out var pid, fileName, actions, attr, argv, envp);
            if (rc != 0 || pid <= 0)
            {
                error = AttachCommandDispatcher.CustomCommandFailed;
                return false;
            }

            ReapLater(pid);
            return true;
        }
        catch
        {
            error = AttachCommandDispatcher.CustomCommandFailed;
            return false;
        }
        finally
        {
            if (actionsInited)
                _ = posix_spawn_file_actions_destroy(actions);
            if (attrInited)
                _ = posix_spawnattr_destroy(attr);
            Marshal.FreeHGlobal(actions);
            Marshal.FreeHGlobal(attr);
            FreeBlock(argv);
            FreeBlock(envp);
            if (nullFd >= 0)
                _ = close(nullFd);
            if (dirFd >= 0)
                _ = close(dirFd);
        }
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static bool TryAddFchdir(IntPtr actions, int fd)
    {
        try
        {
            if (posix_spawn_file_actions_addfchdir(actions, fd) == 0)
                return true;
        }
        catch (EntryPointNotFoundException)
        {
            // older macOS exports only the _np symbol
        }

        try
        {
            return posix_spawn_file_actions_addfchdir_np(actions, fd) == 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static int DirectoryOpenFlag()
    {
        if (OperatingSystem.IsMacOS())
            return 0x100000;
        // Linux x86 and x86-64 use 0x10000. The other ABIs, such as arm64, use 0x4000.
        return RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86
            ? 0x10000
            : 0x4000;
    }

    private static void ReapLater(int pid)
    {
        while (true)
        {
            var rc = waitpid(pid, out _, WNoHang);
            if (rc == pid)
                return;
            if (rc < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno == Eintr)
                    continue;
                if (errno == Echild)
                    return;
                return;
            }

            break;
        }

        _ = Task.Factory.StartNew(
            () => WaitUntilReaped(pid),
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    private static void WaitUntilReaped(int pid)
    {
        while (true)
        {
            var rc = waitpid(pid, out _, 0);
            if (rc == pid)
                return;
            if (rc < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno == Eintr)
                    continue;
                if (errno == Echild)
                    return;
                return;
            }
        }
    }

    private static IntPtr[] BuildArgv(string fileName, IReadOnlyList<string> arguments)
    {
        var argv = new IntPtr[arguments.Count + 2];
        argv[0] = Marshal.StringToHGlobalAnsi(fileName);
        for (var i = 0; i < arguments.Count; i++)
            argv[i + 1] = Marshal.StringToHGlobalAnsi(arguments[i]);
        argv[^1] = IntPtr.Zero;
        return argv;
    }

    private static IntPtr[] BuildEnvp(IReadOnlyDictionary<string, string> overlay)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key || entry.Value is not string value)
                continue;
            if (IsForbiddenKey(key))
                continue;
            map[key] = value;
        }

        foreach (var kv in overlay)
        {
            if (IsForbiddenKey(kv.Key))
                continue;
            map[kv.Key] = kv.Value;
        }

        var envp = new IntPtr[map.Count + 1];
        var i = 0;
        foreach (var kv in map)
            envp[i++] = Marshal.StringToHGlobalAnsi(kv.Key + "=" + kv.Value);
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

    private static void Zero(IntPtr ptr, int length)
    {
        for (var i = 0; i < length; i++)
            Marshal.WriteByte(ptr, i, 0);
    }

    private static bool IsForbiddenKey(string key) =>
        key.StartsWith("HERDR_", StringComparison.Ordinal)
        || PaneIdEnvironment.IsPaneIdKey(key);

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_init(IntPtr fileActions);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_destroy(IntPtr fileActions);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_adddup2(
        IntPtr fileActions, int fildes, int newFildes);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_addclose(IntPtr fileActions, int fildes);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawn_file_actions_addfchdir(IntPtr fileActions, int fd);

    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addfchdir_np", SetLastError = true)]
    private static extern int posix_spawn_file_actions_addfchdir_np(IntPtr fileActions, int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnattr_init(IntPtr attr);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnattr_destroy(IntPtr attr);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnattr_setflags(IntPtr attr, short flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnp(
        out int pid,
        string file,
        IntPtr fileActions,
        IntPtr attrp,
        IntPtr[] argv,
        IntPtr[] envp);
}
