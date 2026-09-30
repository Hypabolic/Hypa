using Hypa.Placement.Domain;

namespace Hypa.Placement.Application;

public static class SshPlacementProfileMapper
{
    public static SshPlacementProfileDto? ToDto(SshPlacementProfile? profile) =>
        profile is null
            ? null
            : new SshPlacementProfileDto
            {
                Id = profile.Id,
                Label = profile.Label,
                Target = profile.Target,
                Session = profile.Session,
                Enabled = profile.Enabled,
                Attention = profile.Attention,
            };

    internal static bool TryFromDto(
        SshPlacementProfileDto? dto,
        out SshPlacementProfile? profile,
        out string? error)
    {
        profile = null;
        error = null;
        if (dto is null)
            return true;

        if (!PeerProfile.TryCreate(
                dto.Id,
                dto.Label,
                dto.Target,
                dto.Session,
                dto.Enabled,
                PeerProviders.Ssh,
                out var peer,
                out var profileError))
        {
            error = profileError ?? "Invalid SSH Placement fields. Check the label, target, and session.";
            return false;
        }

        if (!SshPlacementProfile.TryFromPeerProfile(peer, out var ssh, out var mismatch))
        {
            error = mismatch ?? PlacementReasons.ProfileInvalid;
            return false;
        }

        profile = ssh with { Attention = NormalizeAttention(dto.Attention) };
        return true;
    }

    private static string? NormalizeAttention(string? attention)
    {
        if (string.IsNullOrWhiteSpace(attention))
            return null;
        return attention switch
        {
            SshPlacementAttentionStates.ApprovalRequired => SshPlacementAttentionStates.ApprovalRequired,
            SshPlacementAttentionStates.PreparationRequired => SshPlacementAttentionStates.PreparationRequired,
            _ => null,
        };
    }

    internal static bool ValidateRecord(
        PlacementId placementId,
        string displayName,
        SshPlacementProfile? ssh,
        out string? error)
    {
        error = null;
        if (ssh is null)
            return true;

        if (ssh.Id != placementId.Value)
        {
            error = "SSH profile id must match the Placement id";
            return false;
        }

        if (!string.Equals(ssh.Label, displayName, StringComparison.Ordinal))
        {
            error = "SSH profile label must match the Placement display name";
            return false;
        }

        return true;
    }
}
