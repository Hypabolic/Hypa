using System.Text.Json;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;

namespace Hypa.Cli.Attach.Cubes;

/// <summary>
/// Hold the share dialog on a paired flash, then dismiss it.
/// </summary>
internal static class CubeSharePairingWatch
{
    internal static readonly TimeSpan DismissAfter = TimeSpan.FromMilliseconds(1200);

    internal static bool TryMarkPaired(CubeShareState share, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(share);
        if (share.Paired || !share.Running || share.Starting)
            return false;
        share.Paired = true;
        share.PairedAt = now;
        return true;
    }

    internal static bool ShouldDismiss(CubeShareState share, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(share);
        return share.Paired
            && share.PairedAt is { } at
            && now - at >= DismissAfter;
    }

    /// <summary>
    /// Existing accept has no helper stdout. Redeem writes
    /// <c>consumed</c> on the host invite in the pairing store.
    /// </summary>
    internal static async Task<bool> InviteWasConsumedAsync(
        string? transferValue,
        CancellationToken cancellationToken = default,
        string? pairingStore = null)
    {
        if (string.IsNullOrWhiteSpace(transferValue))
            return false;
        var decoded = HostInviteCodec.Decode(transferValue);
        if (!decoded.Ok || decoded.Value is null)
            return false;

        try
        {
            var directory = string.IsNullOrWhiteSpace(pairingStore)
                ? DevicePairingStatePaths.ResolveFromEnvironment()
                : Path.GetFullPath(pairingStore);
            var store = new FileDevicePairingStore(directory);
            var snapshot = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            foreach (var invite in snapshot.HostInvites)
            {
                if (!string.Equals(invite.Id.Value, decoded.Value.InviteId.Value, StringComparison.Ordinal))
                    continue;
                return invite.Consumed || invite.RedeemedBy is not null;
            }
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
        catch (JsonException)
        {
            return false;
        }

        return false;
    }
}
