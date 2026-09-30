using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Mux;
using Hypa.Continuity.Application;

namespace Hypa.Cli.Attach;

internal sealed record PendingMoveWorkConfirm(
    ContextMenuItem Item,
    MouseEngineResult Result);

internal sealed record PendingTransferPick(MouseEngineResult Result);

public sealed partial class AttachSession
{
    internal const string SamePlacementDetail = "source and dest are the same placement";
    internal const string UnknownSourcePlacementDetail = "source placement is unknown";

    internal const string UiBusyDetail = "ui_busy";

    internal static async Task BeginTransferPickerAsync(
        ContextMenuItem item,
        MouseEngineResult result,
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        if (!live.Release.ContinuityEnabled)
            return;
        _ = item;

        if (live.Engine.PopupOpen
            || live.Engine.Mode is not (AttachClientMode.Terminal
                or AttachClientMode.ContextMenu
                or AttachClientMode.GlobalMenu))
        {
            live.StatusError = UiBusyDetail;
            if (tty is not null)
                PaintChrome(tty, live);
            return;
        }

        var paneId = ResolveTransferPaneId(live, result, out var ownerError);
        if (live.MoveWork is null || string.IsNullOrWhiteSpace(paneId) || ownerError is not null)
        {
            live.StatusError = ownerError ?? AdoptedWorkOwnerResolver.AdoptRequiredDetail;
            if (tty is not null)
                PaintChrome(tty, live);
            return;
        }

        live.PendingTransfer = new PendingTransferPick(result with { PaneId = paneId });
        await ApplyModeEventsAsync(
                live.Engine.EnterTransferPicker(),
                live,
                control,
                tty,
                linked: null,
                ct)
            .ConfigureAwait(false);
        HydrateTransferCatalog(live);
        if (tty is not null)
            PaintChrome(tty, live);
    }

    internal static async Task CompletePendingTransferPickAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        if (!live.Release.ContinuityEnabled)
        {
            live.PendingTransfer = null;
            return;
        }

        var destPlacementId = live.Engine.TakeTransferPlacementId();
        var pending = live.PendingTransfer;
        live.PendingTransfer = null;
        if (string.IsNullOrWhiteSpace(destPlacementId) || pending is null)
            return;

