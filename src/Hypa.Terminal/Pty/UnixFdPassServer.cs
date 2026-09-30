using System.Net.Sockets;
using Microsoft.Win32.SafeHandles;

namespace Hypa.Terminal.Pty;

/// <summary>
/// One-shot AF_UNIX listener used to exchange a single FD with hypa-pty-host
/// via SCM_RIGHTS (stdio cannot carry descriptors).
/// Socket lives in a private 0700 directory; peer UID must match local euid.
/// </summary>
internal sealed class UnixFdPassServer : IAsyncDisposable
{
    private readonly string _path;
    private readonly string _privateDir;
    private readonly Socket _listener;
    private int _disposed;

    private UnixFdPassServer(string path, string privateDir, Socket listener)
    {
        _path = path;
        _privateDir = privateDir;
        _listener = listener;
    }

    public string Path => _path;

    public static UnixFdPassServer Create()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("FD-pass UDS is Unix-only.");

        var (dir, path) = UnixPrivateSocketPath.Create("hypa-fdpass");
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            UnixPrivateSocketPath.HardenSocketFile(path);
            // Listener itself must not leak across accidental exec of the managed process.
            UnixPtyFdValidation.SetCloexec(listener.SafeHandle.DangerousGetHandle().ToInt32());
            listener.Listen(1);
            return new UnixFdPassServer(path, dir, listener);
        }
        catch
        {
            listener.Dispose();
            UnixPrivateSocketPath.TryUnlink(path);
            UnixPrivateSocketPath.TryDeleteDirectory(dir);
            throw;
        }
    }

    /// <summary>Accept one connection and receive a single SCM_RIGHTS PTY master (helper → managed).</summary>
    public async Task<SafeFileHandle> AcceptAndRecvFdAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var sock = await _listener.AcceptAsync(ct).ConfigureAwait(false);
        UnixPrivateSocketPath.ValidatePeerIsSelf(sock);
        var handle = sock.SafeHandle;
        var fd = handle.DangerousGetHandle().ToInt32();
        return UnixAncillaryFd.RecvPtyMasterSafeHandle(fd);
    }

    /// <summary>Accept one connection and send a single SCM_RIGHTS FD (managed → helper).</summary>
    public async Task AcceptAndSendFdAsync(int fdToSend, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var sock = await _listener.AcceptAsync(ct).ConfigureAwait(false);
        UnixPrivateSocketPath.ValidatePeerIsSelf(sock);
        var handle = sock.SafeHandle;
        var connFd = handle.DangerousGetHandle().ToInt32();
        UnixAncillaryFd.SendFd(connFd, fdToSend);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        try { _listener.Dispose(); }
        catch { /* ignore */ }
        UnixPrivateSocketPath.TryUnlink(_path);
        UnixPrivateSocketPath.TryDeleteDirectory(_privateDir);
        return ValueTask.CompletedTask;
    }
}
