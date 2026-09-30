using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Hypa.AgentRuntime.Application;

namespace Hypa.Terminal.Pty;

public sealed partial class UnixPaneProcessInfoProbe
{
    internal const int ProcListPgrpOnly = 2;
    internal const int ProcBsdInfoPgidOffset = 100;

    /// <summary>
    /// Processes in the foreground group. Linux walks the process tree
    /// from the shell and the group leader, then keeps members whose
    /// process group matches. macOS lists the group with
    /// <c>proc_listpids</c>.
    /// </summary>
    internal static ForegroundJobSnapshot? TryCollectForegroundJob(int shellPid, int groupId)
    {
        if (groupId <= 0)
            return null;
        if (OperatingSystem.IsLinux())
            return TryCollectLinuxForegroundJob(shellPid, groupId);
        if (OperatingSystem.IsMacOS())
            return TryCollectMacForegroundJob(groupId);
        return null;
    }

    private string CommandFromForegroundJob(ForegroundJobSnapshot? job, int groupId)
    {
        if (job is null || job.Processes.Count == 0)
            return "";

        if (AgentProcessResolver.TryIdentifyInJob(job, out var kind, _paths))
            return kind;

        foreach (var process in job.Processes)
        {
            if (process.Pid != groupId)
                continue;
            var argv0 = process.Argv is { Count: > 0 } ? process.Argv[0] : process.Argv0;
            var name = CommandBaseName(argv0);
            if (name.Length > 0)
                return name;
            return CommandBaseName(process.Name);
        }

        return "";
    }

    internal static ForegroundJobSnapshot? TryCollectLinuxForegroundJob(int shellPid, int groupId)
    {
        if (!OperatingSystem.IsLinux() || groupId <= 0)
            return null;

        var processes = new List<ForegroundProcessSnapshot>();
        foreach (var pid in CollectLinuxProcessTree(shellPid, groupId))
        {
            if (!TryReadLinuxPgrpAndComm(pid, out var pgrp, out var comm) || pgrp != groupId)
                continue;

            var argv = TryReadLinuxArgv(pid);
            processes.Add(new ForegroundProcessSnapshot
            {
                Pid = pid,
                Name = comm,
                Argv = argv,
                Cmdline = argv is null ? null : string.Join(' ', argv),
            });
        }

        return FinishJob(groupId, processes);
    }

