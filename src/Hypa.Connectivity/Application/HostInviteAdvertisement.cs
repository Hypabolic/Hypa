using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>Choose the addresses a host invite carries.</summary>
public static class HostInviteAdvertisement
{
    public static ConnectivityOutcome<IReadOnlyList<string>> Resolve(
        IReadOnlyList<string> interfaceAddresses,
        string? namedAddress,
        string? bindAddress,
        int port)
    {
        ArgumentNullException.ThrowIfNull(interfaceAddresses);
        if (!HostInviteReach.TrySelectAdvertised(
                interfaceAddresses,
                namedAddress,
                bindAddress,
                port,
                out var hosts,
                out var error))
        {
            return ConnectivityOutcome<IReadOnlyList<string>>.Failure(
                ConnectivityReasons.InviteInvalid,
                error ?? "invite host is invalid");
        }

        return ConnectivityOutcome<IReadOnlyList<string>>.Success(hosts);
    }
}
