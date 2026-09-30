using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Hypa.ControlPlane.Unix;

/// <summary>
/// Compare AF_UNIX peer credentials to <c>geteuid()</c>.
/// Linux: <c>SO_PEERCRED</c>. macOS: <c>getpeereid</c> (Darwin equal of LOCAL_PEERCRED).
/// Windows: no-op success — mux peer-cred is Unix-only.
/// </summary>
public sealed class EuidPeerAuthenticator : IUnixPeerAuthenticator
{
    private const int SolSocketLinux = 1;
    private const int SoPeercredLinux = 17;

    public void Authenticate(Socket connected)
    {
        ArgumentNullException.ThrowIfNull(connected);
        if (OperatingSystem.IsWindows())
            return;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "Control-plane peer credentials require Linux or macOS.");
        }

        var handle = connected.SafeHandle;
        var addRef = false;
        try
        {
            handle.DangerousAddRef(ref addRef);
            var fd = handle.DangerousGetHandle().ToInt32();
            if (fd < 0)
            {
                throw new UnauthorizedAccessException(
                    "AF_UNIX peer credentials: socket handle is invalid.");
            }

            var peerUid = OperatingSystem.IsLinux()
                ? GetPeerUidLinux(fd)
                : GetPeerUidMac(fd);
            var self = geteuid();
            ThrowIfForeignUid(peerUid, self);
        }
        finally
        {
            if (addRef)
                handle.DangerousRelease();
        }
    }

    /// <summary>Pure compare for tests. No live socket and no root fixture required.</summary>
    public static bool IsSameUid(uint peerUid, uint selfUid) => peerUid == selfUid;

    /// <summary>Reject a mismatched uid. Linux and macOS fixture does not use sudo.</summary>
    public static void ThrowIfForeignUid(uint peerUid, uint selfUid)
    {
        if (IsSameUid(peerUid, selfUid))
            return;

        throw new UnauthorizedAccessException(
            $"AF_UNIX peer uid {peerUid} does not match local euid {selfUid} (uid-private socket).");
    }

    private static uint GetPeerUidLinux(int fd)
    {
        // struct ucred { pid_t pid; uid_t uid; gid_t gid; }
        Span<int> ucred = stackalloc int[3];
        var len = ucred.Length * sizeof(int);
        if (getsockopt(fd, SolSocketLinux, SoPeercredLinux, ref ucred[0], ref len) != 0)
        {
            var err = Marshal.GetLastPInvokeError();
            throw new Win32Exception(err, $"getsockopt(SO_PEERCRED) failed errno={err}");
        }

        return unchecked((uint)ucred[1]);
    }

    private static uint GetPeerUidMac(int fd)
    {
        uint euid = 0;
        uint egid = 0;
        if (getpeereid(fd, ref euid, ref egid) != 0)
        {
            var err = Marshal.GetLastPInvokeError();
            throw new Win32Exception(err, $"getpeereid failed errno={err}");
        }

        return euid;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    private static extern int getsockopt(int sockfd, int level, int optname, ref int optval, ref int optlen);

    [DllImport("libc", SetLastError = true)]
    private static extern int getpeereid(int s, ref uint euid, ref uint egid);
}
