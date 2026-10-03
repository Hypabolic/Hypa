using System.Runtime.Versioning;
using Hypa.AgentRuntime.Application;

namespace Hypa.Terminal.Pty;

public sealed partial class UnixPaneProcessInfoProbe
{
    // Darwin proc_vnodepathinfo: two vnode_info_path structs; vip_path follows
    // the 152-byte vnode_info. ABI from sys/proc_info.h (arm64 and x86_64).
    private const int ProcPidVnodePathInfo = 9;
    private const int ProcVnodePathInfoSize = 2352;
    private const int VnodePathOffset = 152;
    private const int VnodePathLength = 1024;

    public string? TryGetWorkingDirectory(int shellPid)
    {
        if (shellPid <= 0)
            return null;
        try
        {
            if (OperatingSystem.IsLinux())
                return PaneWorkingDirectory.Validate(
                    File.ResolveLinkTarget($"/proc/{shellPid}/cwd", returnFinalTarget: false)?.FullName, Directory.Exists);
            if (OperatingSystem.IsMacOS())
                return ReadMacWorkingDirectory(shellPid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return null;
    }

    [SupportedOSPlatform("macos")]
    private static string? ReadMacWorkingDirectory(int pid)
    {
        Span<byte> buffer = stackalloc byte[ProcVnodePathInfoSize];
        int written;
        unsafe
        {
            fixed (byte* pointer = buffer)
                written = proc_pidinfo(pid, ProcPidVnodePathInfo, 0, (nint)pointer, buffer.Length);
        }
        return written == buffer.Length
            ? PaneWorkingDirectory.Validate(ReadCString(buffer.Slice(VnodePathOffset, VnodePathLength)), Directory.Exists)
            : null;
    }
}
