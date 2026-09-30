using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>Admit one host-invite redeem. This is not join admission.</summary>
public interface IHostInviteRedeemer
{
    ValueTask<ConnectivityOutcome<DeviceRecord>> RedeemAsync(
        HostInviteRedeemRequest request,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);

    ValueTask<ConnectivityOutcome> RevokeEnrolledAsync(
        HostInviteRevokeRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ConnectivityOutcome> RecordFailedRedeemAttemptAsync(
        InviteId inviteId,
        CancellationToken cancellationToken = default);
}
