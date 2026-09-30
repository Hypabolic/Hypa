using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Hypa.Cli.Mux;

/// <summary>
/// Verifies a status-file PID is a live hypa mux before SIGTERM.
/// Fail closed: unknown or dead processes are not killed.
/// </summary>
internal static class MuxProcessIdentity
{
    public static bool IsAlive(int pid)
    {
        if (pid <= 0)
            return false;

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static bool IsMuxServerProcess(int pid)
    {
        if (!TryAcquireMuxServer(pid, out var lease) || lease is null)
            return false;

        lease.Dispose();
        return true;
    }

    /// <summary>
    /// Opens a kill lease only when the PID is a live mux and a stable
    /// identity (start time, and a held pidfd on Linux) can be held.
    /// </summary>
    public static bool TryAcquireMuxServer(int pid, out MuxProcessLease? lease) =>
        TryAcquireMuxServer(pid, MuxPidFdPolicy.ForCurrentPlatform(), out lease);

    /// <summary>
    /// Test seam: Linux requires a pidfd. A failed open must not create a lease.
    /// </summary>
    internal static bool TryAcquireMuxServer(
        int pid,
        MuxPidFdPolicy pidFdPolicy,
        out MuxProcessLease? lease)
    {
        lease = null;
        if (pid <= 0)
            return false;

        Process? process = null;
        var pidfd = -1;
        try
        {
            process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                process.Dispose();
                return false;
            }

            if (!TryReadStartTimeUtc(process, out var startTimeUtc))
            {
                process.Dispose();
                return false;
            }

            if (pidFdPolicy.Required)
            {
                pidfd = pidFdPolicy.OpenPidFd?.Invoke(pid) ?? -1;
                if (pidfd < 0)
                {
                    process.Dispose();
                    return false;
                }
            }

            var argv = TryReadArgv(pid);
            if (argv is null || !LooksLikeMux(argv))
            {
                ClosePidFd(pidfd);
                process.Dispose();
                return false;
            }

            if (!HasStableIdentity(pid, startTimeUtc))
            {
                ClosePidFd(pidfd);
                process.Dispose();
                return false;
            }

            lease = new MuxProcessLease(
                process,
                pid,
                startTimeUtc,
                pidfd,
                requirePidFd: pidFdPolicy.Required);
            return true;
        }
        catch (ArgumentException)
        {
            ClosePidFd(pidfd);
            process?.Dispose();
            return false;
        }
        catch (InvalidOperationException)
        {
            ClosePidFd(pidfd);
            process?.Dispose();
            return false;
        }
    }

    internal static bool LooksLikeMux(string? processName, string? commandLine)
    {
        _ = processName;
        // Fail closed: a process name of hypa or hypa-runtime is not enough.
        // Recycled attach/doctor/-c PIDs and non-serving internal aliases
        // share those names. Refuse when argv is unread or is not mux serve.
        if (string.IsNullOrWhiteSpace(commandLine))
            return false;

        return LooksLikeMux(SplitArgs(commandLine));
    }

