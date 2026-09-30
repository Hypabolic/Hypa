namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// Operator text when the QUIC listener cannot use one address.
/// </summary>
public static class QuicAcceptBindNotice
{
    public const string SpecificAddressDetail =
        "The QUIC stack uses a wildcard UDP socket. It cannot start for a specific bind address. Use TLS on this address, or bind 0.0.0.0 or :: to start QUIC.";
}
