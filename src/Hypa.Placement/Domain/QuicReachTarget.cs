using System.Net;
using System.Text;

namespace Hypa.Placement.Domain;

/// <summary>
/// Reach target for a QUIC peer row. Hostname or IP only. No rendezvous URL.
/// </summary>
public static class QuicReachTarget
{
    public const int DefaultTlsPort = 443;
    public const int MaxUtf8Bytes = 1024;

    public static bool TryValidate(string? target, out string normalized, out string? error)
    {
        normalized = "";
        error = null;
        if (string.IsNullOrWhiteSpace(target))
        {
            error = "QUIC reach target is required";
            return false;
        }

        var trimmed = target.Trim();
        if (trimmed.StartsWith('-'))
        {
            error = "QUIC reach target must not start with '-'";
            return false;
        }

        if (RemoteTarget.ContainsControl(trimmed))
        {
            error = "QUIC reach target must contain no control characters";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(trimmed) > MaxUtf8Bytes)
        {
            error = $"QUIC reach target must be at most {MaxUtf8Bytes} bytes";
            return false;
        }

        if (trimmed.Contains('@')
            || trimmed.Contains("://", StringComparison.Ordinal)
            || RemoteTarget.ContainsPasswordUserinfo(trimmed))
        {
            error = "QUIC reach target must be a hostname or IP";
            return false;
        }

        if (!TryParseHostPort(trimmed, out _, out _, out var hostError))
        {
            error = hostError;
            return false;
        }

        normalized = trimmed;
        return true;
    }

    public static bool TryParseHostPort(
        string target,
        out string host,
        out int port,
        out string? error)
    {
        host = "";
        port = DefaultTlsPort;
        error = null;

        var trimmed = target.Trim();
        if (trimmed.Length == 0)
        {
            error = "QUIC reach target is required";
            return false;
        }

        if (trimmed.StartsWith('['))
        {
            var closing = trimmed.IndexOf(']');
            if (closing <= 1 || closing >= trimmed.Length - 1 || trimmed[closing + 1] != ':')
            {
                error = "QUIC reach target must be a hostname or IP";
                return false;
            }

            host = trimmed[1..closing];
            if (!IPAddress.TryParse(host, out _))
            {
                error = "QUIC reach target must be a hostname or IP";
                return false;
            }

            var portPart = trimmed[(closing + 2)..];
            if (!int.TryParse(portPart, out port) || port is <= 0 or > 65535)
            {
                error = "QUIC reach target port is out of range";
                return false;
            }

            return true;
        }

        var colon = trimmed.LastIndexOf(':');
        if (colon > 0 && colon < trimmed.Length - 1 && trimmed.Count(c => c == ':') == 1)
        {
            var portPart = trimmed[(colon + 1)..];
            if (int.TryParse(portPart, out var parsed) && parsed is > 0 and <= 65535)
            {
                host = trimmed[..colon];
                port = parsed;
                return ValidateHost(host, out error);
            }
        }

        host = trimmed;
        return ValidateHost(host, out error);
    }

    private static bool ValidateHost(string host, out string? error)
    {
        error = null;
        if (host.Length == 0)
        {
            error = "QUIC reach target must be a hostname or IP";
            return false;
        }

        if (IPAddress.TryParse(host, out _))
            return true;

        if (host.Contains(' ') || host.Contains('/'))
        {
            error = "QUIC reach target must be a hostname or IP";
            return false;
        }

        foreach (var label in host.Split('.'))
        {
            if (label.Length == 0 || label.Length > 63)
            {
                error = "QUIC reach target must be a hostname or IP";
                return false;
            }

            foreach (var c in label)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c == '-'))
                {
                    error = "QUIC reach target must be a hostname or IP";
                    return false;
                }
            }
        }

        return true;
    }
}
