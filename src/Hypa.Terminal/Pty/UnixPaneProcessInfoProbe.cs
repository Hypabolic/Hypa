using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Infrastructure;

namespace Hypa.Terminal.Pty;

/// <summary>
/// Unix foreground-group probe. Linux reads <c>/proc/&lt;pid&gt;/stat</c> tpgid
/// and walks the process tree for that group. macOS reads <c>proc_pidinfo</c>
/// <c>e_tpgid</c> and lists the group with <c>proc_listpids</c>. Command is
/// the agent kind when any process in the group names an agent. Otherwise
/// it is the group leader basename. Returns null when the group cannot be
/// observed. Never publishes <c>getpgid</c> (that is the shell group).
/// </summary>
public sealed partial class UnixPaneProcessInfoProbe : IPaneProcessInfoProbe
{
    private readonly IAgentPathCanonicalizer _paths;

    public UnixPaneProcessInfoProbe(IAgentPathCanonicalizer paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    public UnixPaneProcessInfoProbe()
        : this(OsAgentPathCanonicalizer.Instance)
    {
    }

    internal const int ProcPidTbsdInfo = 3;
    internal const int ProcBsdInfoSize = 136;
    internal const int ProcBsdInfoCommOffset = 48;
    internal const int ProcBsdInfoCommLength = 16;
    internal const int ProcBsdInfoNameOffset = 64;
    internal const int ProcBsdInfoNameLength = 32;
    internal const int ProcBsdInfoTpgidOffset = 112;

    public int? TryGetForegroundGroup(int shellPid)
    {
        if (shellPid <= 0)
            return null;
        if (OperatingSystem.IsLinux())
            return TryReadLinuxForegroundGroup(shellPid);
        if (OperatingSystem.IsMacOS())
            return TryReadMacForegroundGroup(shellPid);
        return null;
    }

    public PaneForegroundInfo? TryGetForegroundInfo(int shellPid)
    {
        if (shellPid <= 0)
            return null;

        int? group;
        if (OperatingSystem.IsLinux())
            group = TryReadLinuxForegroundGroup(shellPid);
        else if (OperatingSystem.IsMacOS())
            group = TryReadMacForegroundGroup(shellPid);
        else
            return null;

        if (group is not int tpgid)
            return null;

        var job = TryCollectForegroundJob(shellPid, tpgid);
        var command = CommandFromForegroundJob(job, tpgid);
        if (command.Length == 0)
        {
            command = OperatingSystem.IsLinux()
                ? TryReadLinuxCommand(tpgid, _paths)
                : OperatingSystem.IsMacOS()
                    ? TryReadMacPidCommand(tpgid, _paths)
                    : "";
        }

        return new PaneForegroundInfo
        {
            GroupId = tpgid,
            Pid = tpgid,
            Command = command,
            // tpgid == shellPid approximates that without a job walk
            // (PaneRuntime.cs:1943-1944).
            ForegroundIsPaneShell = tpgid == shellPid,
        };
    }

    internal static int? TryReadLinuxForegroundGroup(int pid)
    {
        try
        {
            var path = "/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat";
            if (!File.Exists(path))
                return null;
            return TryParseLinuxStatTpgid(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parse tpgid from a Linux <c>/proc/&lt;pid&gt;/stat</c> body.
    /// After comm: state ppid pgrp session tty_nr tpgid.
    /// </summary>
    internal static int? TryParseLinuxStatTpgid(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        var close = text.LastIndexOf(')');
        if (close < 0 || close + 2 >= text.Length)
            return null;
        var rest = text[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (rest.Length < 6)
            return null;
        if (!int.TryParse(rest[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tpgid))
            return null;
        return tpgid > 0 ? tpgid : null;
    }

    internal static int? TryReadMacForegroundGroup(int pid)
    {
        if (!OperatingSystem.IsMacOS() || pid <= 0)
            return null;
        try
        {
            Span<byte> buffer = stackalloc byte[ProcBsdInfoSize];
            int written;
            unsafe
            {
                fixed (byte* p = buffer)
                {
                    written = proc_pidinfo(pid, ProcPidTbsdInfo, 0, (nint)p, ProcBsdInfoSize);
                }
            }

            if (written < ProcBsdInfoTpgidOffset + sizeof(int))
                return null;
            var tpgid = BinaryPrimitives.ReadInt32LittleEndian(
                buffer.Slice(ProcBsdInfoTpgidOffset, sizeof(int)));
            return tpgid > 0 ? tpgid : null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    internal static int? TryReadMacForeground(int pid, out string command)
    {
        command = "";
        if (!OperatingSystem.IsMacOS() || pid <= 0)
            return null;
        try
        {
            Span<byte> buffer = stackalloc byte[ProcBsdInfoSize];
            int written;
            unsafe
            {
                fixed (byte* p = buffer)
                {
                    written = proc_pidinfo(pid, ProcPidTbsdInfo, 0, (nint)p, ProcBsdInfoSize);
                }
            }

            if (written < ProcBsdInfoTpgidOffset + sizeof(int))
                return null;
            var tpgid = BinaryPrimitives.ReadInt32LittleEndian(
                buffer.Slice(ProcBsdInfoTpgidOffset, sizeof(int)));
            if (tpgid <= 0)
                return null;

            command = ReadMacCommand(tpgid, buffer);
            return tpgid;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    internal static string TryReadLinuxCommand(int pid) =>
        TryReadLinuxCommand(pid, OsAgentPathCanonicalizer.Instance);

    internal static string TryReadLinuxCommand(int pid, IAgentPathCanonicalizer paths)
    {
        if (pid <= 0)
            return "";
        try
        {
            var cmdlinePath = "/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/cmdline";
            if (File.Exists(cmdlinePath))
            {
                var named = TryParseLinuxCmdline(File.ReadAllBytes(cmdlinePath), paths);
                if (!string.IsNullOrWhiteSpace(named))
                    return named;
            }

            var commPath = "/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/comm";
            if (File.Exists(commPath))
                return File.ReadAllText(commPath).Trim();
        }
        catch (IOException)
        {
            return "";
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }

        return "";
    }

    /// <summary>
    /// Name the command that owns the foreground group. Argv0 basename, or
    /// the agent kind when a wrapper script path names an agent.
    /// </summary>
    internal static string TryParseLinuxCmdline(byte[]? raw) =>
        TryParseLinuxCmdline(raw, OsAgentPathCanonicalizer.Instance);

    internal static string TryParseLinuxCmdline(byte[]? raw, IAgentPathCanonicalizer paths)
    {
        if (raw is null || raw.Length == 0)
            return "";
        var parts = Encoding.UTF8.GetString(raw).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return CommandNameFromArgv(parts, paths);
    }

    /// <summary>
    /// Map argv to the occupant command. Node, bun, python, and shell
    /// wrappers resolve through <see cref="AgentProcessResolver"/>.
    /// </summary>
    internal static string CommandNameFromArgv(IReadOnlyList<string>? parts) =>
        CommandNameFromArgv(parts, OsAgentPathCanonicalizer.Instance);

    internal static string CommandNameFromArgv(
        IReadOnlyList<string>? parts,
        IAgentPathCanonicalizer paths)
    {
        if (parts is null || parts.Count == 0)
            return "";

        var argv0 = CommandBaseName(parts[0]);
        var job = new ForegroundJobSnapshot
        {
            ProcessGroupId = 1,
            Processes =
            [
                new ForegroundProcessSnapshot
                {
                    Pid = 1,
                    Name = argv0,
                    Argv = parts,
                    Cmdline = string.Join(' ', parts),
                },
            ],
        };
        return AgentProcessResolver.TryIdentifyInJob(job, out var kind, paths) ? kind : argv0;
    }

    internal const int CtlKern = 1;
    internal const int KernProcArgs2 = 49;
    internal const int MacArgvBufferBytes = 256 * 1024;

    /// <summary>
    /// Parse macOS <c>KERN_PROCARGS2</c>: argc, exec path, then argv.
    /// </summary>
    internal static List<string>? ParseKernProcArgs2(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 4)
            return null;

        var argc = BinaryPrimitives.ReadInt32LittleEndian(buffer);
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

    internal static string TryReadMacPidCommand(int pid) =>
        TryReadMacPidCommand(pid, OsAgentPathCanonicalizer.Instance);

    internal static string TryReadMacPidCommand(int pid, IAgentPathCanonicalizer paths)
    {
        var argv = TryReadMacArgv(pid);
        return argv is { Count: > 0 } ? CommandNameFromArgv(argv, paths) : "";
    }

    internal static IReadOnlyList<string>? TryReadMacArgv(int pid)
    {
        if (!OperatingSystem.IsMacOS() || pid <= 0)
            return null;
        try
        {
            int[] mib = [CtlKern, KernProcArgs2, pid];
            var buffer = new byte[MacArgvBufferBytes];
            nuint size = (nuint)buffer.Length;
            if (NativeSysctl(mib, (uint)mib.Length, buffer, ref size, IntPtr.Zero, 0) != 0)
                return null;
            if (size == 0 || size > (nuint)buffer.Length)
                return null;
            var argv = ParseKernProcArgs2(buffer.AsSpan(0, (int)size));
            return argv is { Count: > 0 } ? argv : null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    internal static string CommandBaseName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        var trimmed = path.Trim().Trim('"');
        var slash = trimmed.LastIndexOf('/');
        var name = slash >= 0 && slash < trimmed.Length - 1
            ? trimmed[(slash + 1)..]
            : trimmed;
        var exe = name.LastIndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0 && exe == name.Length - 4)
            name = name[..exe];
        return name;
    }

    private static string ReadMacCommand(int tpgid, ReadOnlySpan<byte> shellInfo)
    {
        if (!OperatingSystem.IsMacOS())
            return "";
        if (tpgid > 0)
        {
            var fromArgv = TryReadMacPidCommand(tpgid);
            if (fromArgv.Length > 0)
                return fromArgv;

            try
            {
                Span<byte> buffer = stackalloc byte[ProcBsdInfoSize];
                int written;
                unsafe
                {
                    fixed (byte* p = buffer)
                    {
                        written = proc_pidinfo(tpgid, ProcPidTbsdInfo, 0, (nint)p, ProcBsdInfoSize);
                    }
                }

                if (written >= ProcBsdInfoNameOffset + ProcBsdInfoNameLength)
                {
                    var named = ReadCString(buffer.Slice(ProcBsdInfoNameOffset, ProcBsdInfoNameLength));
                    if (named.Length > 0)
                        return CommandBaseName(named);
                    var comm = ReadCString(buffer.Slice(ProcBsdInfoCommOffset, ProcBsdInfoCommLength));
                    if (comm.Length > 0)
                        return CommandBaseName(comm);
                }
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        if (shellInfo.Length >= ProcBsdInfoNameOffset + ProcBsdInfoNameLength)
        {
            var named = ReadCString(shellInfo.Slice(ProcBsdInfoNameOffset, ProcBsdInfoNameLength));
            if (named.Length > 0)
                return CommandBaseName(named);
        }

        return "";
    }

    private static string ReadCString(ReadOnlySpan<byte> bytes)
    {
        var zero = bytes.IndexOf((byte)0);
        if (zero < 0)
            zero = bytes.Length;
        if (zero <= 0)
            return "";
        return Encoding.UTF8.GetString(bytes[..zero]).Trim();
    }

    [LibraryImport("libproc", SetLastError = true)]
    [SupportedOSPlatform("macos")]
    private static partial int proc_pidinfo(
        int pid,
        int flavor,
        ulong arg,
        nint buffer,
        int bufferSize);

    [DllImport("libc", EntryPoint = "sysctl", SetLastError = true)]
    private static extern int NativeSysctl(
        int[] name,
        uint namelen,
        byte[]? oldp,
        ref nuint oldlenp,
        IntPtr newp,
        nuint newlen);
}
