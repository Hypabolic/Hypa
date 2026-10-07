using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Sidebar;

namespace Hypa.Cli.Attach.Chrome;

public sealed record ChromeHitApplyResult(
    KeyActionRequest? Request,
    int TabOverflowOffset,
    bool RebuildChrome,
    string? FocusTabId = null,
    string? PaneId = null,
    string? FocusPaneId = null,
    string? FocusWorkspaceId = null,
    bool Detach = false,
    bool OpenGlobalMenu = false,
    string? SidebarSectionId = null,
    bool OpenMobileSwitcher = false,
    bool CloseMobileSwitcher = false,
    bool OpenWhatsNew = false,
    bool ToggleAgentSort = false,
    string? ToggleWorktreeGroupKey = null,
    string? SidebarTreeId = null,
    SidebarCollectionActivation? CollectionActivation = null,
    bool InvokeCollectionAction = false,
    string? ConnectPlacementId = null,
    bool OpenAddCube = false,
    bool OpenShareMux = false,
    bool OpenUpdateNotice = false);

/// <summary>
/// Maps chrome hit-test records to layout actions. calls this from mouse.
/// Keyboard tests call it now so + / overflow / zoom are not paint-only.
/// </summary>
public static class ChromeHitApply
{
    public static ChromeHitApplyResult Apply(
        ChromeHit hit,
        int overflowOffset,
        int maxOverflowOffset)
    {
        ArgumentNullException.ThrowIfNull(hit);
        var maxOffset = Math.Max(0, maxOverflowOffset);
        var offset = Math.Clamp(overflowOffset, 0, maxOffset);

        switch (hit.Kind)
        {
            case ChromeHitKind.TabNew:
                return new ChromeHitApplyResult(
                    new KeyActionRequest(KeyActionId.NewTab),
                    offset,
                    RebuildChrome: true);
            case ChromeHitKind.TabOverflowPrev:
                return new ChromeHitApplyResult(null, Math.Max(0, offset - 1), RebuildChrome: true);
            case ChromeHitKind.TabOverflowNext:
                return new ChromeHitApplyResult(
                    null,
                    Math.Min(maxOffset, offset + 1),
                    RebuildChrome: true);
            case ChromeHitKind.Tab:
                return new ChromeHitApplyResult(
                    null,
                    offset,
                    RebuildChrome: true,
                    FocusTabId: hit.TabId);
            case ChromeHitKind.TabClose:
                return new ChromeHitApplyResult(
                    new KeyActionRequest(KeyActionId.CloseTab),
                    offset,
                    RebuildChrome: true,
                    FocusTabId: hit.TabId);
            case ChromeHitKind.SidebarWorkspaceClose:
                return new ChromeHitApplyResult(
                    new KeyActionRequest(KeyActionId.CloseWorkspace, TargetId: hit.WorkspaceId),
                    offset,
                    RebuildChrome: true);
            case ChromeHitKind.PaneClose:
                return new ChromeHitApplyResult(
                    new KeyActionRequest(KeyActionId.ClosePane),
                    offset,
                    RebuildChrome: true,
                    PaneId: hit.PaneId);
            case ChromeHitKind.Zoom:
                return new ChromeHitApplyResult(
                    new KeyActionRequest(KeyActionId.Zoom),
                    offset,
                    RebuildChrome: true);
            case ChromeHitKind.Pane:
                return new ChromeHitApplyResult(
                    null,
                    offset,
                    RebuildChrome: true,
                    FocusPaneId: hit.PaneId);
            case ChromeHitKind.SidebarWorktreeGroupToggle:
                return new ChromeHitApplyResult(
                    null,
                    offset,
                    RebuildChrome: true,
                    ToggleWorktreeGroupKey: hit.GroupKey);
            case ChromeHitKind.SidebarWorkspace:
                return new ChromeHitApplyResult(
                    null,
                    offset,
                    RebuildChrome: true,
                    FocusWorkspaceId: hit.WorkspaceId);
            case ChromeHitKind.SidebarAgent:
                return new ChromeHitApplyResult(
                    null,
                    offset,
                    RebuildChrome: true,
                    FocusPaneId: hit.PaneId);
            case ChromeHitKind.SidebarHiddenPane:
                return new ChromeHitApplyResult(
                    null,
                    offset,
                    RebuildChrome: true,
                    FocusPaneId: hit.PaneId);
            case ChromeHitKind.SidebarTreeToggle:
                return new ChromeHitApplyResult(
                    null,
                    offset,
                    RebuildChrome: true,
                    SidebarTreeId: hit.PaneId);
            case ChromeHitKind.SidebarCube when !string.IsNullOrWhiteSpace(hit.PlacementId):
                return new ChromeHitApplyResult(
                    null,
                    offset,
                    RebuildChrome: true,
                    CloseMobileSwitcher: true,
                    ConnectPlacementId: hit.PlacementId);
            case ChromeHitKind.SidebarCube:
                return new ChromeHitApplyResult(
                    null,
                    offset,
                    RebuildChrome: false);
            case ChromeHitKind.SidebarCollectionItem:
                return ApplyCollectionItem(hit, offset);
            case ChromeHitKind.Toast:
                return new ChromeHitApplyResult(
                    null,
                    offset,
                    RebuildChrome: true,
                    FocusPaneId: hit.PaneId);
            case ChromeHitKind.SidebarMenu:
                return new ChromeHitApplyResult(
                    null,
                    offset,
                    RebuildChrome: true,
                    OpenGlobalMenu: true);
            case ChromeHitKind.SidebarNew:
                return new ChromeHitApplyResult(
                    new KeyActionRequest(KeyActionId.NewWorkspace),
                    offset,
                    RebuildChrome: true);
            case ChromeHitKind.SidebarAddCube:
                return new ChromeHitApplyResult(
                    null,
                    offset,
                    RebuildChrome: true,
                    OpenAddCube: true);
            case ChromeHitKind.SidebarShareMux:
                return new ChromeHitApplyResult(
                    null,
                    offset,
                    RebuildChrome: true,
                    OpenShareMux: true);
            case ChromeHitKind.SidebarUpdateNotice:
                return new ChromeHitApplyResult(
                    null,
                    offset,
                    RebuildChrome: true,
                    OpenUpdateNotice: true);
            case ChromeHitKind.SidebarSort:
                return new ChromeHitApplyResult(
                    null,
                    offset,
                    RebuildChrome: true,
                    ToggleAgentSort: true);
            case ChromeHitKind.SidebarSection:
                return new ChromeHitApplyResult(
                    null,
                    offset,
                    RebuildChrome: true,
                    SidebarSectionId: hit.WorkspaceId);
            case ChromeHitKind.SidebarEdge:
                return new ChromeHitApplyResult(
                    new KeyActionRequest(KeyActionId.ToggleSidebar),
                    offset,
                    RebuildChrome: true);
            case ChromeHitKind.MobileSwitch:
                return new ChromeHitApplyResult(
                    null,
                    offset,
                    RebuildChrome: true,
                    OpenMobileSwitcher: true);
            case ChromeHitKind.MobileSwitcherClose:
                return new ChromeHitApplyResult(
                    null,
                    offset,
                    RebuildChrome: true,
                    CloseMobileSwitcher: true);
            case ChromeHitKind.MobileSwitcherMenu:
                return ApplySwitcherMenu(hit.MenuIndex, offset);
            default:
                return new ChromeHitApplyResult(null, offset, RebuildChrome: false);
        }
    }

