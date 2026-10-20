using System.Net;
using System.Net.Quic;
using System.Net.Security;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>Shared QUIC transport settings for the first single-stream adapter.</summary>
internal static class QuicTransportConstants
{
    public const long DefaultCloseErrorCode = 1;

    public const long DefaultStreamErrorCode = 2;

    public const int SingleBidirectionalStreamLimit = 1;

    /// <summary>
    /// A connection with no packet for this time closes. The attach health
    /// ping and the keepalive below keep a live link above it.
    /// </summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A quiet link sends a QUIC keepalive this often, so a NAT keeps its UDP binding.</summary>
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(10);

    /// <summary>Receive window of the connection.</summary>
    public const int ConnectionReceiveWindowBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Receive window of each stream. Render credit limits the render bytes
    /// in flight, so this window is not the queue in front of an echo.
    /// </summary>
    public const int StreamReceiveWindowBytes = 1024 * 1024;

    /// <summary>
    /// Sets the idle timeout, the keepalive and the receive windows. The
    /// accept and the client use the same values.
    /// </summary>
#pragma warning disable CA1416
    public static T WithLinkOptions<T>(T options)
        where T : QuicConnectionOptions
    {
        ArgumentNullException.ThrowIfNull(options);
        options.IdleTimeout = IdleTimeout;
        options.KeepAliveInterval = KeepAliveInterval;
        options.InitialReceiveWindowSizes = new QuicReceiveWindowSizes
        {
            Connection = ConnectionReceiveWindowBytes,
            LocallyInitiatedBidirectionalStream = StreamReceiveWindowBytes,
            RemotelyInitiatedBidirectionalStream = StreamReceiveWindowBytes,
            UnidirectionalStream = StreamReceiveWindowBytes,
        };
        return options;
    }
#pragma warning restore CA1416

    public const string LoopbackServerName = "localhost";

    public static SslApplicationProtocol ApplicationProtocol { get; } =
        new("hypa.connectivity.v0");

    /// <summary>
    /// RFC 6066 forbids an IP literal in SNI. .NET then sends an empty name
    /// and the MsQuic handshake does not finish.
    /// </summary>
    public static string ClientTargetHost(string? tlsServerName, string host)
    {
        var candidate = string.IsNullOrWhiteSpace(tlsServerName) ? host : tlsServerName;
        return IPAddress.TryParse(candidate, out _) ? LoopbackServerName : candidate;
    }
}
