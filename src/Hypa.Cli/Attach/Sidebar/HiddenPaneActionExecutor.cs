using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Overlay;

namespace Hypa.Cli.Attach.Sidebar;

/// <summary>
/// Runs planned hidden-pane RPCs. Show in new tab uses a JSON boolean
/// <c>create_pane</c> false. Failed show uses atomic <c>if_empty</c> close.
/// A later focus failure does not close a tab that already holds the
/// shown pane. <see cref="RunAsync"/> claims a target-scoped resize
/// lease first. Overlay modal input and resize tracking stays on the
/// overlay lane.
/// </summary>
public static class HiddenPaneActionExecutor
{
    public static async Task<HiddenPaneActionResult> RunAsync(
        HiddenPaneActionRequest request,
        IReadOnlyList<HiddenPaneRecord> catalog,
        IAttachCommandPort port,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(port);

        HiddenPaneTargetLease.Claim? claim = null;
        TargetPaneLeasePair? modalPair = null;
        if (request.Action is HiddenPaneAction.ShowModal)
        {
            modalPair = await TargetPaneLeasePair.TryClaimAsync(
                    port,
                    request.PaneId,
                    ct,
                    takeover: false)
                .ConfigureAwait(false);
            if (modalPair is null)
            {
                return HiddenPaneActionResult.Fail(
                    HiddenPaneActionError.MissingLease("target overlay leases were not granted"),
                    request.PriorTabId,
                    request.PriorWorkspaceId);
            }

            request = request with { LeaseId = modalPair.ResizeLease };
        }
        else if (HiddenPaneTargetLease.RequiresClaim(request.Action))
        {
            var claimed = await HiddenPaneTargetLease.ClaimResizeAsync(port, request.PaneId, ct)
                .ConfigureAwait(false);
            if (!claimed.IsOk)
            {
                return HiddenPaneActionResult.Fail(
                    claimed.Error,
                    request.PriorTabId,
                    request.PriorWorkspaceId);
            }

            claim = claimed.Value;
            request = request with { LeaseId = claim.LeaseId };
        }

        var planned = HiddenPaneActionPlanner.Plan(request, catalog);
        if (!planned.IsOk)
        {
            if (modalPair is not null)
                await modalPair.ReleaseAsync(port, ct).ConfigureAwait(false);
            await HiddenPaneTargetLease.ReleaseIfNewlyGrantedAsync(port, claim, ct)
                .ConfigureAwait(false);
            return HiddenPaneActionResult.Fail(
                planned.Error,
                request.PriorTabId,
                request.PriorWorkspaceId);
        }

        var outcome = await ExecuteAsync(planned.Value, port, ct).ConfigureAwait(false);
        if (!outcome.Succeeded)
        {
            if (modalPair is not null)
                await modalPair.ReleaseAsync(port, ct).ConfigureAwait(false);
            await HiddenPaneTargetLease.ReleaseIfNewlyGrantedAsync(port, claim, ct)
                .ConfigureAwait(false);
            return outcome;
        }

        if (request.Action is HiddenPaneAction.Hide && !request.OverlayVisible)
        {
            await HiddenPaneTargetLease.ReleaseQuietAsync(port, claim, ct)
                .ConfigureAwait(false);
        }

        return outcome with
        {
            TargetLeaseId = modalPair?.ResizeLease ?? claim?.LeaseId,
            TargetLeaseNewlyGranted = modalPair?.ResizeNewlyGranted ?? claim?.NewlyGranted ?? false,
            TargetInputLeaseId = modalPair?.InputLease,
            TargetInputLeaseNewlyGranted = modalPair?.InputNewlyGranted ?? false,
        };
    }

    public static JsonObject ToJson(HiddenPanePlannedCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var obj = new JsonObject();
        if (call.PaneId is not null)
            obj["pane_id"] = call.PaneId;
        if (call.TabId is not null)
            obj["tab_id"] = call.TabId;
        if (call.WorkspaceId is not null)
            obj["workspace_id"] = call.WorkspaceId;
        if (call.TargetPaneId is not null)
            obj["target_pane_id"] = call.TargetPaneId;
        if (call.Direction is not null)
            obj["direction"] = call.Direction;
        if (call.Mode is not null)
            obj["mode"] = call.Mode;
        if (call.LeaseId is not null)
            obj["lease_id"] = call.LeaseId;
        if (call.AttachClientId is not null)
            obj["attach_client_id"] = call.AttachClientId;
        if (call.CreatePane is { } createPane)
            obj["create_pane"] = createPane;
        return obj;
    }

