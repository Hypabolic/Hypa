using Microsoft.Extensions.Logging;

namespace Hypa.Terminal.Pty;

/// <summary>
/// One in-flight H2 export listener. Dispose unlinks the private sock and
/// cancels Accept. A later export or pane dispose replaces the previous lease.
/// </summary>
internal sealed class HandoffExportLease : IAsyncDisposable
{
    internal static readonly TimeSpan AcceptTimeout = TimeSpan.FromSeconds(30);

    private readonly UnixPtyHandoffPort _listener;
    private readonly CancellationTokenSource _cts;
    private int _disposed;

    private HandoffExportLease(UnixPtyHandoffPort listener, CancellationTokenSource cts)
    {
        _listener = listener;
        _cts = cts;
    }

    public string SocketPath => _listener.SocketPath ?? string.Empty;

    public static HandoffExportLease Start(
        IPtyProcessControl exporter,
        PtyHandoffExportOptions options,
        CancellationToken ct,
        ILogger logger)
    {
        var listener = UnixPtyHandoffPort.Listen();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(AcceptTimeout);
        var lease = new HandoffExportLease(listener, cts);
        _ = Task.Run(async () =>
        {
            try
            {
                await listener.AcceptAsync(cts.Token).ConfigureAwait(false);
                await exporter.ExportHandoffAsync(listener, options, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Handoff export background task ended");
            }
            finally
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }, CancellationToken.None);
        return lease;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try { await _cts.CancelAsync().ConfigureAwait(false); }
        catch { /* already cancelled */ }

        try { await _listener.DisposeAsync().ConfigureAwait(false); }
        catch { /* unlink best-effort */ }

        _cts.Dispose();
    }
}
