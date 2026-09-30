using System.Net;
using System.Net.Security;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>Shared QUIC transport settings for the first single-stream adapter.</summary>
internal static class QuicTransportConstants
{
    public const long DefaultCloseErrorCode = 1;

    public const long DefaultStreamErrorCode = 2;

    public const int SingleBidirectionalStreamLimit = 1;

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
