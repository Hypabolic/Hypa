using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach.Chrome;
using Hypa.ControlPlane;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Overlay;

namespace Hypa.Cli.Attach;

public sealed partial class AttachSession
{
    internal static bool ApplyOverlayPlacement(AttachLiveState live, JsonElement payload)
    {
        ArgumentNullException.ThrowIfNull(live);
        var mode = TryJsonString(payload, "mode");
        var paneId = TryJsonString(payload, "pane_id");
        var eventClient = TryJsonString(payload, "attach_client_id");
        var generation = payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("overlay_generation", out var gen)
            && gen.ValueKind == JsonValueKind.Number
            && gen.TryGetInt64(out var value)
                ? value
                : 0L;
        if (!live.Overlay.Matches(live.AttachClientId, eventClient))
            return false;

        if (string.Equals(mode, PaneOverlayWire.Overlay, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(paneId))
        {
            if (AttachClientModePublication.HumanExclusiveSurfaceOpen(live))
                return false;
            var geometry = ResolveOverlayGeometry(live);
            if (geometry is null)
                return false;
            if (!live.Overlay.ApplyServerShow(paneId, generation, geometry))
                return false;
            live.ClearLastPainted(paneId);
            live.Engine.OverlayOpen = true;
            live.Engine.OverlayPaneId = paneId;
            return true;
        }

        if (string.Equals(mode, PaneOverlayWire.Hidden, StringComparison.OrdinalIgnoreCase))
        {
            if (!live.Overlay.ApplyServerHide(paneId, generation))
                return false;
            live.Engine.OverlayOpen = false;
            live.Engine.OverlayPaneId = null;
            return true;
        }

        return false;
    }

    internal static async Task<bool> ReconcileOverlayPlacementEventAsync(
        IAttachCommandPort? control,
        AttachLiveState live,
        JsonElement payload,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        var mode = TryJsonString(payload, "mode");
        var paneId = TryJsonString(payload, "pane_id");
        var eventClient = TryJsonString(payload, "attach_client_id");
        var generation = payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("overlay_generation", out var gen)
            && gen.ValueKind == JsonValueKind.Number
            && gen.TryGetInt64(out var value)
                ? value
                : 0L;
        // slot is set.
        // surface before the next modal. Reject-before-apply must fence G
        // so a later replay cannot own the slot. A persist-fail cancel
        // retries the same pane and generation only.
        if (string.Equals(mode, PaneOverlayWire.Overlay, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(paneId)
            && live.Overlay.Matches(live.AttachClientId, eventClient)
            && (AttachClientModePublication.HumanExclusiveSurfaceOpen(live)
                || live.Overlay.MatchesRecoverableCancel(paneId, generation)))
        {
            await CancelOwnedOverlayReservationAsync(control, live, paneId, generation, ct)
                .ConfigureAwait(false);
            return false;
        }

        var changed = ApplyOverlayPlacement(live, payload);
        ApplyLocalOverlayVisibility(live, payload);
        return changed;
    }

    internal static bool ApplyOverlayPaneExit(AttachLiveState live, JsonElement payload)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!live.Overlay.OwnsModal)
            return false;
        var paneId = TryJsonString(payload, "pane_id");
        if (string.IsNullOrWhiteSpace(paneId)
            || !string.Equals(paneId, live.Overlay.PaneId, StringComparison.Ordinal))
        {
            return false;
        }

        var state = TryJsonString(payload, "state") ?? TryJsonString(payload, "action");
        if (!string.Equals(state, "exited", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(state, "closed", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(state, "dead", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var generation = payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("overlay_generation", out var gen)
            && gen.ValueKind == JsonValueKind.Number
            && gen.TryGetInt64(out var value)
                ? value
                : live.Overlay.AppliedGeneration + 1;
        if (!live.Overlay.ApplyServerHide(paneId, generation))
            return false;
        live.Engine.OverlayOpen = false;
        live.Engine.OverlayPaneId = null;
        return true;
    }

    internal static PopupGeometryResult? ResolveOverlayGeometry(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var content = live.Chrome?.Content ?? new CellRect(0, 0, 80, 24);
        return ClientOverlayInput.ResolveForContent(content);
    }

    internal static bool SyncOverlayGeometry(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!live.Overlay.OwnsModal)
            return false;
        var geometry = ResolveOverlayGeometry(live);
        if (geometry is null)
            return false;
        var before = live.Overlay.Geometry;
        live.Overlay.UpdateGeometry(geometry);
        return before is null
            || before.InnerCols != geometry.InnerCols
            || before.InnerRows != geometry.InnerRows
            || before.OuterCol != geometry.OuterCol
            || before.OuterRow != geometry.OuterRow;
    }

    internal static async Task PrepareOverlayAsync(
        IAttachCommandPort? control,
        AttachLiveState live,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!live.Overlay.OwnsModal || live.Overlay.Geometry is not { } geometry)
            return;
        if (control is null)
        {
            await FailPrepareCleanupAsync(control, live, ct).ConfigureAwait(false);
            return;
        }

        var paneId = live.Overlay.PaneId!;
        if (live.OverlayLeases is not { } leases || !leases.Matches(paneId))
        {
            live.OverlayLeases?.Untrack(live.Renew);
            if (live.OverlayLeases is { } stale)
                await stale.ReleaseAsync(control, ct).ConfigureAwait(false);
            try
            {
                leases = await TargetPaneLeasePair.TryClaimAsync(control, paneId, ct)
                    .ConfigureAwait(false);
            }
            catch (ControlPlaneException)
            {
                await FailPrepareCleanupAsync(control, live, ct).ConfigureAwait(false);
                return;
            }

            live.OverlayLeases = leases;
            leases?.Track(live.Renew);
        }

        if (leases is null)
        {
            await FailPrepareCleanupAsync(control, live, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await control.CallAsync(
                    ProtocolMethods.PaneResize,
                    new JsonObject
                    {
                        ["pane_id"] = paneId,
                        ["lease_id"] = leases.ResizeLease,
                        ["cols"] = geometry.InnerCols,
                        ["rows"] = geometry.InnerRows,
                    },
                    ct)
                .ConfigureAwait(false);
            var barrier = 0L;
            if (live.TryGetPaneFrame(paneId, out var cached) && cached is not null)
                barrier = cached.Generation;
            live.Overlay.NoteResized(barrier);
        }
        catch (ControlPlaneException)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(live.RenderSub))
        {
            try
            {
                await control.CallAsync(
                        ProtocolMethods.TerminalObserve,
                        new JsonObject
                        {
                            ["pane_id"] = paneId,
                            ["subscription_id"] = live.RenderSub,
                            ["replace"] = false,
                        },
                        ct)
                    .ConfigureAwait(false);
            }
            catch (ControlPlaneException)
            {
            }
        }
    }

    internal static async Task HideOwnedOverlayAsync(
        IAttachCommandPort? control,
        AttachLiveState live,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!live.Overlay.OwnsModal)
        {
            if (live.Overlay.HasRecoverableCancel)
            {
                await CancelOwnedOverlayReservationAsync(
                        control,
                        live,
                        live.Overlay.RecoverableCancelPaneId,
                        live.Overlay.RecoverableCancelGeneration,
                        ct)
                    .ConfigureAwait(false);
            }

            return;
        }

        var paneId = live.Overlay.PaneId!;
        var generation = live.Overlay.Generation;
        var leaseId = live.OverlayLeases?.InputLease;
        if (control is null || string.IsNullOrWhiteSpace(leaseId))
        {
            await CancelOwnedOverlayReservationAsync(control, live, paneId, generation, ct)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            await control.CallAsync(
                    ProtocolMethods.PaneHide,
                    new JsonObject
                    {
                        ["pane_id"] = paneId,
                        ["lease_id"] = leaseId,
                        ["attach_client_id"] = live.AttachClientId,
                        ["overlay_generation"] = generation,
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch (ControlPlaneException ex) when (IsOverlayHideReleased(ex))
        {
            await FinishReleasedOverlayAsync(control, live, paneId, generation, ct)
                .ConfigureAwait(false);
            return;
        }
        catch (ControlPlaneException ex)
        {
            live.StatusError = FormatStatus(ex);
            return;
        }

        await FinishReleasedOverlayAsync(control, live, paneId, generation, ct)
            .ConfigureAwait(false);
    }

    internal static async Task FailPrepareCleanupAsync(
        IAttachCommandPort? control,
        AttachLiveState live,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        var paneId = live.Overlay.PaneId;
        var generation = live.Overlay.Generation;
        await CancelOwnedOverlayReservationAsync(control, live, paneId, generation, ct)
            .ConfigureAwait(false);
        try
        {
            if (control is not null)
                await PublishClientModeAsync(control, live, ct).ConfigureAwait(false);
        }
        catch (ControlPlaneException)
        {
        }
    }

    internal static async Task CancelOwnedOverlayReservationAsync(
        IAttachCommandPort? control,
        AttachLiveState live,
        string? paneId,
        long generation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        var retainLocal = false;
        if (control is not null
            && !string.IsNullOrWhiteSpace(paneId)
            && !string.IsNullOrWhiteSpace(live.AttachClientId))
        {
            try
            {
                await control.CallAsync(
                        ProtocolMethods.PaneHide,
                        new JsonObject
                        {
                            ["pane_id"] = paneId,
                            ["attach_client_id"] = live.AttachClientId,
                            ["overlay_generation"] = generation,
                        },
                        ct)
                    .ConfigureAwait(false);
            }
            catch (ControlPlaneException ex) when (IsOverlayHideReleased(ex))
            {
            }
            catch (ControlPlaneException ex)
            {
                retainLocal = true;
                if (!live.Overlay.OwnsModal)
                    live.Overlay.NoteRecoverableCancel(paneId, generation);
                live.StatusError = FormatStatus(ex);
            }
        }

        if (retainLocal)
            return;

        FinishCancelledOverlay(live, paneId, generation);
        if (live.Overlay.OwnsModal)
            return;

        await ReleaseOverlayLeasesAsync(control, live, ct).ConfigureAwait(false);
        live.OverlayPaneId = null;
    }

    internal static async Task ReleaseOverlayLeasesAsync(
        IAttachCommandPort? control,
        AttachLiveState live,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.OverlayLeases is not { } leases)
            return;
        leases.Untrack(live.Renew);
        if (control is not null)
            await leases.ReleaseAsync(control, ct).ConfigureAwait(false);
        live.OverlayLeases = null;
    }

    internal static void ClearLocalOverlay(AttachLiveState live, bool resetEventFence = false)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (resetEventFence)
            live.Overlay.ResetConnectionFence();
        else
            live.Overlay.AcknowledgeLocalHide();
        live.Engine.OverlayOpen = false;
        live.Engine.OverlayPaneId = null;
        live.OverlayLeases?.Untrack(live.Renew);
        live.OverlayLeases = null;
    }

    /// <summary>
    /// when the overlay slot is set. Recoverable cancel is not that
    // / slot.
    /// after the exclusive surface closes. Bare Escape then retries
    /// the pending owner cancel. Other keys stay on the parent pane.
    /// </summary>
    internal static IReadOnlyList<KeyEngineEvent> RouteRecoverableCancelKeys(
        AttachLiveState live,
        IReadOnlyList<KeyEngineEvent> events,
        ReadOnlySpan<byte> decoded)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(events);
        if (!live.Overlay.HasRecoverableCancel)
            return events;
        if (AttachClientModePublication.HumanExclusiveSurfaceOpen(live))
            return events;
        if (live.Engine.OverlayOpen)
            return events;
        if (decoded.Length != 1 || decoded[0] != 0x1b)
            return events;
        if (events.Any(ev => ev.Kind == KeyEngineEventKind.HideOverlay))
            return events;

        return
        [
            new KeyEngineEvent(
                KeyEngineEventKind.HideOverlay,
                TargetId: live.Overlay.RecoverableCancelPaneId),
        ];
    }

    internal static async Task ApplyHideOverlayEventAsync(
        UnixRawTerminal? tty,
        AttachLiveState live,
        IAttachCommandPort control,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        await HideOwnedOverlayAsync(control, live, ct).ConfigureAwait(false);
        await PublishClientModeAsync(control, live, ct).ConfigureAwait(false);
        if (tty is not null)
            PaintChrome(tty, live);
    }

    internal static bool OverlayDispatchReady(AttachLiveState live, string? targetId)
    {
        ArgumentNullException.ThrowIfNull(live);
        return live.Overlay.OwnsModal
            && string.Equals(targetId, live.Overlay.PaneId, StringComparison.Ordinal)
            && live.Overlay.Admit(live.Overlay.Generation)
            && live.OverlayLeases is { InputLease: { Length: > 0 } };
    }

    internal static async Task PublishClientModeAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(live);
        // input when overlay is set. One host-size cell frame, then encode once.
        control = LiveControlPort(live, control);
        var token = AttachClientModePublication.PublishedToken(live);
        if (live.Engine.OverlayOpen && !live.Worktrees.IsOpen)
            token = "overlay";
        try
        {
            await control.CallAsync(
                    ProtocolMethods.UiClientMode,
                    new JsonObject { ["client_mode"] = token },
                    ct)
                .ConfigureAwait(false);
        }
        catch (ControlPlaneException ex) when (IsUiBusyStatus(ex))
        {
            RevertRejectedHumanModal(live);
        }
    }

