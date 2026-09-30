using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Hypa.ControlPlane.Unix;

/// <summary>
/// Read AF_UNIX peer credentials. Linux: SO_PEERCRED. macOS: LOCAL_PEERPID.
/// </summary>
internal static class UnixPeerCredentials
{
    private const int SolSocketLinux = 1;
    private const int SoPeercredLinux = 17;
    private const int SolLocalMac = 0;
    private const int LocalPeerPidMac = 0x002;

    public static bool TryGetPeerPid(Socket connected, out int peerPid)
    {
        peerPid = 0;
        ArgumentNullException.ThrowIfNull(connected);
        if (OperatingSystem.IsWindows())
            return false;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return false;

        var handle = connected.SafeHandle;
        var addRef = false;
        try
        {
            handle.DangerousAddRef(ref addRef);
            var fd = handle.DangerousGetHandle().ToInt32();
            if (fd < 0)
                return false;

            peerPid = OperatingSystem.IsLinux()
                ? GetPeerPidLinux(fd)
                : GetPeerPidMac(fd);
            return peerPid > 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
        finally
        {
            if (addRef)
                handle.DangerousRelease();
        }
    }

    private static int GetPeerPidLinux(int fd)
    {
        Span<int> ucred = stackalloc int[3];
        var len = ucred.Length * sizeof(int);
        if (getsockopt(fd, SolSocketLinux, SoPeercredLinux, ref ucred[0], ref len) != 0)
        {
            var err = Marshal.GetLastPInvokeError();
            throw new Win32Exception(err, $"getsockopt(SO_PEERCRED) failed errno={err}");
        }

        return ucred[0];
    }

    private static int GetPeerPidMac(int fd)
    {
        int pid = 0;
        var len = sizeof(int);
        if (getsockopt(fd, SolLocalMac, LocalPeerPidMac, ref pid, ref len) != 0)
        {
            var err = Marshal.GetLastPInvokeError();
            throw new Win32Exception(err, $"getsockopt(LOCAL_PEERPID) failed errno={err}");
        }

        return pid;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int getsockopt(int sockfd, int level, int optname, ref int optval, ref int optlen);
}
