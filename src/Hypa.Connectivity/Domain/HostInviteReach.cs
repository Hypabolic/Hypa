using System.Net;
using System.Net.Sockets;

namespace Hypa.Connectivity.Domain;

/// <summary>
/// Advertise host for a host invite. Wildcard binds are not this value.
/// </summary>
public static class HostInviteReach
{
    /// <summary>The most addresses one invite carries. A longer list is refused on decode.</summary>
    public const int MaxHosts = 32;

    public static bool TryValidate(
        string? host,
        int port,
        out string normalizedHost,
        out string? error)
    {
        normalizedHost = "";
        error = null;
        if (string.IsNullOrWhiteSpace(host))
        {
            error = "invite host is required";
            return false;
        }

        var trimmed = host.Trim();
        if (IsWildcard(trimmed))
        {
            error = "invite host must not be a wildcard bind";
            return false;
        }

        if (port is <= 0 or > 65535)
        {
            error = "invite port is out of range";
            return false;
        }

        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            trimmed = trimmed[1..^1];

        if (IPAddress.TryParse(trimmed, out var ip))
        {
            if (IPAddress.Any.Equals(ip) || IPAddress.IPv6Any.Equals(ip))
            {
                error = "invite host must not be a wildcard bind";
                return false;
            }

            normalizedHost = trimmed;
            return true;
        }

        if (trimmed.Contains(' ') || trimmed.Contains('/') || trimmed.Contains(':'))
        {
            error = "invite host must be a hostname or IP";
            return false;
        }

        foreach (var label in trimmed.Split('.'))
        {
            if (label.Length == 0 || label.Length > 63)
            {
                error = "invite host must be a hostname or IP";
                return false;
            }

            foreach (var c in label)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c == '-'))
                {
                    error = "invite host must be a hostname or IP";
                    return false;
                }
            }
        }

        normalizedHost = trimmed;
        return true;
    }

    public static bool IsWildcard(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;
        var trimmed = host.Trim();
        if (trimmed is "0.0.0.0" or "::" or "[::]")
            return true;
        return IPAddress.TryParse(trimmed.Trim('[', ']'), out var ip)
            && (IPAddress.Any.Equals(ip) || IPAddress.IPv6Any.Equals(ip));
    }

    /// <summary>
    /// Addresses an invite carries. A named address is the whole list.
    /// A specific bind address is the whole list when no address is named.
    /// Otherwise the list is every usable address from the interface list.
    /// </summary>
    public static bool TrySelectAdvertised(
        IReadOnlyList<string>? interfaceAddresses,
        string? namedAddress,
        string? bindAddress,
        int port,
        out IReadOnlyList<string> hosts,
        out string? error)
    {
        hosts = [];
        error = null;
        if (!string.IsNullOrWhiteSpace(namedAddress))
        {
            if (!TryValidate(namedAddress, port, out var named, out error))
                return false;
            if (TryParseAddress(named, out var namedIp) && !ListenerServes(bindAddress, namedIp.AddressFamily))
            {
                error = namedIp.AddressFamily == AddressFamily.InterNetworkV6
                    ? "the listener serves IPv4 only, so an IPv6 address cannot reach it"
                    : "the listener serves IPv6 only, so an IPv4 address cannot reach it";
                return false;
            }

            hosts = [named];
            return true;
        }

        if (!string.IsNullOrWhiteSpace(bindAddress) && !IsWildcard(bindAddress))
        {
            if (!TryValidate(bindAddress, port, out var bound, out error))
                return false;
            hosts = [bound];
            return true;
        }

        if (port is <= 0 or > 65535)
        {
            error = "invite port is out of range";
            return false;
        }

        // The default listener binds 0.0.0.0, which serves IPv4 only. Only a
        // bind to :: is dual stack, so only then does the invite carry IPv6.
        var listenerServesIpv6 = ListenerServes(bindAddress, AddressFamily.InterNetworkV6);
        var selected = new List<string>();
        if (interfaceAddresses is not null)
        {
            foreach (var candidate in interfaceAddresses)
            {
                if (!TryNormalizeInterfaceAddress(candidate, out var usable))
                    continue;
                if (!listenerServesIpv6
                    && TryParseAddress(usable, out var usableIp)
                    && usableIp.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    continue;
                }

                if (selected.Exists(existing =>
                        string.Equals(existing, usable, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                selected.Add(usable);
                if (selected.Count == MaxHosts)
                    break;
            }
        }

        if (selected.Count == 0)
        {
            error = "no reachable address";
            return false;
        }

        hosts = selected;
        return true;
    }

    /// <summary>
    /// Whether the accept listener serves an address family. No bind means the
    /// default 0.0.0.0, which serves IPv4 only. A bind to :: is dual stack.
    /// </summary>
    private static bool ListenerServes(string? bindAddress, AddressFamily family)
    {
        if (string.IsNullOrWhiteSpace(bindAddress)
            || !IPAddress.TryParse(bindAddress.Trim().Trim('[', ']'), out var bindIp))
        {
            return family == AddressFamily.InterNetwork;
        }

        if (IPAddress.IPv6Any.Equals(bindIp))
            return true;
        return bindIp.AddressFamily == family;
    }

    public static bool IsLoopbackHost(string? host) =>
        TryParseAddress(host, out var ip) && IPAddress.IsLoopback(ip);

    public static bool IsLinkLocalHost(string? host)
    {
        if (!TryParseAddress(host, out var ip))
            return false;
        if (ip.IsIPv6LinkLocal)
            return true;
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var bytes = ip.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    private static bool TryNormalizeInterfaceAddress(string? host, out string normalized)
    {
        normalized = "";
        if (!TryParseAddress(host, out var ip))
            return false;
        if (IPAddress.Any.Equals(ip) || IPAddress.IPv6Any.Equals(ip))
            return false;
        if (IPAddress.IsLoopback(ip) || ip.IsIPv4MappedToIPv6)
            return false;
        if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast)
            return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            if (bytes[0] == 169 && bytes[1] == 254)
                return false;
            if (bytes[0] >= 224)
                return false;
        }

        normalized = ip.ToString();
        return true;
    }

    private static bool TryParseAddress(string? host, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(host) || IsWildcard(host))
            return false;
        var trimmed = host.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            trimmed = trimmed[1..^1];
        var zone = trimmed.IndexOf('%');
        if (zone >= 0)
            trimmed = trimmed[..zone];
        return IPAddress.TryParse(trimmed, out address!);
    }
}
