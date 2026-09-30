using System.Net;

namespace Hypa.Connectivity.Domain;

/// <summary>Configured bind target for the Connectivity accept helper.</summary>
public sealed record ConnectivityAcceptBind
{
    public required string Host { get; init; }

    public required int Port { get; init; }

    public bool Tls { get; init; }

    public string? TlsServerName { get; init; }
}

/// <summary>Owner token for one mux bridge reservation.</summary>
public sealed record MuxBridgeReservation(Guid Value)
{
    public static MuxBridgeReservation Create() => new(Guid.NewGuid());
}

/// <summary>Successful mux bind plus the reservation that owns the Unix socket.</summary>
public sealed record MuxBridgeBind
{
    public required JoinBinding Binding { get; init; }

    public required MuxBridgeReservation Reservation { get; init; }
}

public static class ConnectivityAcceptLimits
{
    public const int MaxConcurrentHandshakes = 32;

    public static TimeSpan HandshakeTimeout { get; } = TimeSpan.FromSeconds(30);
}

public static class ConnectivityAcceptBindRules
{
    public static bool TryValidate(ConnectivityAcceptBind? bind, out string detail)
    {
        detail = "";
        if (bind is null)
        {
            detail = "accept bind is required";
            return false;
        }

        if (string.IsNullOrWhiteSpace(bind.Host))
        {
            detail = "accept bind host is required";
            return false;
        }

        if (bind.Port is < 0 or > 65535)
        {
            detail = "accept bind port is out of range";
            return false;
        }

        if (bind.Tls
            && bind.TlsServerName is not null
            && string.IsNullOrWhiteSpace(bind.TlsServerName))
        {
            detail = "tls server name is required";
            return false;
        }

        return true;
    }

    public static bool TryResolveBindAddress(
        ConnectivityAcceptBind bind,
        out IPAddress address,
        out string detail)
    {
        address = IPAddress.None;
        detail = "";
        if (!TryValidate(bind, out detail))
            return false;

        var host = bind.Host.Trim();
        if (IPAddress.TryParse(host, out var parsed))
        {
            address = parsed;
            return true;
        }

        detail = "accept bind host must be a literal ip address";
        return false;
    }

    public static bool IsLoopbackOnly(IPAddress address) =>
        IPAddress.IsLoopback(address) || IPAddress.IPv6Loopback.Equals(address);

    public static bool IsWildcard(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address);
    }

    public static BytePathEndpoint ToDialEndpoint(
        ConnectivityAcceptBind bind,
        int boundPort,
        bool? quicListening = null) =>
        new()
        {
            Host = bind.Host,
            Port = boundPort,
            Tls = bind.Tls,
            TlsServerName = bind.TlsServerName ?? bind.Host,
            QuicListening = quicListening,
        };
}
