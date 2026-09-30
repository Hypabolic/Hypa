using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Attach.Worktrees;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach;

public sealed partial class AttachSession
{
    internal static bool ConsumesWorktreeDialogKeys(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        return live.Worktrees.IsOpen;
    }

    internal static async Task<bool> HandleWorktreeDialogKeysAsync(
        UnixRawTerminal? tty,
        AttachLiveState live,
        List<byte> decoded,
        IAttachCommandPort control,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(decoded);
        ArgumentNullException.ThrowIfNull(control);
        foreach (var decodedKey in KeyEventDecoder.Decode(CollectionsMarshal.AsSpan(decoded)))
            await RouteWorktreeDialogKeyAsync(decodedKey.Chord, live, control, tty, ct).ConfigureAwait(false);
        return live.DetachRequested;
    }

    internal static async Task RouteWorktreeDialogKeyAsync(
        KeyChord chord,
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chord);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        var dialog = live.Worktrees;
        if (!dialog.IsOpen)
            return;
        if (chord.Key is "esc" && dialog.Cancel())
        {
            await PublishWorktreeClientModeAsync(control, live, ct).ConfigureAwait(false);
            PaintWorktreeDialog(tty, live);
            return;
        }

        if (chord.IsEnter)
        {
            await SubmitWorktreeDialogAsync(live, control, tty, ct).ConfigureAwait(false);
            return;
        }

        if (chord.Key is "up")
        {
            dialog.MoveOpenSelection(-1);
            PaintWorktreeDialog(tty, live);
            return;
        }

        if (chord.Key is "down")
        {
            dialog.MoveOpenSelection(1);
            PaintWorktreeDialog(tty, live);
            return;
        }

        // on the first slash. :82-94 and :146-158 then insert "/".
        if (chord.Key is "slash" && dialog.FocusOpenSearch())
        {
            PaintWorktreeDialog(tty, live);
            return;
        }

        if (chord.Key is "backspace" && dialog.Backspace())
        {
            PaintWorktreeDialog(tty, live);
            return;
        }

