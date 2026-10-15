using Hypa.Placement.Domain;

namespace Hypa.Placement.Application;

/// <summary>
/// A placement directory that can save the device that a host accepted for a QUIC row.
/// The save goes to the store that holds the row, so the load and the save never differ.
/// </summary>
public interface IQuicDeviceEnrollment
{
    ValueTask<PlacementOutcome<PlacementRecord>> EnrollQuicDeviceAsync(
        DirectoryIdentity owner,
        PlacementId placementId,
        string deviceId,
        CancellationToken cancellationToken = default);
}
