using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach.Keys;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach.Sidebar;

/// <summary>
/// Claims resize authority on the hidden target pane.
/// Overlay modal input and resize tracking is a later overlay-lane seam.
/// </summary>
public static class HiddenPaneTargetLease
{
    public sealed record Claim(string PaneId, string LeaseId, bool NewlyGranted);

    public static bool RequiresClaim(HiddenPaneAction action) =>
        action is HiddenPaneAction.ShowNewTab
            or HiddenPaneAction.SplitRight
            or HiddenPaneAction.SplitBelow
            or HiddenPaneAction.ShowModal
            or HiddenPaneAction.Hide;

    public static async Task<Result<Claim, HiddenPaneActionError>> ClaimResizeAsync(
        IAttachCommandPort port,
        string paneId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(port);
        if (string.IsNullOrWhiteSpace(paneId))
        {
            return Result<Claim, HiddenPaneActionError>.Fail(
                HiddenPaneActionError.MissingLease("target pane id is required"));
        }

        JsonElement result;
        try
        {
            result = await port.CallAsync(
                    ProtocolMethods.RuntimeLeaseClaim,
                    new JsonObject
                    {
                        ["pane_id"] = paneId,
                        ["scope"] = LeaseScopes.Resize,
                        ["ttl_ms"] = LeaseRenewLoop.DefaultTtlMs,
                        ["takeover"] = false,
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ControlPlaneException or InvalidOperationException)
        {
            return Result<Claim, HiddenPaneActionError>.Fail(
                HiddenPaneActionError.MissingLease(ex.Message));
        }

        var outcome = ReadString(result, "outcome");
        var leaseId = ReadString(result, "lease_id");
        if (string.IsNullOrWhiteSpace(leaseId)
            || (outcome is not (LeaseOutcomes.Granted or LeaseOutcomes.AlreadyHeld)))
        {
            return Result<Claim, HiddenPaneActionError>.Fail(
                HiddenPaneActionError.MissingLease(
                    "target resize lease was not granted: " + (outcome ?? "unknown")));
        }

        return Result<Claim, HiddenPaneActionError>.Ok(
            new Claim(paneId, leaseId, NewlyGranted: outcome == LeaseOutcomes.Granted));
    }

    public static Task ReleaseIfNewlyGrantedAsync(
        IAttachCommandPort port,
        Claim? claim,
        CancellationToken ct)
    {
        if (claim is null || !claim.NewlyGranted)
            return Task.CompletedTask;
        return ReleaseQuietAsync(port, claim, ct);
    }

    public static async Task ReleaseQuietAsync(
        IAttachCommandPort port,
        Claim? claim,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(port);
        if (claim is null || string.IsNullOrWhiteSpace(claim.LeaseId))
            return;

        try
        {
            await port.CallAsync(
                    ProtocolMethods.RuntimeLeaseRelease,
                    new JsonObject { ["lease_id"] = claim.LeaseId },
                    ct)
                .ConfigureAwait(false);
        }
        catch (ControlPlaneException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string? ReadString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
