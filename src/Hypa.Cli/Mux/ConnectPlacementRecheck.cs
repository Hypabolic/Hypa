using DeviceId = Hypa.Connectivity.Domain.DeviceId;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;

namespace Hypa.Cli.Mux;

internal readonly record struct ConnectPlacementHold(
    IPlacementDirectory Directory,
    DirectoryIdentity Requester,
    PlacementId Id,
    PlacementRecord Record,
    PlacementDirectoryChangeStamp? Stamp,
    DeviceId? JoinedDevice = null);

/// <summary>
/// Post-dial placement check. An unchanged directory stamp accepts the
/// record from the connect read. A changed stamp, or a stamp that cannot
/// be read, reloads and checks existence, enablement, the dialed profile
/// fields, and requester authorization.
/// </summary>
internal static class ConnectPlacementRecheck
{
    /// <summary>
    /// True when the profile that was dialed is still enabled with the same
    /// dial fields. Label and attention do not change the dial. A plain
    /// placement keeps its kind, mux identity, and an awake reachability.
    /// A QUIC placement with no enrolled device can get the device that this
    /// connect joined with. That enrollment is the change that the connect made
    /// itself. Each other enrolled value is a change.
    /// </summary>
    public static bool SameEnabledProfile(
        PlacementRecord original,
        PlacementRecord current,
        DeviceId? joinedDevice = null)
    {
        if (original.Ssh is { Enabled: true } ssh)
        {
            return current.Ssh is { Enabled: true } next
                && next.Id == ssh.Id
                && string.Equals(next.Target, ssh.Target, StringComparison.Ordinal)
                && string.Equals(next.Session, ssh.Session, StringComparison.Ordinal);
        }

        if (original.Quic is { Enabled: true } quic)
        {
            return current.Quic is { Enabled: true } next
                && next.Id == quic.Id
                && string.Equals(next.Target, quic.Target, StringComparison.Ordinal)
                && string.Equals(next.Session, quic.Session, StringComparison.Ordinal)
                && string.Equals(next.CertificateSha256, quic.CertificateSha256, StringComparison.OrdinalIgnoreCase)
                && SameEnrolledDevice(quic.EnrolledDeviceId, next.EnrolledDeviceId, joinedDevice);
        }

        // A plain placement routes by kind and mux identity. It must still be
        // plain (no SSH or QUIC profile, enabled or not), the same route, and awake.
        return current.Ssh is null
            && current.Quic is null
            && current.Kind == original.Kind
            && current.MuxIdentity == original.MuxIdentity
            && current.Reachability is not (PlacementReachability.Unreachable or PlacementReachability.Asleep);
    }

    private static bool SameEnrolledDevice(string? original, string? current, DeviceId? joinedDevice)
    {
        if (string.Equals(current, original, StringComparison.Ordinal))
            return true;

        return string.IsNullOrWhiteSpace(original)
            && joinedDevice is { } joined
            && !string.IsNullOrEmpty(joined.Value)
            && string.Equals(current, joined.Value, StringComparison.Ordinal);
    }

    public static async ValueTask<bool> StillCurrentAsync(
        ConnectPlacementHold held,
        CubesConnectStageClock? stages,
        CancellationToken cancellationToken)
    {
        if (held.Stamp is { } captured && held.Directory is IPlacementDirectoryChangeStamp probe)
        {
            try
            {
                if (probe.ReadChangeStamp() == captured)
                    return true;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        PlacementDirectoryChangeStamp? beforeReload = null;
        if (held.Directory is IPlacementDirectoryChangeStamp reloadProbe)
        {
            try
            {
                beforeReload = reloadProbe.ReadChangeStamp();
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        PlacementOutcome<PlacementRecord> found;
        try
        {
            found = await held.Directory
                .GetForConnectAsync(held.Requester, held.Id, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }

        if (!found.Ok || found.Value is null)
            return false;
        var joinedDevice = held.JoinedDevice;
        if (joinedDevice is null && stages is not null && stages.TryJoinedDevice(held.Id.Value, out var noted))
            joinedDevice = noted;
        if (!SameEnabledProfile(held.Record, found.Value, joinedDevice))
            return false;
        if (beforeReload is not { } started || held.Directory is not IPlacementDirectoryChangeStamp confirm)
            return true;

        try
        {
            if (confirm.ReadChangeStamp() != started)
                return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        stages?.RememberPlacement(found.Value, started, held.Directory);
        return true;
    }
}
