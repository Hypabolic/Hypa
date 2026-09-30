using Hypa.Connectivity.Application;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// Copies control NDJSON between a framed accept session and the mux Unix stream.
/// The Unix side never sees Connectivity frames.
/// </summary>
internal static class AcceptControlNdjsonPump
{
    public static async Task RunAsync(
        IFramedSession session,
        Stream unixStream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(unixStream);

        await using var control = new FramedControlStream(session, ownsSession: false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var toUnix = control.CopyToAsync(unixStream, linked.Token);
        var toPeer = unixStream.CopyToAsync(control, linked.Token);
        var first = await Task.WhenAny(toUnix, toPeer).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(toUnix, toPeer).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (NotSupportedException)
        {
        }

        if (first.IsFaulted)
        {
            try
            {
                await first.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (NotSupportedException)
            {
            }
        }
    }
}
