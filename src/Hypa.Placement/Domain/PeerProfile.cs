using System.Text;
using System.Text.RegularExpressions;

namespace Hypa.Placement.Domain;

/// <summary>
/// Shared peer row contract. A later catalog persists the same fields on a
/// <see cref="PlacementDirectoryKind.Peer"/> Placement.
/// </summary>
public sealed partial record PeerProfile
{
    public const int MaxLabelUtf8Bytes = 128;
    public const int MaxSessionAsciiBytes = 64;

    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Target { get; init; }
    public required string Session { get; init; }
    public required bool Enabled { get; init; }
    public required string Provider { get; init; }

    public static PeerProfile ExplicitSsh(string target, string session)
    {
        if (!TryCreate(
                NewId(),
                target.Trim(),
                target,
                session,
                enabled: true,
                PeerProviders.Ssh,
                out var profile,
                out var error))
        {
            throw new ArgumentException(error, nameof(target));
        }

        return profile;
    }

    public static bool TryCreate(
        string id,
        string label,
        string target,
        string session,
        bool enabled,
        string provider,
        out PeerProfile profile,
        out string? error)
    {
        profile = null!;
        error = null;

        var trimmedId = id.Trim();
        if (trimmedId.Length == 0 || RemoteTarget.ContainsControl(trimmedId) || trimmedId.Contains(' '))
        {
            error = "peer profile id cannot be empty";
            return false;
        }

        var trimmedLabel = label.Trim();
        if (trimmedLabel.Length == 0)
        {
            error = LabelEmptyError(provider);
            return false;
        }

        if (RemoteTarget.ContainsControl(trimmedLabel)
            || Encoding.UTF8.GetByteCount(trimmedLabel) > MaxLabelUtf8Bytes)
        {
            error = LabelLengthError(provider);
            return false;
        }

        if (!PeerProviders.IsKnown(provider))
        {
            error = "peer provider must be ssh or quic";
            return false;
        }

        string normalizedTarget;
        if (string.Equals(provider, PeerProviders.Ssh, StringComparison.Ordinal))
        {
            if (!RemoteTarget.TryValidate(target, out normalizedTarget, out var targetError))
            {
                error = targetError;
                return false;
            }
        }
        else
        {
            if (!QuicReachTarget.TryValidate(target, out normalizedTarget, out var targetError))
            {
                error = targetError;
                return false;
            }
        }

        if (!TryValidateSession(session, out var normalizedSession, out var sessionError))
        {
            error = sessionError;
            return false;
        }

        profile = new PeerProfile
        {
            Id = trimmedId,
            Label = trimmedLabel,
            Target = normalizedTarget,
            Session = normalizedSession,
            Enabled = enabled,
            Provider = provider,
        };
        return true;
    }

    public static bool TryValidateSession(string? session, out string normalized, out string? error)
    {
        normalized = "";
        error = null;
        if (string.IsNullOrEmpty(session))
        {
            error = "session name cannot be empty";
            return false;
        }

        if (session.Length > MaxSessionAsciiBytes)
        {
            error = $"session name cannot be longer than {MaxSessionAsciiBytes} bytes";
            return false;
        }

        if (!SessionNameRegex().IsMatch(session))
        {
            error = "session name may only contain ASCII letters, numbers, '.', '_' and '-'";
            return false;
        }

        normalized = session;
        return true;
    }

    public static string NewId() => Guid.NewGuid().ToString("N");

    private static string LabelEmptyError(string provider) =>
        string.Equals(provider, PeerProviders.Ssh, StringComparison.Ordinal)
            ? "SSH endpoint label cannot be empty"
            : "peer placement label cannot be empty";

    private static string LabelLengthError(string provider) =>
        string.Equals(provider, PeerProviders.Ssh, StringComparison.Ordinal)
            ? $"SSH endpoint label must be at most {MaxLabelUtf8Bytes} bytes and contain no control characters"
            : $"peer placement label must be at most {MaxLabelUtf8Bytes} bytes and contain no control characters";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionNameRegex();
}