    internal static bool IsUiBusyStatus(ControlPlaneException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return string.Equals(ex.Message, "ui_busy", StringComparison.Ordinal);
    }

    internal static bool IsOverlayHideReleased(ControlPlaneException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ex.Code == ProtocolErrorCodes.Fenced
            || ex.Code == ProtocolErrorCodes.NotFound)
        {
            return true;
        }

        return ex.Code == ProtocolErrorCodes.InvalidState && IsUiBusyStatus(ex);
    }

    internal static void FinishCancelledOverlay(
        AttachLiveState live,
        string? paneId,
        long generation)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!string.IsNullOrWhiteSpace(paneId))
            live.Overlay.ApplyServerHide(paneId, generation);
        else
            live.Overlay.AcknowledgeLocalHide();

        if (live.Overlay.OwnsModal)
            return;

        live.Engine.OverlayOpen = false;
        live.Engine.OverlayPaneId = null;
        if (!live.Overlay.HasRecoverableCancel)
            ClearTransientStatusError(live);
    }

    internal static async Task FinishReleasedOverlayAsync(
        IAttachCommandPort? control,
        AttachLiveState live,
        string? paneId,
        long generation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        FinishCancelledOverlay(live, paneId, generation);
        if (live.Overlay.OwnsModal)
            return;

        await ReleaseOverlayLeasesAsync(control, live, ct).ConfigureAwait(false);
        live.OverlayPaneId = null;
    }

    internal static void RevertRejectedHumanModal(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.Worktrees.IsOpen)
            live.Worktrees.Cancel();
        if (live.MouseMenu is not null)
        {
            if (live.Engine.Mode is AttachClientMode.GlobalMenu)
                live.Engine.LeaveGlobalMenu();
            else if (live.Engine.Mode is AttachClientMode.ContextMenu)
                live.Engine.LeaveContextMenu();
            live.MouseMenu = null;
        }

        if (live.Engine.Mode is not (AttachClientMode.Terminal or AttachClientMode.Prefix)
            && !live.Engine.OverlayOpen)
        {
            // Closing settings changes integration load state. Use the input/paint lock.
            live.WithPaint(() => _ = live.Engine.ForceTerminalForPopup());
        }

        live.Engine.ExclusiveSurfaceOpen = ExclusiveClientSurfaceOpen(live);
        live.Dispatcher.ClientMode = AttachClientModePublication.PublishedToken(live);
        ReconcilePrefixAsciiInput(live);
        live.StatusError = "ui_busy";
    }

    internal static JsonObject OverlaySendKeysBody(AttachLiveState live, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(payload);
        return new JsonObject
        {
            ["pane_id"] = live.Overlay.PaneId,
            ["lease_id"] = live.OverlayLeases?.InputLease,
            ["encoding"] = "base64",
            ["data"] = Convert.ToBase64String(payload),
            ["overlay_generation"] = live.Overlay.Generation,
        };
    }

    internal static bool OverlayIsVisiblePane(AttachLiveState live, string? paneId) =>
        live.Overlay.OwnsModal
        && !string.IsNullOrWhiteSpace(paneId)
        && string.Equals(live.Overlay.PaneId, paneId, StringComparison.Ordinal);
}
