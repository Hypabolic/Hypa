using System.Net;

namespace Hypa.Connectivity.Domain;

/// <summary>Protocol version for the versioned join envelope.</summary>
public readonly record struct ConnectivityProtocolVersion(int Major, int Minor)
{
    public static ConnectivityProtocolVersion V0 { get; } = new(0, 1);

    public override string ToString() => Major + "." + Minor;

    public static bool TryParse(string? value, out ConnectivityProtocolVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Trim().Split('.', 2, StringSplitOptions.None);
        if (parts.Length != 2)
            return false;
        if (!int.TryParse(parts[0], out var major) || major < 0)
            return false;
        if (!int.TryParse(parts[1], out var minor) || minor < 0)
            return false;

        version = new ConnectivityProtocolVersion(major, minor);
        return true;
    }
}

/// <summary>Opaque join nonce. Not a Unix path and not a listen address.</summary>
public readonly record struct JoinNonce
{
    public string Value { get; } = "";

    private JoinNonce(string value) => Value = value;

    public override string ToString() => Value;

    public static bool TryParse(string? value, out JoinNonce nonce)
    {
        nonce = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (trimmed.Length is < 8 or > 128)
            return false;
        if (ConnectivityIdRules.LooksLikePathOrListen(trimmed))
            return false;

        foreach (var c in trimmed)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-')
                return false;
        }

        nonce = new JoinNonce(trimmed);
        return true;
    }
}

/// <summary>Paired device identity. Distinct from account, Placement, and capability.</summary>
public readonly record struct DeviceId
{
    public string Value { get; } = "";

    private DeviceId(string value) => Value = value;

    public override string ToString() => Value;

    public static bool TryParse(string? value, out DeviceId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (!trimmed.StartsWith("dev_", StringComparison.Ordinal))
            return false;
        if (trimmed.Length is < 8 or > 128)
            return false;
        if (ConnectivityIdRules.LooksLikePathOrListen(trimmed))
            return false;

        id = new DeviceId(trimmed);
        return true;
    }
}

/// <summary>One-time host invite identity. Distinct from device and Placement.</summary>
public readonly record struct InviteId
{
    public string Value { get; } = "";

    private InviteId(string value) => Value = value;

    public override string ToString() => Value;

    public static bool TryParse(string? value, out InviteId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (!trimmed.StartsWith("inv_", StringComparison.Ordinal))
            return false;
        if (trimmed.Length is < 8 or > 128)
            return false;
        if (ConnectivityIdRules.LooksLikePathOrListen(trimmed))
            return false;

        id = new InviteId(trimmed);
        return true;
    }
}

/// <summary>
/// Placement identity for a join. Distinct from operator, device, and capability.
/// This is not Hypa.Placement directory state.
/// </summary>
public readonly record struct PlacementId
{
    public string Value { get; } = "";

    private PlacementId(string value) => Value = value;

    public override string ToString() => Value;

    public static bool TryParse(string? value, out PlacementId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (!trimmed.StartsWith("plc_", StringComparison.Ordinal))
            return false;
        if (trimmed.Length is < 8 or > 128)
            return false;
        if (ConnectivityIdRules.LooksLikePathOrListen(trimmed))
            return false;
        if (trimmed.StartsWith("dev_", StringComparison.Ordinal))
            return false;
        if (trimmed.StartsWith("opr_", StringComparison.Ordinal))
            return false;

        id = new PlacementId(trimmed);
        return true;
    }
}

/// <summary>
/// Outbound rendezvous URL. The mux dials this value. It is not a listen bind.
/// Unix socket paths are rejected so remote clients never see the uid-private mux socket.
/// </summary>
public readonly record struct RendezvousUrl
{
    public string Value { get; } = "";

    private RendezvousUrl(string value) => Value = value;

    public override string ToString() => Value;

    public static bool TryParse(string? value, out RendezvousUrl url)
    {
        url = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (trimmed.StartsWith("unix:", StringComparison.OrdinalIgnoreCase))
            return false;
        if (trimmed.Contains(".sock", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme is not "https" and not "wss" and not "http" and not "ws")
            return false;
        if (string.IsNullOrEmpty(uri.Host))
            return false;
        if (uri.Host is "0.0.0.0" or "::" or "[::]")
            return false;
        if (IPAddress.TryParse(uri.Host, out var ip)
            && (IPAddress.Any.Equals(ip) || IPAddress.IPv6Any.Equals(ip)))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Fragment))
            return false;

        url = new RendezvousUrl(trimmed);
        return true;
    }

    public static ConnectivityOutcome<RendezvousUrl> ParseOutcome(string? value)
    {
        if (TryParse(value, out var url))
            return ConnectivityOutcome<RendezvousUrl>.Success(url);

        return ConnectivityOutcome<RendezvousUrl>.Failure(
            ConnectivityReasons.RendezvousUrlInvalid,
            "rendezvous url must be an outbound http or websocket url");
    }

    public static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }
}

internal static class ConnectivityIdRules
{
    public static bool LooksLikePathOrListen(string value) =>
        value.Contains('/')
        || value.Contains('\\')
        || value.Contains("://", StringComparison.Ordinal)
        || value.StartsWith("unix:", StringComparison.OrdinalIgnoreCase)
        || value.Contains(".sock", StringComparison.OrdinalIgnoreCase)
        || value.Contains("0.0.0.0", StringComparison.Ordinal)
        || value.Contains("[::]", StringComparison.Ordinal);

    public static bool LooksLikePlacementId(string? value) =>
        PlacementId.TryParse(value, out _);
}
