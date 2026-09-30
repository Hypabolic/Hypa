using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Hypa.Placement.Domain;

/// <summary>Stable Placement identifier. Distinct from mux identity.</summary>
public readonly record struct PlacementId(string Value)
{
    public override string ToString() => Value;

    public static PlacementId New() => new("plc_" + Guid.NewGuid().ToString("N"));

    public static PlacementId Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("plc_", StringComparison.Ordinal))
            throw new ArgumentException("placement id must start with plc_.", nameof(value));
        return new PlacementId(trimmed);
    }

    public static bool TryParse(string? value, out PlacementId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("plc_", StringComparison.Ordinal))
            return false;
        id = new PlacementId(trimmed);
        return true;
    }
}

/// <summary>
/// Opaque mux identity. Not a raw IP, socket path, or URL.
/// Invalid values are not constructable.
/// </summary>
public readonly record struct MuxIdentity
{
    public string Value { get; } = "";

    private MuxIdentity(string value) => Value = value;

    public override string ToString() => Value;

    public static bool TryParse(string? value, out MuxIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (trimmed.Length is < 8 or > 128)
            return false;
        if (!trimmed.StartsWith("mux_", StringComparison.Ordinal))
            return false;
        if (LooksLikeNetworkAddress(trimmed) || LooksLikePath(trimmed))
            return false;

        var rest = trimmed["mux_".Length..];
        if (rest.Length == 0)
            return false;
        foreach (var c in rest)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                return false;
        }

        identity = new MuxIdentity(trimmed);
        return true;
    }

    /// <summary>
    /// Map an operator session name to an opaque mux identity.
    /// Underscore names stay <c>mux_{session}</c>. Hyphen names are hashed.
    /// </summary>
    public static bool TryFromSessionName(string? session, out MuxIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(session))
            return false;

        var trimmed = session.Trim();
        if (TryParse("mux_" + trimmed, out identity))
            return true;

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trimmed)))
            .ToLowerInvariant();
        return TryParse("mux_" + digest, out identity);
    }

    public static MuxIdentity Parse(string value)
    {
        if (!TryParse(value, out var identity))
            throw new ArgumentException("mux identity is not an opaque mux_ value.", nameof(value));
        return identity;
    }

    private static bool LooksLikePath(string value) =>
        value.Contains('/')
        || value.Contains('\\')
        || value.Contains("://", StringComparison.Ordinal)
        || value.StartsWith("unix:", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeNetworkAddress(string value)
    {
        if (IPAddress.TryParse(value, out _))
            return true;

        var lastColon = value.LastIndexOf(':');
        if (lastColon <= 0 || lastColon >= value.Length - 1)
            return false;

        var portPart = value[(lastColon + 1)..];
        if (!int.TryParse(portPart, out var port) || port is < 0 or > 65535)
            return false;

        var host = value[..lastColon];
        if (host.StartsWith('[') && host.EndsWith(']'))
            host = host[1..^1];
        return IPAddress.TryParse(host, out _);
    }
}

/// <summary>Authenticated directory requester. A display name is not this value.</summary>
public readonly record struct DirectoryIdentity(string Value)
{
    public override string ToString() => Value;

    public static DirectoryIdentity Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new DirectoryIdentity(value.Trim());
    }

    public static bool TryParse(string? value, out DirectoryIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        identity = new DirectoryIdentity(value.Trim());
        return true;
    }
}
