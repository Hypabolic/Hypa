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

    ValueTask<ConnectivityOutcome<StreamFrame>> ReceiveAsync(
        CancellationToken cancellationToken = default);
}
