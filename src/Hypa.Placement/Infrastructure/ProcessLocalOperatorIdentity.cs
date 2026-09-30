using Hypa.Placement.Domain;

namespace Hypa.Placement.Infrastructure;

/// <summary>
/// Local operator identity for directory requests.
/// The OS account principal is the authenticated actor.
/// Process environment and caller argv are not authentication.
/// </summary>
public static class ProcessLocalOperatorIdentity
{
    public const string EnvironmentVariable = "HYPA_OPERATOR_IDENTITY";

    public static bool TryResolve(out DirectoryIdentity identity) =>
        TryResolve(
            configured: null,
            userName: Environment.UserName,
            out identity);

    public static bool TryResolve(
        string? configured,
        string? userName,
        out DirectoryIdentity identity)
    {
        if (DirectoryIdentity.TryParse(userName, out identity))
            return true;
        return DirectoryIdentity.TryParse(configured, out identity);
    }
}
