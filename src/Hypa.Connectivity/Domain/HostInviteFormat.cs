using System.Text;

namespace Hypa.Connectivity.Domain;

/// <summary>
/// Transferable host invite prefix and field rules.
/// Prefix is not <see cref="DevicePairingPaths.QrPrefix"/>.
/// </summary>
public static class HostInviteFormat
{
    public const string TypeName = "host_invite";
    public const int Schema = 1;

    public static string Wrap(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return DevicePairingPaths.InvitePrefix
            + PairingOfferFormat.ToBase64Url(Encoding.UTF8.GetBytes(json));
    }

    public static string Compact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (!char.IsWhiteSpace(c))
                sb.Append(c);
        }

        return sb.ToString();
    }

    public static bool TryUnwrap(string? value, out byte[] json, out string? error)
    {
        json = [];
        error = "invite is invalid";
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var compacted = Compact(value);
        if (compacted.StartsWith(DevicePairingPaths.QrPrefix, StringComparison.OrdinalIgnoreCase))
        {
            error = "pairing offer is not a host invite";
            return false;
        }

        var body = compacted.StartsWith(DevicePairingPaths.InvitePrefix, StringComparison.OrdinalIgnoreCase)
            ? compacted[DevicePairingPaths.InvitePrefix.Length..]
            : compacted;
        if (!PairingOfferFormat.TryFromBase64Url(body, out json) || json.Length == 0)
            return false;

        error = null;
        return true;
    }

    public static bool TryNormalizeSecret(string? secret, out string normalized, out string? error)
    {
        normalized = "";
        error = null;
        if (string.IsNullOrWhiteSpace(secret))
        {
            error = "invite secret is required";
            return false;
        }

        var trimmed = secret.Trim().ToLowerInvariant();
        if (trimmed.Length != HostInviteLimits.SecretBytes * 2)
        {
            error = "invite secret must be 128 bits";
            return false;
        }

        foreach (var c in trimmed)
        {
            if (!Uri.IsHexDigit(c))
            {
                error = "invite secret must be 128 bits";
                return false;
            }
        }

        normalized = trimmed;
        return true;
    }

    public static bool TryNormalizeFingerprint(string? value, out string hex, out string? error)
    {
        hex = "";
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "certificate fingerprint is required";
            return false;
        }

        var trimmed = value.Trim().Replace(":", "", StringComparison.Ordinal).ToLowerInvariant();
        if (trimmed.Length != 64)
        {
            error = "certificate fingerprint must be 64 hex characters";
            return false;
        }

        foreach (var c in trimmed)
        {
            if (!Uri.IsHexDigit(c))
            {
                error = "certificate fingerprint must be 64 hex characters";
                return false;
            }
        }

        hex = trimmed;
        return true;
    }
}
