using System.Net.Sockets;
using Hypa.ControlPlane;

namespace Hypa.Cli.Mux;

/// <summary>
/// Remote-host side of the SSH stdio bridge.
/// </summary>
public static class RemoteClientBridgeCommand
{
    public static bool IsRemoteClientBridge(string[] args) =>
        args.Length >= 1 && args[0] == "remote-client-bridge";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        var session = "default";
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--session" && i + 1 < args.Length)
            {
                session = args[++i].Trim();
                continue;
            }

            if (args[i].StartsWith("--session=", StringComparison.Ordinal))
            {
                session = args[i]["--session=".Length..].Trim();
            }
        }

        if (string.IsNullOrEmpty(session))
        {
            await Console.Error.WriteLineAsync("session name cannot be empty").ConfigureAwait(false);
            return 2;
        }

        string socketPath;
        try
        {
            socketPath = UnixSocketServer.ResolveSocketPath(session);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 2;
        }

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(
                    $"failed to connect to remote mux socket {socketPath}: {ex.Message}")
                .ConfigureAwait(false);
            return 1;
        }

        var upload = Task.Run(() => CopyToSocket(Console.OpenStandardInput(), socket, ct), ct);
        var download = Task.Run(() => CopyFromSocket(socket, Console.OpenStandardOutput(), ct), ct);
        await Task.WhenAny(upload, download).ConfigureAwait(false);
        return 0;
    }

    private static void CopyToSocket(Stream source, Socket destination, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (!ct.IsCancellationRequested)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            var offset = 0;
            while (offset < read)
                offset += destination.Send(buffer, offset, read - offset, SocketFlags.None);
        }
    }

    private static void CopyFromSocket(Socket source, Stream destination, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        source.Blocking = true;
        while (!ct.IsCancellationRequested)
        {
            var read = source.Receive(buffer, SocketFlags.None);
            if (read == 0)
                break;
            destination.Write(buffer, 0, read);
            destination.Flush();
        }
    }
}
