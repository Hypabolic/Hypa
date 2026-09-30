namespace Hypa.Placement.Domain;

/// <summary>
/// QUIC provider fields on a peer Placement row. Stores no secrets or sockets.
/// </summary>
public sealed record QuicPlacementProfile
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Target { get; init; }
    public required string Session { get; init; }
    public required bool Enabled { get; init; }
    public string? CertificateSha256 { get; init; }
    public string? EnrolledDeviceId { get; init; }

    public static bool TryFromPeerProfile(
        PeerProfile profile,
        out QuicPlacementProfile quic,
        out string? error)
    {
        quic = null!;
        error = null;
        if (!PeerProfile.TryCreate(
                profile.Id,
                profile.Label,
                profile.Target,
                profile.Session,
                profile.Enabled,
                PeerProviders.Quic,
                out _,
                out var profileError))
        {
            error = profileError;
            return false;
        }

        quic = new QuicPlacementProfile
        {
            Id = profile.Id.Trim(),
            Label = profile.Label,
            Target = profile.Target,
            Session = profile.Session,
            Enabled = profile.Enabled,
        };
        return true;
    }

    public PeerProfile ToPeerProfile() =>
        PeerProfile.TryCreate(
            Id,
            Label,
            Target,
            Session,
            Enabled,
            PeerProviders.Quic,
            out var profile,
            out _) && profile is not null
            ? profile
            : throw new InvalidOperationException("QUIC profile is invalid");

    public static bool MatchesPlacement(
        PlacementId placementId,
        string displayName,
        QuicPlacementProfile profile) =>
        profile.Id == placementId.Value
        && string.Equals(profile.Label, displayName, StringComparison.Ordinal);
}
