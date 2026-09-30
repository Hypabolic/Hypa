namespace Hypa.Terminal.Pty;

/// <summary>
/// Explicit native-helper handoff port for H2 same-host descriptor transfer.
/// Owns the private Unix socket and ancillary SCM_RIGHTS operation.
/// Never models the master PTY as a managed <see cref="Stream"/>.
/// </summary>
public interface IPtyHandoffPort : IAsyncDisposable
{
    /// <summary>Send a control message (Hello / Prepare / HandleMeta body / Commit / Abort).</summary>
    ValueTask SendAsync(PtyHandoffMessage message, CancellationToken ct = default);

    /// <summary>
    /// Receive the next control message. HandleMeta is paired with
    /// <see cref="ReceiveHandleAsync"/> on the importer side.
    /// </summary>
    ValueTask<PtyHandoffMessage> ReceiveAsync(CancellationToken ct = default);

    /// <summary>
    /// Receive the master PTY descriptor attached to HandleMeta via SCM_RIGHTS.
    /// Must be called after receiving a HandleMeta message (or by the port
    /// implementation as part of HandleMeta receive — see <see cref="UnixPtyHandoffPort"/>).
    /// </summary>
    ValueTask<PtyHandoffHandle> ReceiveHandleAsync(CancellationToken ct = default);

    /// <summary>
    /// Send HandleMeta with an attached master FD (exporter side).
    /// </summary>
    ValueTask SendHandleAsync(
        PtyHandoffMessage meta,
        PtyHandoffHandle handle,
        CancellationToken ct = default);
}
