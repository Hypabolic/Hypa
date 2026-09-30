using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach.Keys;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach.Overlay;

/// <summary>
/// Input and resize leases for a pane that is not the focused tiled pane.
/// Hidden overlay show, hide, resize, and keys must use this pair.
/// Hidden-target tiled show and hide (later action execution) must use
/// the same helper. Do not pass the current layout resize lease.
/// </summary>
internal sealed class TargetPaneLeasePair
{
    public required string PaneId { get; init; }

    public required string InputLease { get; init; }

    public required string ResizeLease { get; init; }

    public bool InputNewlyGranted { get; init; }

    public bool ResizeNewlyGranted { get; init; }

    public static async Task<TargetPaneLeasePair?> TryClaimAsync(
        IAttachCommandPort control,
        string paneId,
        CancellationToken ct,
        bool takeover = true)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (string.IsNullOrWhiteSpace(paneId))
            return null;

        ClaimedLease? input = null;
        try
        {
            input = await TryClaimAsync(control, paneId, LeaseScopes.Input, ct, takeover)
                .ConfigureAwait(false);
            if (input is null)
                return null;
            var resize = await TryClaimAsync(control, paneId, LeaseScopes.Resize, ct, takeover)
                .ConfigureAwait(false);
            if (resize is null)
            {
                await ReleaseIfNewlyGrantedAsync(control, input, ct).ConfigureAwait(false);
                return null;
            }

            return new TargetPaneLeasePair
            {
                PaneId = paneId,
                InputLease = input.Value.LeaseId,
                ResizeLease = resize.Value.LeaseId,
                InputNewlyGranted = input.Value.NewlyGranted,
                ResizeNewlyGranted = resize.Value.NewlyGranted,
            };
        }
        catch
        {
            await ReleaseIfNewlyGrantedAsync(control, input, ct).ConfigureAwait(false);
            throw;
        }
    }

    public void Track(LeaseRenewLoop? renew)
    {
        renew?.Track(InputLease);
        renew?.Track(ResizeLease);
    }

    public void Untrack(LeaseRenewLoop? renew)
    {
        renew?.Untrack(InputLease);
        renew?.Untrack(ResizeLease);
    }

    public async Task ReleaseAsync(IAttachCommandPort control, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (InputNewlyGranted)
            await ReleaseQuietAsync(control, InputLease, ct).ConfigureAwait(false);
        if (ResizeNewlyGranted)
            await ReleaseQuietAsync(control, ResizeLease, ct).ConfigureAwait(false);
    }

    public bool Matches(string? paneId) =>
        !string.IsNullOrWhiteSpace(paneId)
        && string.Equals(PaneId, paneId, StringComparison.Ordinal);

    private static async Task<ClaimedLease?> TryClaimAsync(
        IAttachCommandPort control,
        string paneId,
        string scope,
        CancellationToken ct,
        bool takeover)
    {
        var result = await control.CallAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["scope"] = scope,
                    ["ttl_ms"] = LeaseRenewLoop.DefaultTtlMs,
                    ["takeover"] = takeover,
                },
                ct)
            .ConfigureAwait(false);
        if (!result.TryGetProperty("lease_id", out var id)
            || id.ValueKind != JsonValueKind.String
            || id.GetString() is not { Length: > 0 } lease)
        {
            return null;
        }

        var outcome = result.TryGetProperty("outcome", out var token)
            && token.ValueKind == JsonValueKind.String
                ? token.GetString()
                : null;
        var newlyGranted = !string.Equals(outcome, LeaseOutcomes.AlreadyHeld, StringComparison.Ordinal);
        return new ClaimedLease(lease, newlyGranted);
    }

    private static async Task ReleaseIfNewlyGrantedAsync(
        IAttachCommandPort control,
        ClaimedLease? claim,
        CancellationToken ct)
    {
        if (claim is not { NewlyGranted: true } owned)
            return;
        await ReleaseQuietAsync(control, owned.LeaseId, ct).ConfigureAwait(false);
    }

    private static async Task ReleaseQuietAsync(
        IAttachCommandPort control,
        string? leaseId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(leaseId))
            return;
        try
        {
            await control.CallAsync(
                    ProtocolMethods.RuntimeLeaseRelease,
                    new JsonObject { ["lease_id"] = leaseId },
                    ct)
                .ConfigureAwait(false);
        }
        catch (ControlPlaneException)
        {
        }
    }

    private readonly record struct ClaimedLease(string LeaseId, bool NewlyGranted);
}
