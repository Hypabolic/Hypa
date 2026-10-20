using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>
/// Framed multiplex after a successful join. Control is NDJSON. Binary is terminal bytes.
/// </summary>
public interface IFramedSession : IAsyncDisposable
{
    JoinBinding Binding { get; }

    bool IsBinaryPaused { get; }

    bool ApplicationEncryptionEnabled { get; }

    IReadOnlyList<ChannelCursor> LastReceived { get; }

    ValueTask<ConnectivityOutcome> SendAsync(
        StreamFrame frame,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// True when <see cref="SendAsync"/> copies the payload before it returns
    /// or waits, so the caller can use the payload memory again at once.
    /// False when the session keeps the frame, as a queue does.
    /// </summary>
    bool CopiesPayloadOnSend => false;

    ValueTask<ConnectivityOutcome<StreamFrame>> ReceiveAsync(
        CancellationToken cancellationToken = default);
}
