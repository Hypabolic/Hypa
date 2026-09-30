using Hypa.Placement.Domain;

namespace Hypa.Placement.Application;

public static class QuicPlacementProfileMapper
{
    public static QuicPlacementProfileDto? ToDto(QuicPlacementProfile? profile) =>
        profile is null
            ? null
            : new QuicPlacementProfileDto
            {
                Id = profile.Id,
                Label = profile.Label,
                Target = profile.Target,
                Session = profile.Session,
                Enabled = profile.Enabled,
                CertificateSha256 = profile.CertificateSha256,
                EnrolledDeviceId = profile.EnrolledDeviceId,
            };

    internal static bool TryFromDto(
        QuicPlacementProfileDto? dto,
        out QuicPlacementProfile? profile,
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
                PeerProviders.Quic,
                out var peer,
                out var profileError))
        {
            error = profileError ?? "Invalid QUIC Placement fields. Check the label, target, and session.";
            return false;
        }

        if (!QuicPlacementProfile.TryFromPeerProfile(peer, out var quic, out var mismatch))
        {
            error = mismatch ?? PlacementReasons.ProfileInvalid;
            return false;
        }

        if (!CertificateSha256Rules.TryNormalizeOptional(dto.CertificateSha256, out var pin, out var pinError))
        {
            error = pinError ?? "certificate fingerprint is invalid";
            return false;
        }

        profile = quic with
        {
            CertificateSha256 = pin,
            EnrolledDeviceId = string.IsNullOrWhiteSpace(dto.EnrolledDeviceId)
                ? null
                : dto.EnrolledDeviceId.Trim(),
        };
        return true;
    }

    internal static bool ValidateRecord(
        PlacementId placementId,
        string displayName,
        QuicPlacementProfile? quic,
        out string? error)
    {
        error = null;
        if (quic is null)
            return true;

        if (quic.Id != placementId.Value)
        {
            error = "QUIC profile id must match the Placement id";
            return false;
        }

        if (!string.Equals(quic.Label, displayName, StringComparison.Ordinal))
        {
            error = "QUIC profile label must match the Placement display name";
            return false;
        }

        if (!CertificateSha256Rules.TryNormalizeOptional(quic.CertificateSha256, out _, out var pinError))
        {
            error = pinError ?? "certificate fingerprint is invalid";
            return false;
        }

        return true;
    }
}
