using System.Collections;
using System.Runtime.InteropServices;

namespace Hypa.Cli.Mux;

/// <summary>
// / Replace this process image.
/// roles of one binary. Hypa keeps two OS processes and may exec a lean
/// attach image into the same pid so the profile tracks attach.pid.
/// </summary>
internal static class UnixProcessReplace
{
    /// <summary>
    /// Returns true only when the process image was replaced. <c>execve</c>
    /// does not return on success, so a true return is unreachable in
    /// practice. Failure returns false and writes errno to
    /// <paramref name="errno"/>.
    /// </summary>
    public static bool TryExec(string path, string[] args) =>
        TryExec(path, args, out _);

    internal static bool TryExec(string path, string[] args, out int errno)
    {
        errno = 0;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;
        if (OperatingSystem.IsWindows())
            return false;

        var argv = new string[args.Length + 2];
        argv[0] = path;
        if (args.Length > 0)
            Array.Copy(args, 0, argv, 1, args.Length);
        argv[^1] = null!;

        var env = Environment.GetEnvironmentVariables();
        var envp = new string[env.Count + 1];
        var i = 0;
        foreach (DictionaryEntry entry in env)
            envp[i++] = $"{entry.Key}={entry.Value}";
        envp[^1] = null!;

        if (OperatingSystem.IsMacOS())
            return TryExecMacOs(path, argv, envp, out errno);

        var rc = Execve(path, argv, envp);
        errno = Marshal.GetLastPInvokeError();
        return rc == 0;
    }

    // Darwin keeps SA_SIGINFO on a caught signal that exec resets to SIG_DFL, so
    // the new image starts with sigaction {handler = 0, flags = SA_SIGINFO}. The
    // NativeAOT runtime saves that as its previous SIGUSR1 handler and tail-calls
    // sa_sigaction when SA_SIGINFO is set. The first GC thread-suspension signal
    // then jumps to address 0. SETSIGDEF makes the kernel reset dispositions,
    // flags included, as part of the image replacement.
    private static bool TryExecMacOs(string path, string[] argv, string[] envp, out int errno)
    {
        // Darwin posix_spawnattr_t is an opaque pointer. A zeroed blob is valid.
        const int attrSize = 512;
        var attr = Marshal.AllocHGlobal(attrSize);
        var attrInited = false;
        try
        {
            for (var offset = 0; offset < attrSize; offset += sizeof(long))
                Marshal.WriteInt64(attr, offset, 0);

            errno = posix_spawnattr_init(attr);
            if (errno != 0)
                return false;

            attrInited = true;
            var allSignals = uint.MaxValue; // Darwin sigset_t is a uint32 bit set.
            errno = posix_spawnattr_setsigdefault(attr, ref allSignals);
            if (errno != 0)
                return false;

            errno = posix_spawnattr_setflags(attr, SpawnSetExec | SpawnSetSigDef);
            if (errno != 0)
                return false;

            // POSIX_SPAWN_SETEXEC replaces this process in place and keeps the pid.
            // The return value is the error number, and it only returns on failure.
            errno = posix_spawn(IntPtr.Zero, path, IntPtr.Zero, attr, argv, envp);
            return errno == 0;
        }
        finally
        {
            if (attrInited)
                _ = posix_spawnattr_destroy(attr);
            Marshal.FreeHGlobal(attr);
        }
    }

    private const short SpawnSetSigDef = 0x04;
    private const short SpawnSetExec = 0x40;

    internal static string FormatExecFailure(string path, int errno)
    {
        string detail;
        try
        {
            detail = Marshal.GetPInvokeErrorMessage(errno) ?? $"errno {errno}";
        }
        catch (ArgumentException)
        {
            detail = $"errno {errno}";
        }

        return $"error: execve('{path}') failed (errno {errno}: {detail})";
    }

    [DllImport("libc", EntryPoint = "execve", SetLastError = true)]
    private static extern int Execve(string path, string[] argv, string[] envp);

    [DllImport("libc")]
    private static extern int posix_spawnattr_init(IntPtr attr);

    [DllImport("libc")]
    private static extern int posix_spawnattr_destroy(IntPtr attr);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setflags(IntPtr attr, short flags);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setsigdefault(IntPtr attr, ref uint sigset);

    [DllImport("libc")]
    private static extern int posix_spawn(
        IntPtr pid,
        string path,
        IntPtr fileActions,
        IntPtr attr,
        string[] argv,
        string[] envp);
}
