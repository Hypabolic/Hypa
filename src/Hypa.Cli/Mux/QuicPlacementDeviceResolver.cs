using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Placement.Domain;

namespace Hypa.Cli.Mux;

/// <summary>
/// Check the device that <c>add-quic --device</c> names. With no option, nothing
/// is chosen here: the join tries the paired devices in turn (see
/// <see cref="DevicePairingService.ListJoinCandidatesAsync"/>) and saves the
/// device that the host accepts.
/// </summary>
public sealed class QuicPlacementDeviceResolver
{
    public const string DeviceOption = "--device";

    private readonly DevicePairingService _pairing;

    public QuicPlacementDeviceResolver(DevicePairingService pairing)
    {
        _pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
    }

    public async ValueTask<PlacementOutcome<string?>> ResolveAsync(
        string? requested,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return PlacementOutcome<string?>.Success(null);

        var listed = await _pairing
            .ListAsync(OperatorIdentity.LocalSelfHosted, cancellationToken)
            .ConfigureAwait(false);
        if (!listed.Ok || listed.Value is null)
        {
            return PlacementOutcome<string?>.Failure(
                PlacementReasons.ProfileInvalid,
                $"The pairing store is not readable. Pass {DeviceOption} DEVICE_ID.");
        }

        if (!DeviceId.TryParse(requested.Trim(), out var wanted))
        {
            return PlacementOutcome<string?>.Failure(
                PlacementReasons.ProfileInvalid,
                $"{DeviceOption} is not a device id.");
        }

        var match = listed.Value.FirstOrDefault(d => d.Id.Value == wanted.Value);
        if (match is null || match.Status == DeviceTrustStatus.Revoked)
        {
            return PlacementOutcome<string?>.Failure(
                PlacementReasons.ProfileInvalid,
                $"Device {wanted.Value} is not paired on this machine. Pass {DeviceOption} with a paired device id.");
        }

        if (!await HasKeyAsync(match, cancellationToken).ConfigureAwait(false))
        {
            return PlacementOutcome<string?>.Failure(
                PlacementReasons.ProfileInvalid,
                $"Device {wanted.Value} has no key on this machine. Pass {DeviceOption} with a paired device id.");
        }

        return PlacementOutcome<string?>.Success(wanted.Value);
    }

    private async ValueTask<bool> HasKeyAsync(DeviceRecord device, CancellationToken cancellationToken)
    {
        var key = await _pairing.KeyStore.LoadPrivateKeyAsync(device.Id, cancellationToken)
            .ConfigureAwait(false);
        return key is { Length: > 0 };
    }
}