    private static ChromeHitApplyResult ApplySwitcherMenu(int? menuIndex, int offset)
    {
        var items = GlobalMenuModel.Items();
        var index = menuIndex ?? 0;
        if (index < 0 || index >= items.Count)
            return new ChromeHitApplyResult(null, offset, RebuildChrome: false, CloseMobileSwitcher: true);

        var item = items[index];
        if (item.Id == GlobalMenuModel.WhatsNew)
        {
            return new ChromeHitApplyResult(
                null,
                offset,
                RebuildChrome: true,
                CloseMobileSwitcher: true,
                OpenWhatsNew: true);
        }

        if (item.Action is KeyActionId.Detach)
        {
            return new ChromeHitApplyResult(
                new KeyActionRequest(KeyActionId.Detach),
                offset,
                RebuildChrome: false,
                Detach: true,
                CloseMobileSwitcher: true);
        }

        if (item.Action is { } action)
        {
            return new ChromeHitApplyResult(
                new KeyActionRequest(action),
                offset,
                RebuildChrome: true,
                CloseMobileSwitcher: true);
        }

        return new ChromeHitApplyResult(null, offset, RebuildChrome: true, CloseMobileSwitcher: true);
    }

    public static JsonObject? FocusTabParams(string? tabId) =>
        string.IsNullOrWhiteSpace(tabId) ? null : new JsonObject { ["tab_id"] = tabId };

    public static string TabFocusMethod => ProtocolMethods.TabFocus;

    private static ChromeHitApplyResult ApplyCollectionItem(ChromeHit hit, int offset)
    {
        var activation = hit.CollectionActivation;
        if (activation is null)
            return new ChromeHitApplyResult(null, offset, RebuildChrome: false);

        var target = activation.Target;
        var hasTarget = target is not null
            && (!string.IsNullOrWhiteSpace(target.PaneId)
                || !string.IsNullOrWhiteSpace(target.WorkspaceId)
                || !string.IsNullOrWhiteSpace(target.TabId));
        var hasAction = !string.IsNullOrWhiteSpace(activation.ActionId);
        if (hit.SecondaryActivation && hasAction)
        {
            return new ChromeHitApplyResult(
                null,
                offset,
                RebuildChrome: false,
                CollectionActivation: activation,
                InvokeCollectionAction: true);
        }

        if (hasTarget)
        {
            return new ChromeHitApplyResult(
                null,
                offset,
                RebuildChrome: true,
                FocusPaneId: target!.PaneId,
                FocusWorkspaceId: target.WorkspaceId,
                FocusTabId: target.TabId,
                CollectionActivation: activation);
        }

        if (hasAction)
        {
            return new ChromeHitApplyResult(
                null,
                offset,
                RebuildChrome: false,
                CollectionActivation: activation,
                InvokeCollectionAction: true);
        }

        return new ChromeHitApplyResult(null, offset, RebuildChrome: false);
    }
}
