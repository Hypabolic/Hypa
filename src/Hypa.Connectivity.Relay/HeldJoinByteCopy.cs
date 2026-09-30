using System.Net.Sockets;

namespace Hypa.Connectivity.Relay;

/// <summary>
/// Keep joined sockets open after the join result. Copy opaque bytes.
/// Bound NDJSON vs binary framing stays later stream-class work.
/// </summary>
internal static class HeldJoinByteCopy
{
    public static async Task RunAsync(Stream mux, Stream client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mux);
        ArgumentNullException.ThrowIfNull(client);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var muxToClient = CopyAsync(mux, client, linked.Token);
        var clientToMux = CopyAsync(client, mux, linked.Token);
        _ = await Task.WhenAny(muxToClient, clientToMux).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(muxToClient, clientToMux).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private static async Task CopyAsync(Stream from, Stream to, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var n = await from.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (n == 0)
                    return;
                await to.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                await to.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
    }
}
