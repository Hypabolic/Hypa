using System.Diagnostics;
using System.Net.Sockets;
using Hypa.ControlPlane.Unix;

namespace Hypa.Placement.Infrastructure;

/// <summary>
/// Local listener that bridges accepted connections over SSH stdio.
/// </summary>
public sealed class SshStdioBridge : ISshStdioBridge
{
    private readonly string _localSocketPath;
    private readonly UnixSocketFileIdentity? _socketIdentity;
    private readonly Socket _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private readonly SshStdioBridgeStartRequest _request;
    private int _disposed;

    private SshStdioBridge(
        SshStdioBridgeStartRequest request,
        Socket listener,
        UnixSocketFileIdentity? socketIdentity)
    {
        _request = request;
        _localSocketPath = request.LocalSocketPath;
        _listener = listener;
        _socketIdentity = socketIdentity;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public string LocalSocketPath => _localSocketPath;

    public static SshStdioBridge Start(SshStdioBridgeStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (listener, identity) = UnixBridgeListener.Bind(request.LocalSocketPath, request.FreshDirectory);
        return new SshStdioBridge(request, listener, identity);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        _stop.Cancel();
        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        UnixBridgeListener.Stop(_localSocketPath, _socketIdentity, _listener);
        _stop.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private async Task AcceptLoopAsync()
    {
        var authenticator = new EuidPeerAuthenticator();
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var client = _listener.Accept();
                _ = Task.Run(() => BridgeConnection(client, authenticator, _request, _stop.Token));
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
            {
                await Task.Delay(50, _stop.Token).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static void BridgeConnection(
        Socket client,
        EuidPeerAuthenticator authenticator,
        SshStdioBridgeStartRequest request,
        CancellationToken ct)
    {
        using (client)
        {
            try
            {
                authenticator.Authenticate(client);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            using var process = StartBridgeProcess(request);
            if (process is null)
                return;

            using var stdin = process.StandardInput.BaseStream;
            using var stdout = process.StandardOutput.BaseStream;
            var upload = Task.Run(() => CopyStream(client, stdin, ct), ct);
            var download = Task.Run(() => CopyStream(stdout, client, ct), ct);
            Task.WaitAny(upload, download);
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private static Process? StartBridgeProcess(SshStdioBridgeStartRequest request)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in OpenSshArgumentBuilder.Build(new OpenSshProcessRequest
        {
            Target = request.Target,
            RemoteCommand = RemoteMuxCommands.RemoteBridgeCommand(request.Session),
            ConfigPath = request.ConfigPath,
            ControlPath = request.ControlPath,
            BatchMode = request.BatchMode,
        }))
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            return Process.Start(psi);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void CopyStream(Socket source, Stream destination, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        source.Blocking = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var read = source.Receive(buffer, SocketFlags.None);
                if (read == 0)
                    break;
                destination.Write(buffer, 0, read);
                destination.Flush();
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
            {
                Thread.Sleep(10);
            }
        }
    }

    private static void CopyStream(Stream source, Socket destination, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (!ct.IsCancellationRequested)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            var offset = 0;
            while (offset < read)
            {
                offset += destination.Send(buffer, offset, read - offset, SocketFlags.None);
            }
        }
    }
}
