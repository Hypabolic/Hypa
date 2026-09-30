using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>
/// Local same-uid Unix bridge. Remote clients never receive the socket path.
/// </summary>
public interface ILocalUnixBridge
{
    /// <summary>Must stay false. The uid-private Unix path is not a join field.</summary>
    bool ExposesUnixSocketPath { get; }

    /// <summary>
    /// Connect to the local mux socket as this uid. Dial the rendezvous as mux.
    /// The Unix path is not a bootstrap field.
    /// </summary>
    ValueTask<ConnectivityOutcome<MuxBridgeBind>> BindMuxAsync(
        JoinBootstrap bootstrap,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes the mux stream for one reservation. The path never leaves the helper.
    /// </summary>
    Stream? TryTakeMuxStream(MuxBridgeReservation reservation);

    /// <summary>Release one reservation when admission aborts before take.</summary>
    void ReleaseMuxReservation(MuxBridgeReservation reservation);
}
