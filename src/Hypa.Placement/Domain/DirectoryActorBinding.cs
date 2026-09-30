namespace Hypa.Placement.Domain;

/// <summary>
/// Bind a claimed directory identity to the authenticated local operator.
/// A display name or caller text is not authentication.
/// </summary>
public static class DirectoryActorBinding
{
    public static PlacementOutcome<DirectoryIdentity> Bind(
        DirectoryIdentity authenticated,
        string? claimed)
    {
        if (string.IsNullOrWhiteSpace(claimed))
            return PlacementOutcome<DirectoryIdentity>.Success(authenticated);

        if (!DirectoryIdentity.TryParse(claimed, out var claimedId))
        {
            return PlacementOutcome<DirectoryIdentity>.Failure(
                PlacementReasons.IdentityInvalid,
                "identity is required");
        }

        if (claimedId != authenticated)
        {
            return PlacementOutcome<DirectoryIdentity>.Failure(
                PlacementReasons.Unauthorized,
                "caller identity is not the local operator");
        }

        return PlacementOutcome<DirectoryIdentity>.Success(authenticated);
    }
}