        if (TryPrintable(chord, out var text) && dialog.InsertText(text))
            PaintWorktreeDialog(tty, live);
    }

    internal static async Task<bool> TryHandleWorktreeDialogMouseAsync(
        UnixRawTerminal? tty,
        AttachLiveState live,
        IReadOnlyList<MouseEvent> events,
        IAttachCommandPort control,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(control);
        if (!live.Worktrees.IsOpen)
            return false;
        var dialog = live.Worktrees;
        var layout = dialog.Layout
            ?? WorktreeDialogPainter.Measure(dialog, live.Chrome?.Cols ?? 80, live.Chrome?.Rows ?? 24);
        dialog.Layout = layout;
        if (!ConsumesWorktreeDialogChrome(layout, events))
            return false;
        foreach (var ev in events)
            await HandleWorktreeDialogMouseAsync(tty, live, ev, control, ct).ConfigureAwait(false);
        return true;
    }

    private static bool ConsumesWorktreeDialogChrome(
        WorktreeDialogLayout layout,
        IReadOnlyList<MouseEvent> events)
    {
        foreach (var ev in events)
        {
            if (ev.IsWheel)
                return true;
            if (ev.Action is not MouseAction.Press || ev.Button is not MouseButton.Left)
                continue;
            if (layout.Cancel.Contains(ev.Col, ev.Row)
                || layout.Primary.Contains(ev.Col, ev.Row)
                || (layout.Search is { } search && search.Contains(ev.Col, ev.Row)))
            {
                return true;
            }

            foreach (var (rect, _) in layout.Rows)
            {
                if (rect.Contains(ev.Col, ev.Row))
                    return true;
            }
        }

        return false;
    }

    internal static async Task BeginWorktreeActionAsync(
        KeyActionId action,
        string? workspaceId,
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(control);
        if (action is not (KeyActionId.NewWorktree or KeyActionId.OpenWorktree or KeyActionId.RemoveWorktree))
            return;
        if (AttachClientModePublication.ConflictsWithWorktreeOpen(live)
            || live.CubesPairing.IsOpen)
        {
            live.Worktrees.EndpointError = "ui_busy";
            live.StatusError = "ui_busy";
            PaintWorktreeDialog(tty, live);
            return;
        }

        var target = FirstNonEmpty(workspaceId, live.WorkspaceId);
        if (string.IsNullOrWhiteSpace(target))
            return;
        var linked = IsLinkedWorktree(live, target);
        if (action is KeyActionId.NewWorktree or KeyActionId.OpenWorktree && linked)
        {
            live.Worktrees.EndpointError = WorktreeDialogModel.LinkedSourceMessage;
            live.StatusError = live.Worktrees.EndpointError;
            PaintWorktreeDialog(tty, live);
            return;
        }

        if (action is KeyActionId.RemoveWorktree && !linked)
        {
            live.Worktrees.EndpointError = WorktreeDialogModel.NotLinkedMessage;
            live.StatusError = live.Worktrees.EndpointError;
            PaintWorktreeDialog(tty, live);
            return;
        }

        try
        {
            var listed = await control.CallAsync(
                    ProtocolMethods.WorktreeList,
                    ToJson(new WorktreeListParams { WorkspaceId = target }, ProtocolJsonContext.Default.WorktreeListParams),
                    ct)
                .ConfigureAwait(false);
            var result = listed.Deserialize(ProtocolJsonContext.Default.WorktreeListResult);
            if (result is null)
                return;
            switch (action)
            {
                case KeyActionId.NewWorktree:
                    var directory = ReadWorktreeDirectory(live);
                    if (string.IsNullOrWhiteSpace(directory))
                        return;
                    var seed = (ulong)Math.Min(
                        (ulong)live.Time.GetUtcNow().UtcTicks,
                        ulong.MaxValue);
                    live.Worktrees.ShowCreate(target, result.Source.RepoName, directory, seed);
                    break;
                case KeyActionId.OpenWorktree:
                    live.Worktrees.ShowOpen(target, result.Worktrees);
                    break;
                default:
                    live.Worktrees.ShowRemove(target, result.Worktrees);
                    break;
            }

            if (live.Worktrees.EndpointError is { } prepareError)
                live.StatusError = prepareError;
            if (live.Worktrees.IsOpen)
                await PublishWorktreeClientModeAsync(control, live, ct).ConfigureAwait(false);
        }
        catch (ControlPlaneException ex)
        {
            if (live.Worktrees.IsOpen)
                live.Worktrees.Cancel();
            live.Engine.ExclusiveSurfaceOpen = ExclusiveClientSurfaceOpen(live);
            live.Dispatcher.ClientMode = AttachClientModePublication.PublishedToken(live);
            ReconcilePrefixAsciiInput(live);
            live.StatusError = FormatStatus(ex);
        }

        PaintWorktreeDialog(tty, live);
    }

    internal static void ToggleWorktreeGroup(AttachLiveState live, string? groupKey)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (string.IsNullOrWhiteSpace(groupKey))
            return;
        live.CollapsedWorktreeGroups ??= new HashSet<string>(StringComparer.Ordinal);
        if (!live.CollapsedWorktreeGroups.Add(groupKey))
            live.CollapsedWorktreeGroups.Remove(groupKey);
        if (live.SidebarInput is { } input)
        {
            live.SidebarInput = input with { CollapsedWorktreeGroups = live.CollapsedWorktreeGroups };
            RecomposeLiveSidebar(live);
        }

        if (live.ClientViewPreferences is not null)
            PersistClientViewPreferences(live);
    }

    internal static WorkspaceWorktreeTarget? ResolveWorktreeTarget(AttachLiveState live, string workspaceId)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.SidebarInput?.Workspaces is { Count: > 0 } workspaces)
        {
            for (var i = 0; i < workspaces.Count; i++)
            {
                if (!string.Equals(workspaces[i].Id, workspaceId, StringComparison.Ordinal))
                    continue;
                return new WorkspaceWorktreeTarget(
                    workspaceId,
                    workspaces[i].WorktreeKey.Length > 0 || workspaces[i].Branch.Length > 0,
                    workspaces[i].IsLinkedWorktree,
                    WorktreeWorkspaceGrouping.ParentGroupKey(workspaces, i) is not null,
                    live.CollapsedWorktreeGroups is { } collapsed
                    && collapsed.Contains(workspaces[i].WorktreeKey),
                    workspaces[i].WorktreeKey);
            }
        }

        return null;
    }

    private static async Task HandleWorktreeDialogMouseAsync(
        UnixRawTerminal? tty,
        AttachLiveState live,
        MouseEvent ev,
        IAttachCommandPort control,
        CancellationToken ct)
    {
        var dialog = live.Worktrees;
        var layout = dialog.Layout ?? WorktreeDialogPainter.Measure(dialog, live.Chrome?.Cols ?? 80, live.Chrome?.Rows ?? 24);
        dialog.Layout = layout;
        if (ev.IsWheel)
        {
            dialog.MoveOpenSelection(ev.Button is MouseButton.WheelUp ? -1 : 1);
            PaintWorktreeDialog(tty, live);
            return;
        }

        if (ev.Action is not MouseAction.Press || ev.Button is not MouseButton.Left)
            return;
        if (layout.Cancel.Contains(ev.Col, ev.Row))
        {
            if (dialog.Cancel())
                await PublishWorktreeClientModeAsync(control, live, ct).ConfigureAwait(false);
            PaintWorktreeDialog(tty, live);
            return;
        }

        if (layout.Search is { } search && search.Contains(ev.Col, ev.Row))
        {
            dialog.FocusOpenSearch();
            PaintWorktreeDialog(tty, live);
            return;
        }

        foreach (var (rect, index) in layout.Rows)
        {
            if (!rect.Contains(ev.Col, ev.Row))
                continue;
            dialog.SelectOpenEntry(index);
            await SubmitWorktreeDialogAsync(live, control, tty, ct).ConfigureAwait(false);
            return;
        }

        if (layout.Primary.Contains(ev.Col, ev.Row))
            await SubmitWorktreeDialogAsync(live, control, tty, ct).ConfigureAwait(false);
    }

    private static async Task SubmitWorktreeDialogAsync(
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        var dialog = live.Worktrees;
        try
        {
            switch (dialog.Kind)
            {
                case WorktreeDialogKind.Create when dialog.TryBeginCreate(out var create):
                    PaintWorktreeDialog(tty, live);
                    var created = await control.CallAsync(
                            ProtocolMethods.WorktreeCreate,
                            ToJson(create!, ProtocolJsonContext.Default.WorktreeCreateParams),
                            ct)
                        .ConfigureAwait(false);
                    dialog.ApplySuccess();
                    ClearWorktreeStatusError(live);
                    await PublishWorktreeClientModeAsync(control, live, ct).ConfigureAwait(false);
                    await RefreshAfterWorktreeAsync(created, live, control, tty, ct).ConfigureAwait(false);
                    return;
                case WorktreeDialogKind.Open when dialog.TryBeginOpen(out var open):
                    PaintWorktreeDialog(tty, live);
                    var opened = await control.CallAsync(
                            ProtocolMethods.WorktreeOpen,
                            ToJson(open!, ProtocolJsonContext.Default.WorktreeOpenParams),
                            ct)
                        .ConfigureAwait(false);
                    dialog.ApplySuccess();
                    ClearWorktreeStatusError(live);
                    await PublishWorktreeClientModeAsync(control, live, ct).ConfigureAwait(false);
                    await RefreshAfterWorktreeAsync(opened, live, control, tty, ct).ConfigureAwait(false);
                    return;
                case WorktreeDialogKind.Remove when dialog.TryBeginRemove(out var remove):
                    PaintWorktreeDialog(tty, live);
                    await control.CallAsync(
                            ProtocolMethods.WorktreeRemove,
                            ToJson(remove!, ProtocolJsonContext.Default.WorktreeRemoveParams),
                            ct)
                        .ConfigureAwait(false);
                    dialog.ApplySuccess();
                    ClearWorktreeStatusError(live);
                    await PublishWorktreeClientModeAsync(control, live, ct).ConfigureAwait(false);
                    await RefreshChromeAsync(control, live, tty, ct).ConfigureAwait(false);
                    return;
                default:
                    PaintWorktreeDialog(tty, live);
                    return;
            }
        }
        catch (ControlPlaneException ex)
        {
            switch (dialog.Kind)
            {
                case WorktreeDialogKind.Create:
                    dialog.ApplyCreateError(ex.Message);
                    break;
                case WorktreeDialogKind.Open:
                    dialog.ApplyOpenError(ex.Message);
                    break;
                case WorktreeDialogKind.Remove:
                    dialog.ApplyRemoveError(ex.ErrorCode, ex.Message);
                    break;
            }

            live.StatusError = dialog.Kind is WorktreeDialogKind.Remove
                && dialog.Remove is { ForceConfirmation: true, Error: null }
                ? null
                : FormatStatus(ex);
            PaintWorktreeDialog(tty, live);
        }
    }

    private static async Task RefreshAfterWorktreeAsync(
        JsonElement result,
        AttachLiveState live,
        IAttachCommandPort control,
        UnixRawTerminal? tty,
        CancellationToken ct)
    {
        ClearWorktreeStatusError(live);
        var workspaceId = ReadCreatedWorkspaceId(result);
        await RefreshChromeAsync(control, live, tty, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(workspaceId))
            await live.Dispatcher.FocusWorkspaceAsync(workspaceId, ct).ConfigureAwait(false);
        PaintWorktreeDialog(tty, live);
    }

    private static string? ReadCreatedWorkspaceId(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
            return null;
        if (result.TryGetProperty("workspace", out var workspace)
            && workspace.ValueKind == JsonValueKind.Object
            && workspace.TryGetProperty("workspace_id", out var id)
            && id.ValueKind == JsonValueKind.String)
        {
            return id.GetString();
        }

        return null;
    }

    private static bool IsLinkedWorktree(AttachLiveState live, string workspaceId)
    {
        var target = ResolveWorktreeTarget(live, workspaceId);
        return target?.IsLinkedWorktree == true;
    }

    private static string ReadWorktreeDirectory(AttachLiveState live)
    {
        if (live.LastSnapshot is not { } snap || snap.ValueKind != JsonValueKind.Object)
            return "";
        return snap.TryGetProperty("worktree_directory", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static async Task PublishWorktreeClientModeAsync(
        IAttachCommandPort control,
        AttachLiveState live,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(live);
        live.Engine.ExclusiveSurfaceOpen = ExclusiveClientSurfaceOpen(live);
        live.Dispatcher.ClientMode = AttachClientModePublication.PublishedToken(live);
        ReconcilePrefixAsciiInput(live);
        await ReportAttachClientModeAsync(control, live, ct).ConfigureAwait(false);
    }

    private static void PaintWorktreeDialog(UnixRawTerminal? tty, AttachLiveState live)
    {
        if (tty is not null)
            PaintChrome(tty, live);
    }

    private static JsonObject ToJson<T>(T value, JsonTypeInfo<T> info)
    {
        using var doc = JsonSerializer.SerializeToDocument(value, info);
        return JsonNode.Parse(doc.RootElement.GetRawText()) as JsonObject
            ?? new JsonObject();
    }

    private static void ClearWorktreeStatusError(AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        live.StatusError = null;
        live.Worktrees.EndpointError = null;
    }

    private static bool TryPrintable(KeyChord chord, out string text)
    {
        text = "";
        if (chord.Ctrl || chord.Alt || chord.Prefix)
            return false;
        if (chord.Key is "esc" or "enter" or "tab" or "backspace" or "up" or "down" or "left" or "right"
            or "delete" or "home" or "end" or "pageup" or "pagedown" or "insert")
        {
            return false;
        }

        text = chord.Key switch
        {
            "space" => " ",
            "minus" => "-",
            "plus" => "+",
            "comma" => ",",
            "period" => ".",
            "slash" => "/",
            _ when chord.Key.Length == 1 => chord.Shift && char.IsAsciiLetter(chord.Key[0])
                ? char.ToUpperInvariant(chord.Key[0]).ToString()
                : chord.Key,
            _ => "",
        };
        return text.Length > 0;
    }
}

internal sealed record WorkspaceWorktreeTarget(
    string WorkspaceId,
    bool IsGit,
    bool IsLinkedWorktree,
    bool HasWorktreeChildren,
    bool Collapsed,
    string? GroupKey = null);