        var item = new ContextMenuItem(
            ContextMenuModel.MoveWorkPrefix + destPlacementId,
            ContextMenuModel.TransferLabel);
        await BeginMoveWorkConfirmAsync(item, pending.Result, live, control, tty, ct)
            .ConfigureAwait(false);
    }

    internal static string? ResolveTransferPaneId(
        AttachLiveState live,
        MouseEngineResult result,
        out string? ownerError)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(result);
        ownerError = null;
        if (!string.IsNullOrWhiteSpace(result.WorkspaceId)
            && string.IsNullOrWhiteSpace(result.PaneId))
        {
            var adopted = AdoptedWorkOwnerResolver.AdoptedPaneIdsInWorkspace(
                PanesFromSnapshot(live.LastSnapshot),
                result.WorkspaceId);
            if (adopted.Count == 0)
            {
                ownerError = AdoptedWorkOwnerResolver.AdoptRequiredDetail;
                return null;
            }

            if (adopted.Count != 1)
            {
                ownerError = AdoptedWorkOwnerResolver.MultipleWorkspacePanesDetail;
                return null;
            }

            return adopted[0];
        }

        var paneId = !string.IsNullOrWhiteSpace(result.PaneId)
            ? result.PaneId
            : (string.IsNullOrWhiteSpace(live.PaneId) ? null : live.PaneId);
        if (!HasAdoptedWorkHint(live, paneId))
        {
            ownerError = AdoptedWorkOwnerResolver.AdoptRequiredDetail;
            return null;
        }

        return paneId;
    }

    internal static string? ResolveTransferPaneId(AttachLiveState live, MouseEngineResult result) =>
        ResolveTransferPaneId(live, result, out _);

    internal static bool HasAdoptedWorkHint(AttachLiveState live, string? paneId)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (string.IsNullOrWhiteSpace(paneId))
            return false;
        var panes = PanesFromSnapshot(live.LastSnapshot);
        return panes.Any(pane =>
            string.Equals(pane.PaneId, paneId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(pane.WorkId));
    }

    internal static async Task BeginMoveWorkConfirmAsync(
        ContextMenuItem item,
        MouseEngineResult result,
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        if (!live.Release.ContinuityEnabled)
            return;

        var destPlacementId = ContextMenuModel.MoveWorkPlacementId(item.Id, result.PlacementId);
        if (string.IsNullOrWhiteSpace(destPlacementId))
            return;

        var dest = live.Cubes.FirstOrDefault(
            cube => string.Equals(cube.Id, destPlacementId, StringComparison.Ordinal));
        if (dest is null)
        {
            live.StatusError = "placement is not in the Cubes list";
            if (tty is not null)
                PaintChrome(tty, live);
            return;
        }

        if (live.MoveWork is null)
        {
            live.StatusError = AdoptedWorkOwnerResolver.AdoptRequiredDetail;
            if (tty is not null)
                PaintChrome(tty, live);
            return;
        }

        if (RefuseUnknownOrSamePlacement(live, destPlacementId, tty))
            return;

        var source = ResolveSourcePlacement(live, destPlacementId);
        if (source is null || IsSamePlacement(source.Id, destPlacementId))
        {
            live.StatusError = source is null
                ? UnknownSourcePlacementDetail
                : SamePlacementDetail;
            if (tty is not null)
                PaintChrome(tty, live);
            return;
        }

        var workTitle = ResolveWorkTitle(live, source, result);
        var prompt = MoveWorkConfirmPrompt.Format(
            workTitle,
            MoveWorkConfirmPrompt.PlacementLabel(source.Name, source.Kind),
            MoveWorkConfirmPrompt.PlacementLabel(dest.Name, dest.Kind));
        live.PendingMoveWork = new PendingMoveWorkConfirm(item, result);
        await ApplyModeEventsAsync(
                live.Engine.EnterConfirmMoveWork(prompt),
                live,
                control,
                tty,
                linked: null,
                ct)
            .ConfigureAwait(false);
        if (tty is not null)
            PaintChrome(tty, live);
    }

    internal static async Task ApplyMoveWorkConfirmDecisionAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        bool accept,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        if (live.Engine.Mode is not AttachClientMode.ConfirmMoveWork)
            return;
        IReadOnlyList<KeyEngineEvent> events = accept
            ? live.Engine.Feed((byte)'y')
            : live.Engine.Feed((byte)'n');
        await ApplyModeEventsAsync(events, live, control, tty, linked: null, ct)
            .ConfigureAwait(false);
        if (tty is not null)
            PaintChrome(tty, live);
    }

    internal static async Task CompletePendingMoveWorkAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        if (!live.Release.ContinuityEnabled)
        {
            live.PendingMoveWork = null;
            return;
        }

        var pending = live.PendingMoveWork;
        var accepted = live.Engine.TakeConfirmMoveWorkAccept();
        live.PendingMoveWork = null;
        if (!accepted || pending is null)
            return;

        await ApplyMoveWorkAsync(pending.Item, pending.Result, live, control, tty, ct)
            .ConfigureAwait(false);
    }

    internal static async Task ApplyMoveWorkAsync(
        ContextMenuItem item,
        MouseEngineResult result,
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);

        var destPlacementId = ContextMenuModel.MoveWorkPlacementId(item.Id, result.PlacementId);
        if (string.IsNullOrWhiteSpace(destPlacementId))
            return;

        var cube = live.Cubes.FirstOrDefault(
            itemCube => string.Equals(itemCube.Id, destPlacementId, StringComparison.Ordinal));
        if (cube is null)
        {
            live.StatusError = "placement is not in the Cubes list";
            if (tty is not null)
                PaintChrome(tty, live);
            return;
        }

        if (live.MoveWork is null)
        {
            live.StatusError = AdoptedWorkOwnerResolver.AdoptRequiredDetail;
            if (tty is not null)
                PaintChrome(tty, live);
            return;
        }

        if (RefuseUnknownOrSamePlacement(live, destPlacementId, tty))
            return;

        var sourcePaneId = result.PaneId;
        var sourceWorkspaceId = result.WorkspaceId;
        IDestWorkExecutor? destExecutor = null;
        if (live.DestExecutorFactory is not null)
        {
            destExecutor = await live.DestExecutorFactory
                .CreateAsync(destPlacementId, ct)
                .ConfigureAwait(false);
        }

        var outcome = await live.MoveWork.RunAsync(
                new MoveWorkMenuRequest
                {
                    DestPlacementId = destPlacementId,
                    SourcePaneId = sourcePaneId,
                    SourceWorkspaceId = sourceWorkspaceId,
                    SourceMuxSocketPath = live.SourceMuxSocketPath ?? "",
                    Panes = PanesFromSnapshot(live.LastSnapshot),
                    ContinuityStoreDir = live.ContinuityStoreDir,
                    PlacementStoreDir = live.PlacementStoreDir,
                    DestExecutor = destExecutor,
                },
                ct)
            .ConfigureAwait(false);

        if (!outcome.Ok)
        {
            live.StatusError = outcome.Detail ?? outcome.Reason;
            if (tty is not null)
                PaintChrome(tty, live);
            return;
        }

        await ApplyCubesConnectAsync(
                new MouseEngineResult(
                    MouseCommandKind.ApplyMenu,
                    PlacementId: destPlacementId),
                live,
                control,
                tty,
                ct)
            .ConfigureAwait(false);
    }

    internal static IReadOnlyList<OccupantPaneSnapshot> PanesFromSnapshot(JsonElement? snapshot)
    {
        if (snapshot is not { } snap
            || snap.ValueKind != JsonValueKind.Object
            || !snap.TryGetProperty("panes", out var panes)
            || panes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<OccupantPaneSnapshot>();
        foreach (var pane in panes.EnumerateArray())
        {
            if (pane.ValueKind != JsonValueKind.Object)
                continue;
            var paneId = ReadString(pane, "pane_id");
            if (string.IsNullOrWhiteSpace(paneId))
                continue;
            list.Add(new OccupantPaneSnapshot
            {
                PaneId = paneId,
                TabId = ReadString(pane, "tab_id"),
                WorkspaceId = ReadString(pane, "workspace_id"),
                WorkId = ReadString(pane, "work_id"),
                Cwd = ReadString(pane, "cwd"),
                Home = ReadString(pane, "home"),
                AgentStatus = ReadString(pane, "state"),
            });
        }

        return list;
    }

    internal static SidebarCubeItem? ResolveSourcePlacement(
        AttachLiveState live,
        string destPlacementId)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (string.IsNullOrWhiteSpace(live.ConnectedPlacementId)
            || IsSamePlacement(live.ConnectedPlacementId, destPlacementId))
            return null;

        foreach (var cube in live.Cubes)
        {
            if (string.Equals(cube.Id, live.ConnectedPlacementId, StringComparison.Ordinal))
                return cube;
        }

        if (!string.IsNullOrWhiteSpace(live.PlacementDisplayName))
        {
            return new SidebarCubeItem
            {
                Id = live.ConnectedPlacementId,
                Name = live.PlacementDisplayName,
                Kind = live.PlacementKind ?? SidebarCubeKind.Local,
                Reachability = SidebarCubeReachability.Local,
            };
        }

        return null;
    }

    private static bool RefuseUnknownOrSamePlacement(
        AttachLiveState live,
        string destPlacementId,
        UnixRawTerminal? tty)
    {
        if (string.IsNullOrWhiteSpace(live.ConnectedPlacementId))
        {
            live.StatusError = UnknownSourcePlacementDetail;
            if (tty is not null)
                PaintChrome(tty, live);
            return true;
        }

        if (IsSamePlacement(live.ConnectedPlacementId, destPlacementId))
        {
            live.StatusError = SamePlacementDetail;
            if (tty is not null)
                PaintChrome(tty, live);
            return true;
        }

        return false;
    }

    private static bool IsSamePlacement(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && string.Equals(left, right, StringComparison.Ordinal);

    internal static string ResolveWorkTitle(
        AttachLiveState live,
        SidebarCubeItem source,
        MouseEngineResult result)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(result);
        var selected = AdoptedWorkOwnerResolver.WorkIdForOwner(
            PanesFromSnapshot(live.LastSnapshot),
            result.PaneId,
            result.WorkspaceId);
        if (!string.IsNullOrWhiteSpace(selected))
            return selected;
        if (!string.IsNullOrWhiteSpace(source.WorkTitle))
            return source.WorkTitle;
        return "Work";
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
