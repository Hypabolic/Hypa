using System.Security.Cryptography;

namespace Hypa.Connectivity.Domain;

/// <summary>
/// Provider-neutral peer catalog row. Kind stays <c>peer</c>.
/// Provider is a discriminator, not a Placement kind.
/// Hypa stores no keys or passwords.
/// </summary>
public sealed record PeerProfile
{
    public required PeerProfileId Id { get; init; }

    public required string Label { get; init; }

    public required string Target { get; init; }

    public required string Session { get; init; }

    public required bool Enabled { get; init; }

    public required string Provider { get; init; }

    public static PeerProfile FromExplicitTarget(string target, string session) =>
        new()
        {
            Id = PeerProfileId.Create(),
            Label = target,
            Target = target,
            Session = session,
            Enabled = true,
            Provider = PeerProviders.OpenSsh,
        };
}

public sealed record PeerProfileId
{
    public required string Value { get; init; }

    public static PeerProfileId Create()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return new PeerProfileId { Value = "prfl_" + Convert.ToHexString(bytes).ToLowerInvariant() };
    }

    public static PeerProfileId Parse(string value)
    {
        if (!TryParse(value, out var id))
            throw new ArgumentException("Peer profile id is invalid.", nameof(value));
        return id;
    }

    public static bool TryParse(string? value, out PeerProfileId id)
    {
        id = null!;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (value.Length != 21 || !value.StartsWith("prfl_", StringComparison.Ordinal))
            return false;
        for (var i = 5; i < value.Length; i++)
        {
            var c = value[i];
            var hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!hex)
                return false;
        }

        id = new PeerProfileId { Value = value };
        return true;
    }
}

public static class PeerProviders
{
    public const string OpenSsh = "openssh";
}

public static class PeerProfileRules
{
    public const int MaxTargetLength = 1024;
    public const int MinSessionLength = 1;
    public const int MaxSessionLength = 64;

    public static string? Validate(PeerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Label) || HasControl(profile.Label))
            return PeerReachReasons.ProfileInvalid;
        var targetReason = ValidateTarget(profile.Target);
        if (targetReason is not null)
            return targetReason;
        if (!IsValidSession(profile.Session))
            return PeerReachReasons.SessionInvalid;
        if (string.IsNullOrWhiteSpace(profile.Provider))
            return PeerReachReasons.ProviderUnsupported;
        return null;
    }

    public static string? ValidateTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || HasControl(target) || target.Length > MaxTargetLength)
            return PeerReachReasons.TargetInvalid;
        if (target.StartsWith('-'))
            return PeerReachReasons.TargetInvalid;
        if (ContainsPasswordUserInfo(target))
            return PeerReachReasons.CredentialsForbidden;
        return null;
    }

    public static bool IsValidSession(string? session)
    {
        if (string.IsNullOrWhiteSpace(session))
            return false;
        if (session.Length is < MinSessionLength or > MaxSessionLength)
            return false;
        if (!char.IsAsciiLetterOrDigit(session[0]))
            return false;
        foreach (var c in session)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
                continue;
            return false;
        }

        return true;
    }

    public static bool ContainsPasswordUserInfo(string target)
    {
        var remainder = target;
        if (remainder.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            remainder = remainder["ssh://".Length..];
        var at = remainder.IndexOf('@');
        if (at <= 0)
            return false;
        var userInfo = remainder[..at];
        return userInfo.Contains(':');
    }

    private static bool HasControl(string value)
    {
        foreach (var c in value)
        {
            if (char.IsControl(c))
                return true;
        }

        return false;
    }
}