    internal static bool TryParseLinuxStatIdentity(string? text, out int pgrp, out string comm)
    {
        pgrp = 0;
        comm = "";
        if (string.IsNullOrEmpty(text))
            return false;

        var open = text.IndexOf('(');
        var close = text.LastIndexOf(')');
        if (open < 0 || close <= open || close + 2 >= text.Length)
            return false;

        comm = text[(open + 1)..close];
        var rest = text[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (rest.Length < 3 || comm.Length == 0)
            return false;
        if (!int.TryParse(rest[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out pgrp))
            return false;
        return pgrp > 0;
    }

    private static List<int> CollectLinuxProcessTree(int shellPid, int groupId)
    {
        var pending = new Queue<int>();
        var visited = new HashSet<int>();
        Enqueue(shellPid);
        Enqueue(groupId);

        var pids = new List<int>();
        while (pending.Count > 0)
        {
            var pid = pending.Dequeue();
            pids.Add(pid);
            foreach (var tid in ReadNumericDirectory("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/task"))
            {
                foreach (var child in ReadProcChildren(pid, tid))
                    Enqueue(child);
            }
        }

        return pids;

        void Enqueue(int pid)
        {
            if (pid > 0 && visited.Add(pid))
                pending.Enqueue(pid);
        }
    }

    private static List<int> ReadNumericDirectory(string path)
    {
        var ids = new List<int>();
        try
        {
            if (!Directory.Exists(path))
                return ids;
            foreach (var entry in Directory.EnumerateDirectories(path))
            {
                var name = Path.GetFileName(entry);
                if (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
                    ids.Add(id);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return ids;
    }

    private static List<int> ReadProcChildren(int pid, int tid)
    {
        var path = "/proc/" + pid.ToString(CultureInfo.InvariantCulture)
            + "/task/" + tid.ToString(CultureInfo.InvariantCulture) + "/children";
        try
        {
            if (!File.Exists(path))
                return [];
            var text = File.ReadAllText(path);
            var ids = new List<int>();
            foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
                    ids.Add(id);
            }

            return ids;
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool TryReadLinuxPgrpAndComm(int pid, out int pgrp, out string comm)
    {
        pgrp = 0;
        comm = "";
        if (pid <= 0)
            return false;
        try
        {
            var path = "/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat";
            if (!File.Exists(path))
                return false;
            return TryParseLinuxStatIdentity(File.ReadAllText(path), out pgrp, out comm);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string>? TryReadLinuxArgv(int pid)
    {
        if (pid <= 0)
            return null;
        try
        {
            var path = "/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/cmdline";
            if (!File.Exists(path))
                return null;
            var raw = File.ReadAllBytes(path);
            if (raw.Length == 0)
                return null;
            var parts = Encoding.UTF8.GetString(raw).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? null : parts;
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

    internal static ForegroundJobSnapshot? TryCollectMacForegroundJob(int groupId)
    {
        if (!OperatingSystem.IsMacOS() || groupId <= 0)
            return null;

        var processes = new List<ForegroundProcessSnapshot>();
        foreach (var pid in ListMacProcessGroupPids(groupId))
        {
            if (!TryReadMacGroupMember(pid, out var pgid, out var comm) || pgid != groupId)
                continue;

            var argv = TryReadMacArgv(pid);
            processes.Add(new ForegroundProcessSnapshot
            {
                Pid = pid,
                Name = comm,
                Argv0 = Argv0Basename(argv),
                Argv = argv,
                Cmdline = argv is null ? null : string.Join(' ', argv),
            });
        }

        return FinishJob(groupId, processes);
    }

    private static string? Argv0Basename(IReadOnlyList<string>? argv)
    {
        if (argv is not { Count: > 0 })
            return null;
        var name = CommandBaseName(argv[0]);
        if (name.StartsWith('-') && name.Length > 1)
            name = name[1..];
        return name.Length == 0 ? null : name;
    }

    private static int[] ListMacProcessGroupPids(int groupId)
    {
        if (!OperatingSystem.IsMacOS() || groupId <= 0)
            return [];

        var capacity = 16;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var pids = new int[capacity];
            var bytes = pids.Length * sizeof(int);
            int returned;
            try
            {
                returned = proc_listpids((uint)ProcListPgrpOnly, (uint)groupId, pids, bytes);
            }
            catch (DllNotFoundException)
            {
                return [];
            }
            catch (EntryPointNotFoundException)
            {
                return [];
            }

            if (returned <= 0)
                return [];
            if (returned < bytes)
            {
                var count = returned / sizeof(int);
                var list = new List<int>(count);
                for (var i = 0; i < count; i++)
                {
                    if (pids[i] > 0)
                        list.Add(pids[i]);
                }

                return list.ToArray();
            }

            capacity *= 2;
        }

        return [];
    }

    private static bool TryReadMacGroupMember(int pid, out int pgid, out string comm)
    {
        pgid = 0;
        comm = "";
        if (!OperatingSystem.IsMacOS() || pid <= 0)
            return false;

        try
        {
            Span<byte> buffer = stackalloc byte[ProcBsdInfoSize];
            int written;
            unsafe
            {
                fixed (byte* pointer = buffer)
                {
                    written = proc_pidinfo(pid, ProcPidTbsdInfo, 0, (nint)pointer, ProcBsdInfoSize);
                }
            }

            if (written < ProcBsdInfoPgidOffset + sizeof(int))
                return false;

            pgid = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(ProcBsdInfoPgidOffset, sizeof(int)));
            comm = ReadCString(buffer.Slice(ProcBsdInfoCommOffset, ProcBsdInfoCommLength));
            return pgid > 0 && comm.Length > 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static ForegroundJobSnapshot? FinishJob(int groupId, List<ForegroundProcessSnapshot> processes)
    {
        if (processes.Count == 0)
            return null;
        processes.Sort(static (left, right) => left.Pid.CompareTo(right.Pid));
        return new ForegroundJobSnapshot
        {
            ProcessGroupId = groupId,
            Processes = processes,
        };
    }

    [LibraryImport("libproc", SetLastError = true)]
    [SupportedOSPlatform("macos")]
    private static partial int proc_listpids(
        uint type,
        uint typeinfo,
        int[] buffer,
        int bufferSize);
}