    /// <summary>
    /// Mux serve is a recognized host followed immediately by <c>mux</c>
    /// then <c>serve</c>. Compression text that contains those words, or a
    /// flattened <c>-c</c>/<c>-t</c>/doctor/attach form, is not mux serve.
    /// </summary>
    internal static bool LooksLikeMux(IReadOnlyList<string> argv)
    {
        if (argv.Count < 3)
            return false;

        var host = IndexOfMuxHost(argv);
        if (host < 0 || host + 2 >= argv.Count)
            return false;

        return argv[host + 1].Equals("mux", StringComparison.OrdinalIgnoreCase)
            && argv[host + 2].Equals("serve", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryReadStartTimeUtc(int pid, out DateTime startTimeUtc)
    {
        startTimeUtc = default;
        if (pid <= 0)
            return false;

        try
        {
            using var process = Process.GetProcessById(pid);
            return TryReadStartTimeUtc(process, out startTimeUtc);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool HasStableIdentity(int pid, DateTime expectedStartTimeUtc)
    {
        if (!TryReadStartTimeUtc(pid, out var current))
            return false;

        return current == expectedStartTimeUtc;
    }

    internal static IReadOnlyList<string>? TryReadArgv(int pid)
    {
        if (pid <= 0)
            return null;

        if (OperatingSystem.IsLinux())
        {
            var linux = TryReadLinuxArgv(pid);
            if (linux is not null)
                return linux;
        }

        if (OperatingSystem.IsMacOS())
        {
            var mac = TryReadMacArgv(pid);
            if (mac is not null)
                return mac;
        }

        var flattened = TryRunPs(pid, "args=");
        return string.IsNullOrWhiteSpace(flattened) ? null : SplitArgs(flattened);
    }

    internal static (string? Name, string? Command) ReadIdentity(int pid)
    {
        string? name = null;
        try
        {
            using var process = Process.GetProcessById(pid);
            name = process.ProcessName;
        }
        catch
        {
            // fall through to comm/ps
        }

        var comm = TryReadComm(pid);
        if (!string.IsNullOrWhiteSpace(comm))
            name = comm.Trim();

        var argv = TryReadArgv(pid);
        var command = argv is null || argv.Count == 0 ? null : string.Join(' ', argv);
        return (name, command);
    }

    private static bool TryReadStartTimeUtc(Process process, out DateTime startTimeUtc)
    {
        startTimeUtc = default;
        try
        {
            if (process.HasExited)
                return false;

            startTimeUtc = process.StartTime.ToUniversalTime();
            return startTimeUtc != default;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static int IndexOfMuxHost(IReadOnlyList<string> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            var name = Path.GetFileName(tokens[i].Trim('"', '\''));
            if (IsMuxHostFile(name))
                return i;
        }

        return -1;
    }

    private static bool IsMuxHostFile(string fileName) =>
        fileName.Equals("hypa", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("hypa.exe", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("hypa.dll", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("hypa-runtime", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("hypa-runtime.exe", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("hypa-runtime.dll", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("Hypa.AgentServer", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("Hypa.AgentServer.exe", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("Hypa.AgentServer.dll", StringComparison.OrdinalIgnoreCase);

    internal static List<string> SplitArgs(string commandLine)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        foreach (var ch in commandLine)
        {
            if (quote is { } q)
            {
                if (ch == q)
                    quote = null;
                else
                    current.Append(ch);
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }

    private static string? TryReadComm(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var path = $"/proc/{pid}/comm";
                if (File.Exists(path))
                    return File.ReadAllText(path).Trim();
            }
            catch
            {
                return null;
            }
        }

        return TryRunPs(pid, "comm=");
    }

    [SupportedOSPlatform("linux")]
    private static IReadOnlyList<string>? TryReadLinuxArgv(int pid)
    {
        try
        {
            var path = $"/proc/{pid}/cmdline";
            if (!File.Exists(path))
                return null;

            // Keep NUL boundaries. Do not flatten to spaces.
            var raw = File.ReadAllBytes(path);
            var argv = SplitNulTerminated(raw);
            return argv.Count == 0 ? null : argv;
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("macos")]
    private static IReadOnlyList<string>? TryReadMacArgv(int pid)
    {
        try
        {
            int[] mib = [Native.CtlKern, Native.KernProcArgs2, pid];
            var buffer = new byte[Native.MacArgvBufferBytes];
            nuint size = (nuint)buffer.Length;
            if (Native.Sysctl(mib, (uint)mib.Length, buffer, ref size, IntPtr.Zero, 0) != 0)
                return null;

            if (size == 0 || size > (nuint)buffer.Length)
                return null;

            var argv = ParseKernProcArgs2(buffer.AsSpan(0, (int)size));
            return argv is { Count: > 0 } ? argv : null;
        }
        catch
        {
            return null;
        }
    }

    internal static List<string>? ParseKernProcArgs2(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 4)
            return null;

        var argc = BitConverter.ToInt32(buffer);
        if (argc <= 0 || argc > 1024)
            return null;

        var offset = 4;
        while (offset < buffer.Length && buffer[offset] != 0)
            offset++;
        if (offset >= buffer.Length)
            return null;

        offset++;
        while (offset < buffer.Length && buffer[offset] == 0)
            offset++;

        var argv = new List<string>(argc);
        for (var n = 0; n < argc && offset < buffer.Length; n++)
        {
            var start = offset;
            while (offset < buffer.Length && buffer[offset] != 0)
                offset++;
            argv.Add(Encoding.UTF8.GetString(buffer.Slice(start, offset - start)));
            offset++;
        }

        return argv.Count == argc ? argv : null;
    }

    internal static List<string> SplitNulTerminated(byte[] raw)
    {
        var tokens = new List<string>();
        var start = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != 0)
                continue;

            if (i > start)
                tokens.Add(Encoding.UTF8.GetString(raw, start, i - start));
            start = i + 1;
        }

        if (start < raw.Length)
            tokens.Add(Encoding.UTF8.GetString(raw, start, raw.Length - start));

        return tokens;
    }

    private static string? TryRunPs(int pid, string format)
    {
        try
        {
            using var ps = Process.Start(new ProcessStartInfo
            {
                FileName = "ps",
                ArgumentList = { "-p", pid.ToString(), "-o", format },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (ps is null)
                return null;
            var output = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit(1000);
            return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("linux")]
    private static int TryOpenPidFd(int pid)
    {
        try
        {
            var fd = Native.SyscallPidfdOpen(Native.PidfdOpen, pid, 0);
            return fd >= 0 ? fd : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static void ClosePidFd(int pidfd)
    {
        if (pidfd < 0)
            return;

        try
        {
            _ = Native.Close(pidfd);
        }
        catch
        {
            // best-effort
        }
    }

    internal readonly record struct MuxPidFdPolicy(bool Required, Func<int, int>? OpenPidFd)
    {
        public static MuxPidFdPolicy ForCurrentPlatform()
        {
            if (OperatingSystem.IsLinux())
                return new MuxPidFdPolicy(Required: true, OpenPidFd: TryOpenPidFd);

            return new MuxPidFdPolicy(Required: false, OpenPidFd: null);
        }

        public static MuxPidFdPolicy LinuxRequiredUnavailable { get; } =
            new(Required: true, OpenPidFd: static _ => -1);
    }

    internal sealed class MuxProcessLease : IDisposable
    {
        private readonly Process _process;
        private readonly DateTime _startTimeUtc;
        private readonly int _pidfd;
        private readonly bool _requirePidFd;
        private bool _disposed;

        internal MuxProcessLease(
            Process process,
            int pid,
            DateTime startTimeUtc,
            int pidfd,
            bool requirePidFd)
        {
            _process = process;
            Pid = pid;
            _startTimeUtc = startTimeUtc;
            _pidfd = pidfd;
            _requirePidFd = requirePidFd;
        }

        public int Pid { get; }

        internal DateTime StartTimeUtc => _startTimeUtc;

        internal bool HoldsPidFd => _pidfd >= 0;

        internal bool MatchesCapturedStartTime() => HasStableIdentity(Pid, _startTimeUtc);

        public bool TryTerminate(out string error)
        {
            error = string.Empty;
            if (_disposed)
            {
                error = $"mux pid {Pid} lease already released.";
                return false;
            }

            if (_requirePidFd)
            {
                if (_pidfd < 0)
                {
                    error = $"Status pid {Pid} has no pidfd. Not killing.";
                    return false;
                }

                if (!OperatingSystem.IsLinux())
                {
                    error = $"Status pid {Pid} requires pidfd delivery. Not killing.";
                    return false;
                }

                return TrySendTermViaPidFd(out error);
            }

            if (!HasStableIdentity(Pid, _startTimeUtc))
            {
                error = $"Status pid {Pid} identity changed or vanished before SIGTERM. Not killing.";
                return false;
            }

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    if (_process.HasExited)
                        return true;

                    _process.Kill(entireProcessTree: false);
                    return true;
                }

                return TrySendTermViaKill(out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        [SupportedOSPlatform("linux")]
        private bool TrySendTermViaPidFd(out string error)
        {
            error = string.Empty;
            try
            {
                var rc = Native.SyscallPidfdSendSignal(
                    Native.PidfdSendSignal,
                    _pidfd,
                    Native.SigTerm,
                    IntPtr.Zero,
                    0);
                if (rc == 0)
                    return true;

                var errno = Marshal.GetLastPInvokeError();
                if (errno == Native.Esrch)
                    return true;

                error = $"pidfd_send_signal({Pid}) failed (errno {errno}).";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private bool TrySendTermViaKill(out string error)
        {
            error = string.Empty;
            using var kill = Process.Start(new ProcessStartInfo
            {
                FileName = "kill",
                ArgumentList = { "-TERM", Pid.ToString() },
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });
            if (kill is null)
            {
                error = $"Could not start kill for pid {Pid}.";
                return false;
            }

            kill.WaitForExit(2000);
            if (kill.ExitCode == 0)
                return true;

            if (!IsAlive(Pid))
                return true;

            var err = kill.StandardError.ReadToEnd();
            error = $"kill -TERM {Pid} failed (exit {kill.ExitCode}). {err.Trim()}";
            return false;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            ClosePidFd(_pidfd);
            _process.Dispose();
        }
    }

    private static class Native
    {
        public const int SigTerm = 15;
        public const int Esrch = 3;
        public const long PidfdOpen = 434;
        public const long PidfdSendSignal = 424;
        public const int CtlKern = 1;
        public const int KernProcArgs2 = 49;
        public const int MacArgvBufferBytes = 256 * 1024;

        [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
        public static extern int SyscallPidfdOpen(long number, int pid, uint flags);

        [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
        public static extern int SyscallPidfdSendSignal(
            long number,
            int pidfd,
            int sig,
            IntPtr info,
            uint flags);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        public static extern int Close(int fd);

        [DllImport("libc", EntryPoint = "sysctl", SetLastError = true)]
        public static extern int Sysctl(
            int[] name,
            uint namelen,
            byte[]? oldp,
            ref nuint oldlenp,
            IntPtr newp,
            nuint newlen);
    }
}