    public static async Task<HiddenPaneActionResult> ExecuteAsync(
        HiddenPaneActionPlan plan,
        IAttachCommandPort port,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(port);

        string? createdTabId = null;
        string? createdWorkspaceId = null;
        var showCompleted = false;
        try
        {
            foreach (var call in plan.Calls)
            {
                if (call.IsCleanup)
                    continue;

                var body = ToJson(call);
                if (plan.CreateEmptyTab
                    && string.Equals(call.Method, ProtocolMethods.PaneShow, StringComparison.Ordinal)
                    && createdTabId is not null)
                {
                    body["tab_id"] = createdTabId;
                }

                var result = await port.CallAsync(call.Method, body, ct).ConfigureAwait(false);
                if (string.Equals(call.Method, ProtocolMethods.TabCreate, StringComparison.Ordinal))
                {
                    createdTabId = ReadString(result, "tab_id");
                    createdWorkspaceId = ReadString(result, "workspace_id");
                    if (string.IsNullOrWhiteSpace(createdTabId))
                    {
                        return HiddenPaneActionResult.Fail(
                            HiddenPaneActionError.NotFound("tab.create returned no tab_id"),
                            plan.PriorTabId,
                            plan.PriorWorkspaceId);
                    }
                }
                else if (string.Equals(call.Method, ProtocolMethods.PaneShow, StringComparison.Ordinal))
                {
                    showCompleted = true;
                }
            }
        }
        catch (Exception ex)
        {
            return await FailAfterMutationAsync(
                    plan,
                    port,
                    createdTabId,
                    createdWorkspaceId,
                    showCompleted,
                    ex)
                .ConfigureAwait(false);
        }

        if (plan.FocusCreatedTab && createdTabId is not null)
        {
            if (!await TryFocusAsync(port, createdTabId, createdWorkspaceId, ct).ConfigureAwait(false))
            {
                await TryFocusAsync(
                        port,
                        createdTabId,
                        createdWorkspaceId,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        return HiddenPaneActionResult.Ok(createdTabId, createdWorkspaceId);
    }

    private static async Task<HiddenPaneActionResult> FailAfterMutationAsync(
        HiddenPaneActionPlan plan,
        IAttachCommandPort port,
        string? createdTabId,
        string? createdWorkspaceId,
        bool showCompleted,
        Exception error)
    {
        if (showCompleted)
        {
            await TryFocusAsync(
                    port,
                    createdTabId,
                    createdWorkspaceId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return HiddenPaneActionResult.Ok(createdTabId, createdWorkspaceId);
        }

        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var cleanupCt = cleanup.Token;
        string? cleanedTabId = null;
        if (plan.CleanupCreatedTabOnFailure && createdTabId is not null)
        {
            try
            {
                var closed = await port.CallAsync(
                        ProtocolMethods.TabClose,
                        new JsonObject
                        {
                            ["tab_id"] = createdTabId,
                            ["if_empty"] = true,
                        },
                        cleanupCt)
                    .ConfigureAwait(false);
                if (ReadClosed(closed))
                    cleanedTabId = createdTabId;
            }
            catch
            {
                // Keep the first error. The empty tab close is best effort.
            }
        }

        string? restoredTab = null;
        string? restoredWorkspace = null;
        if (plan.RestoreFocusOnFailure)
        {
            restoredTab = plan.PriorTabId;
            restoredWorkspace = plan.PriorWorkspaceId;
            await TryFocusAsync(port, plan.PriorTabId, plan.PriorWorkspaceId, cleanupCt)
                .ConfigureAwait(false);
        }

        return HiddenPaneActionResult.Fail(
            HiddenPaneActionError.InvalidAction(error.Message),
            restoredTab,
            restoredWorkspace,
            cleanedTabId);
    }

    private static async Task<bool> TryFocusAsync(
        IAttachCommandPort port,
        string? tabId,
        string? workspaceId,
        CancellationToken ct)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(tabId))
            {
                await port.CallAsync(
                        ProtocolMethods.TabFocus,
                        new JsonObject { ["tab_id"] = tabId },
                        ct)
                    .ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(workspaceId))
            {
                await port.CallAsync(
                        ProtocolMethods.WorkspaceFocus,
                        new JsonObject { ["workspace_id"] = workspaceId },
                        ct)
                    .ConfigureAwait(false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ReadClosed(JsonElement el) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty("closed", out var value)
        && value.ValueKind == JsonValueKind.True;

    private static string? ReadString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public sealed record HiddenPaneActionResult
{
    public required bool Succeeded { get; init; }
    public string? FocusTabId { get; init; }
    public string? FocusWorkspaceId { get; init; }
    public string? RestoredTabId { get; init; }
    public string? RestoredWorkspaceId { get; init; }
    public string? CleanedTabId { get; init; }
    public string? TargetLeaseId { get; init; }
    public bool TargetLeaseNewlyGranted { get; init; }
    public string? TargetInputLeaseId { get; init; }
    public bool TargetInputLeaseNewlyGranted { get; init; }
    public HiddenPaneActionError? Error { get; init; }

    public static HiddenPaneActionResult Ok(string? tabId, string? workspaceId) =>
        new()
        {
            Succeeded = true,
            FocusTabId = tabId,
            FocusWorkspaceId = workspaceId,
        };

    public static HiddenPaneActionResult Fail(
        HiddenPaneActionError error,
        string? restoredTabId,
        string? restoredWorkspaceId,
        string? cleanedTabId = null) =>
        new()
        {
            Succeeded = false,
            Error = error,
            RestoredTabId = restoredTabId,
            RestoredWorkspaceId = restoredWorkspaceId,
            CleanedTabId = cleanedTabId,
        };
}
