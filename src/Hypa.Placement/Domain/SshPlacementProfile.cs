namespace Hypa.Placement.Domain;

/// <summary>
/// SSH provider fields on a peer Placement row.
/// OpenSSH owns credentials. This record stores no key material.
/// </summary>
public static class SshPlacementAttentionStates
{
    public const string ApprovalRequired = "approval_required";
    public const string PreparationRequired = "preparation_required";
}

public sealed record SshPlacementProfile
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Target { get; init; }
    public required string Session { get; init; }
    public required bool Enabled { get; init; }
    public string? Attention { get; init; }

    public static bool TryFromPeerProfile(
        PeerProfile profile,
        out SshPlacementProfile ssh,
        out string? error)
    {
        ssh = null!;
        error = null;
        if (!PeerProfile.TryCreate(
                profile.Id,
                profile.Label,
                profile.Target,
                profile.Session,
                profile.Enabled,
                PeerProviders.Ssh,
                out _,
                out var profileError))
        {
            error = profileError;
            return false;
        }

        ssh = new SshPlacementProfile
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
            PeerProviders.Ssh,
            out var profile,
            out _) && profile is not null
            ? profile
            : throw new InvalidOperationException("SSH profile is invalid");

    public static bool MatchesPlacement(PlacementId placementId, string displayName, SshPlacementProfile profile) =>
        profile.Id == placementId.Value
        && string.Equals(profile.Label, displayName, StringComparison.Ordinal);
}
