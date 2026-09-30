using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach.Sidebar;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach.Keys;

/// <summary>Live sink. Calls existing RPCs. Tracks last pane for <c>last_pane</c>.</summary>
public sealed class AttachCommandDispatcher : IKeyActionSink
{
    private IAttachCommandPort _port;

    public AttachCommandDispatcher(
        IAttachCommandPort port,
        string? workspaceId = null,
        string? tabId = null,
        string? paneId = null,
        string? resizeLease = null,
        AgentPanelSort agentPanelSort = AgentPanelSort.Spaces)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        WorkspaceId = workspaceId;
        TabId = tabId;
        PaneId = paneId;
        ResizeLease = resizeLease;
        AgentPanelSort = agentPanelSort;
    }

    internal void RebindPort(IAttachCommandPort port)
    {
        ArgumentNullException.ThrowIfNull(port);
        _port = port;
    }

    internal void RebindTarget(
        string? workspaceId,
        string? tabId,
        string? paneId,
        string? resizeLease)
    {
        WorkspaceId = workspaceId;
        TabId = tabId;
        ResizeLease = resizeLease;
        if (!string.IsNullOrWhiteSpace(paneId))
            RememberPane(paneId);
    }

    public string? WorkspaceId { get; private set; }

    public string? TabId { get; private set; }

    public string? PaneId { get; private set; }

    public string? LastPaneId { get; private set; }

    public string? LastNotificationPaneId { get; set; }

    public string? CommandOverlayPaneId { get; private set; }

    public string? CommandOverlayRestorePaneId { get; private set; }

    public string? CommandOverlayTabId { get; private set; }

    public string? CommandOverlayLayoutRestorePaneId { get; private set; }

    public bool CommandOverlayGone { get; private set; }

    public bool HasPendingCommandOverlayRestore =>
        CommandOverlayGone && !string.IsNullOrWhiteSpace(CommandOverlayPaneId);

    public string? ResizeLease { get; set; }

    /// <summary>Live <c>ui.agent_panel_sort</c>. Must match compact sidebar digits.</summary>
    public AgentPanelSort AgentPanelSort { get; set; }

    public const string CustomCommandFailed = "custom command failed";
    public const string PluginActionUnavailable = "plugin_action_unavailable";

    public IReadOnlyList<KeyCommandBinding> Commands { get; set; } = [];

    public IDetachedCommandLauncher? DetachedLauncher { get; set; }

    public string? SocketPath { get; set; }

    public string? BinPath { get; set; }

    public string? ActivePaneCwd { get; set; }

    public int AreaCols { get; set; } = 80;

    public int AreaRows { get; set; } = 24;

    public string ClientMode { get; set; } = "terminal";

    public bool PopupIsOpen { get; set; }

    public void FocusPane(string paneId) => RememberPane(paneId);

    public async Task FocusWorkspaceAsync(string workspaceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return;
        await _port.CallAsync(
                ProtocolMethods.WorkspaceFocus,
                new JsonObject { ["workspace_id"] = workspaceId },
                ct)
            .ConfigureAwait(false);
        WorkspaceId = workspaceId;
        await RefreshFocusAsync(ct).ConfigureAwait(false);
    }

    public Task SetSplitRatioAsync(IReadOnlyList<int> path, double ratio, CancellationToken ct)
    {
        if (TabId is null || ResizeLease is null)
            return Task.CompletedTask;
        ArgumentNullException.ThrowIfNull(path);
        var pathNodes = new JsonNode?[path.Count];
        for (var i = 0; i < path.Count; i++)
            pathNodes[i] = JsonValue.Create(path[i]);
        var next = ratio;
        if (next <= 0)
            next = 0.01;
        if (next >= 1)
            next = 0.99;
        return _port.CallAsync(
            ProtocolMethods.LayoutSetSplitRatio,
            new JsonObject
            {
                ["tab_id"] = TabId,
                ["path"] = new JsonArray(pathNodes),
                ["ratio"] = next,
                ["lease_id"] = ResizeLease,
            },
            ct);
    }

    public Task MoveTabToAsync(string tabId, int index, CancellationToken ct) =>
        _port.CallAsync(
            ProtocolMethods.TabMove,
            new JsonObject
            {
                ["tab_id"] = tabId,
                ["index"] = index,
            },
            ct);

    public Task SetRightClickAsync(string paneId, string policy, CancellationToken ct) =>
        _port.CallAsync(
            ProtocolMethods.PaneInputSet,
            new JsonObject
            {
                ["pane_id"] = paneId,
                ["right_click"] = policy,
            },
            ct);

    public Task ClearPaneNameAsync(string paneId, CancellationToken ct) =>
        _port.CallAsync(
            ProtocolMethods.PaneRename,
            new JsonObject
            {
                ["pane_id"] = paneId,
                ["label"] = "",
            },
            ct);

    public Task SwapWithAsync(string paneId, string targetPaneId, CancellationToken ct) =>
        _port.CallAsync(
            ProtocolMethods.PaneSwap,
            new JsonObject
            {
                ["pane_id"] = paneId,
                ["target_pane_id"] = targetPaneId,
            },
            ct);

    public Task SplitPaneAsync(string paneId, string direction, CancellationToken ct) =>
        _port.CallAsync(
            ProtocolMethods.PaneSplit,
            new JsonObject
            {
                ["pane_id"] = paneId,
                ["direction"] = direction,
            },
            ct);

    public void Handle(KeyActionRequest request) =>
        HandleAsync(request, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public async ValueTask HandleAsync(KeyActionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        switch (request.Action)
        {
            case KeyActionId.NewWorkspace:
                await NewWorkspaceAsync(ct, request.PromptText).ConfigureAwait(false);
                break;
            case KeyActionId.RenameWorkspace:
                if (!string.IsNullOrEmpty(request.PromptText) && WorkspaceId is { } wsRename)
                {
                    await _port.CallAsync(
                            ProtocolMethods.WorkspaceRename,
                            new JsonObject
                            {
                                ["workspace_id"] = wsRename,
                                ["label"] = request.PromptText,
                            },
                            ct)
                        .ConfigureAwait(false);
                }

                break;
            case KeyActionId.CloseWorkspace:
                {
                    // WorkspaceClose with the menu workspace_id, not the
                    // focused workspace when those differ.
                    var wsClose = !string.IsNullOrWhiteSpace(request.TargetId)
                        ? request.TargetId
                        : WorkspaceId;
                    if (wsClose is { Length: > 0 })
                    {
                        var closingFocused = string.Equals(
                            wsClose,
                            WorkspaceId,
                            StringComparison.Ordinal);
                        var closedPane = closingFocused ? PaneId : null;
                        await _port.CallAsync(
                                ProtocolMethods.WorkspaceClose,
                                new JsonObject { ["workspace_id"] = wsClose },
                                ct)
                            .ConfigureAwait(false);
                        await RefreshFocusAsync(ct, recordLast: false).ConfigureAwait(false);
                        ForgetClosedPane(closedPane);
                    }
                }

                break;
            case KeyActionId.NavigateWorkspaceUp:
                await CycleWorkspaceAsync(-1, ct).ConfigureAwait(false);
                break;
            case KeyActionId.NavigateWorkspaceDown:
                await CycleWorkspaceAsync(1, ct).ConfigureAwait(false);
                break;
            case KeyActionId.PreviousWorkspace:
                await CycleWorkspaceAsync(-1, ct).ConfigureAwait(false);
                break;
            case KeyActionId.NextWorkspace:
                await CycleWorkspaceAsync(1, ct).ConfigureAwait(false);
                break;
            case KeyActionId.NewTab:
                if (WorkspaceId is { } wsTab)
                {
                    var tabParams = new JsonObject
                    {
                        ["workspace_id"] = wsTab,
                        ["create_pane"] = true,
                    };
                    if (!string.IsNullOrEmpty(request.PromptText))
                        tabParams["label"] = request.PromptText;
                    var created = await _port.CallAsync(
                            ProtocolMethods.TabCreate,
                            tabParams,
                            ct)
                        .ConfigureAwait(false);
                    ApplyTab(created);
                }

                break;
            case KeyActionId.RenameTab:
                if (!string.IsNullOrEmpty(request.PromptText) && TabId is { } tabRename)
                {
                    await _port.CallAsync(
                            ProtocolMethods.TabRename,
                            new JsonObject
                            {
                                ["tab_id"] = tabRename,
                                ["label"] = request.PromptText,
                            },
                            ct)
                        .ConfigureAwait(false);
                }

                break;
            case KeyActionId.PreviousTab:
                await CycleTabAsync(-1, ct).ConfigureAwait(false);
                break;
            case KeyActionId.NextTab:
                await CycleTabAsync(1, ct).ConfigureAwait(false);
                break;
            case KeyActionId.SwitchTab:
                await FocusTabIndexAsync(request.Index ?? 1, ct).ConfigureAwait(false);
                break;
            case KeyActionId.IndexedTabs:
                if (request.Index is { } tabIndex)
                    await FocusTabIndexAsync(tabIndex, ct).ConfigureAwait(false);
                break;
            case KeyActionId.SwitchWorkspace:
            case KeyActionId.IndexedWorkspaces:
                if (request.Index is { } workspaceIndex)
                    await FocusWorkspaceIndexAsync(workspaceIndex, ct).ConfigureAwait(false);
                break;
            case KeyActionId.FocusAgent:
            case KeyActionId.IndexedAgents:
                if (request.Index is { } agentIndex)
                    await FocusAgentIndexAsync(agentIndex, ct).ConfigureAwait(false);
                break;
            case KeyActionId.MoveTabPrevious:
                await MoveTabAsync(-1, ct).ConfigureAwait(false);
                break;
            case KeyActionId.MoveTabNext:
                await MoveTabAsync(1, ct).ConfigureAwait(false);
                break;
            case KeyActionId.CloseTab:
                if (TabId is { } tabClose)
                {
                    var closedPane = PaneId;
                    await _port.CallAsync(
                            ProtocolMethods.TabClose,
                            new JsonObject { ["tab_id"] = tabClose },
                            ct)
                        .ConfigureAwait(false);
                    await RefreshFocusAsync(ct, recordLast: false).ConfigureAwait(false);
                    ForgetClosedPane(closedPane);
                }

                break;
            case KeyActionId.RenamePane:
                if (!string.IsNullOrEmpty(request.PromptText) && PaneId is { } paneRename)
                {
                    await _port.CallAsync(
                            ProtocolMethods.PaneRename,
                            new JsonObject
                            {
                                ["pane_id"] = paneRename,
                                ["label"] = request.PromptText,
                            },
                            ct)
                        .ConfigureAwait(false);
                }

                break;
            case KeyActionId.FocusPaneLeft:
                await FocusDirectionAsync("left", ct).ConfigureAwait(false);
                break;
            case KeyActionId.FocusPaneDown:
                await FocusDirectionAsync("down", ct).ConfigureAwait(false);
                break;
            case KeyActionId.FocusPaneUp:
                await FocusDirectionAsync("up", ct).ConfigureAwait(false);
                break;
            case KeyActionId.FocusPaneRight:
                await FocusDirectionAsync("right", ct).ConfigureAwait(false);
                break;
            case KeyActionId.NavigatePaneLeft:
                await FocusDirectionAsync("left", ct).ConfigureAwait(false);
                break;
            case KeyActionId.NavigatePaneDown:
                await FocusDirectionAsync("down", ct).ConfigureAwait(false);
                break;
            case KeyActionId.NavigatePaneUp:
                await FocusDirectionAsync("up", ct).ConfigureAwait(false);
                break;
            case KeyActionId.NavigatePaneRight:
                await FocusDirectionAsync("right", ct).ConfigureAwait(false);
                break;
            case KeyActionId.CyclePaneNext:
                await CyclePaneAsync(1, ct).ConfigureAwait(false);
                break;
            case KeyActionId.CyclePanePrevious:
                await CyclePaneAsync(-1, ct).ConfigureAwait(false);
                break;
            case KeyActionId.LastPane:
                if (LastPaneId is { Length: > 0 } last && last != PaneId)
                    await FocusPaneAsync(last, ct).ConfigureAwait(false);
                break;
            case KeyActionId.SplitVertical:
                if (PaneId is { } splitV)
                {
                    await _port.CallAsync(
                            ProtocolMethods.PaneSplit,
                            new JsonObject
                            {
                                ["pane_id"] = splitV,
                                ["direction"] = "right",
                            },
                            ct)
                        .ConfigureAwait(false);
                    await RefreshFocusAsync(ct).ConfigureAwait(false);
                }

                break;
            case KeyActionId.SplitHorizontal:
                if (PaneId is { } splitH)
                {
                    await _port.CallAsync(
                            ProtocolMethods.PaneSplit,
                            new JsonObject
                            {
                                ["pane_id"] = splitH,
                                ["direction"] = "down",
                            },
                            ct)
                        .ConfigureAwait(false);
                    await RefreshFocusAsync(ct).ConfigureAwait(false);
                }

                break;
            case KeyActionId.ClosePane:
                if (PaneId is { } closePane)
                {
                    await _port.CallAsync(
                            ProtocolMethods.PaneClose,
                            new JsonObject { ["pane_id"] = closePane },
                            ct)
                        .ConfigureAwait(false);
                    await RefreshFocusAsync(ct, recordLast: false).ConfigureAwait(false);
                    ForgetClosedPane(closePane);
                }

                break;
            case KeyActionId.Zoom:
                if (PaneId is { } zoomPane)
                {
                    await _port.CallAsync(
                            ProtocolMethods.PaneZoom,
                            new JsonObject
                            {
                                ["pane_id"] = zoomPane,
                                ["mode"] = "toggle",
                            },
                            ct)
                        .ConfigureAwait(false);
                }

                break;
            case KeyActionId.ResizePaneLeft:
                await ResizeAsync("left", ct).ConfigureAwait(false);
                break;
            case KeyActionId.ResizePaneDown:
                await ResizeAsync("down", ct).ConfigureAwait(false);
                break;
            case KeyActionId.ResizePaneUp:
                await ResizeAsync("up", ct).ConfigureAwait(false);
                break;
            case KeyActionId.ResizePaneRight:
                await ResizeAsync("right", ct).ConfigureAwait(false);
                break;
            case KeyActionId.SwapPaneLeft:
                await SwapAsync("left", ct).ConfigureAwait(false);
                break;
            case KeyActionId.SwapPaneDown:
                await SwapAsync("down", ct).ConfigureAwait(false);
                break;
            case KeyActionId.SwapPaneUp:
                await SwapAsync("up", ct).ConfigureAwait(false);
                break;
            case KeyActionId.SwapPaneRight:
                await SwapAsync("right", ct).ConfigureAwait(false);
                break;
            case KeyActionId.PreviousAgent:
                await CycleAgentAsync(-1, ct).ConfigureAwait(false);
                break;
            case KeyActionId.NextAgent:
                await CycleAgentAsync(1, ct).ConfigureAwait(false);
                break;
            case KeyActionId.OpenNotificationTarget:
                await OpenNotificationTargetAsync(ct).ConfigureAwait(false);
                break;
            case KeyActionId.Goto:
            case KeyActionId.Navigate:
            case KeyActionId.ToggleSidebar:
            case KeyActionId.Settings:
            case KeyActionId.WorkspacePicker:
            case KeyActionId.CopyMode:
            case KeyActionId.ResizeMode:
            case KeyActionId.Help:
            case KeyActionId.Detach:
            case KeyActionId.NewWorktree:
            case KeyActionId.OpenWorktree:
            case KeyActionId.RemoveWorktree:
            case KeyActionId.ReloadConfig:
                break;
            case KeyActionId.Command:
                await RunCustomCommandAsync(request, ct).ConfigureAwait(false);
                break;
            case KeyActionId.EditScrollback:
            case KeyActionId.AnnotateHandoff:
            case KeyActionId.RemoteImagePaste:
            case KeyActionId.Prefix:
                break;
        }
    }

    private async Task RunCustomCommandAsync(KeyActionRequest request, CancellationToken ct)
    {
        if (request.Index is not int index || index < 0 || index >= Commands.Count)
            throw FailedCustomCommand();

        var command = Commands[index];
        var type = string.IsNullOrWhiteSpace(command.Type) ? "shell" : command.Type;
        if (string.IsNullOrEmpty(command.Command))
            throw FailedCustomCommand();

        switch (type)
        {
            case "shell":
                RunDetachedShell(command);
                return;
            case "pane":
                await RunOverlayPaneAsync(command, ct).ConfigureAwait(false);
                return;
            case "popup":
                await RunPopupAsync(command, ct).ConfigureAwait(false);
                return;
            case "plugin_action":
                await InvokePluginActionAsync(command, ct).ConfigureAwait(false);
                return;
            default:
                throw FailedCustomCommand();
        }
    }

    public Task InvokePluginActionAsync(string actionId, CancellationToken ct)
    {
        var trimmed = actionId.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidState,
                PluginActionUnavailable);
        }

        return _port.CallAsync(
            ProtocolMethods.PluginActionInvoke,
            new JsonObject { ["action_id"] = trimmed },
            ct);
    }

    private Task InvokePluginActionAsync(KeyCommandBinding command, CancellationToken ct) =>
        InvokePluginActionAsync(command.Command, ct);

    private void RunDetachedShell(KeyCommandBinding command)
    {
        if (DetachedLauncher is null)
            throw FailedCustomCommand();

        var env = BuildCommandEnv();
        var cwd = Directory.Exists(ActivePaneCwd) ? ActivePaneCwd : null;
        if (!DetachedLauncher.TryStart("/bin/sh", ["-lc", command.Command], env, cwd, out _))
            throw FailedCustomCommand();
    }

    private async Task RunOverlayPaneAsync(KeyCommandBinding command, CancellationToken ct)
    {
        if (PaneId is null)
            throw FailedCustomCommand();

        var restorePaneId = PaneId;
        var env = BuildCommandEnv();
        var splitParams = new JsonObject
        {
            ["pane_id"] = PaneId,
            ["direction"] = "down",
            ["command"] = "/bin/sh",
            ["args"] = new JsonArray("-c", command.Command),
            ["close_on_exit"] = true,
        };
        if (env.Count > 0)
            splitParams["env"] = EnvObject(env);
        if (!string.IsNullOrWhiteSpace(ActivePaneCwd) && Directory.Exists(ActivePaneCwd))
            splitParams["cwd"] = ActivePaneCwd;

        var created = await _port.CallAsync(ProtocolMethods.PaneSplit, splitParams, ct)
            .ConfigureAwait(false);
        var overlayId = TryString(created, "pane_id");
        if (string.IsNullOrWhiteSpace(overlayId))
            throw FailedCustomCommand();

        CommandOverlayPaneId = overlayId;
        CommandOverlayRestorePaneId = restorePaneId;
        CommandOverlayTabId = TabId;
        CommandOverlayLayoutRestorePaneId = null;
        CommandOverlayGone = false;
        try
        {
            await _port.CallAsync(
                    ProtocolMethods.PaneZoom,
                    new JsonObject
                    {
                        ["pane_id"] = overlayId,
                        ["mode"] = "on",
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch (ControlPlaneException ex) when (ex.Code == ProtocolErrorCodes.NotFound)
        {
            // Server already closed a fast overlay (echo/true) and restored focus.
            ClearCommandOverlay();
            return;
        }

        RememberPane(overlayId);
    }

    public void NoteCommandOverlayGone(string? layoutRestorePaneId = null)
    {
        CommandOverlayGone = true;
        if (string.IsNullOrWhiteSpace(layoutRestorePaneId)
            || string.Equals(layoutRestorePaneId, CommandOverlayPaneId, StringComparison.Ordinal))
        {
            return;
        }

        CommandOverlayLayoutRestorePaneId = layoutRestorePaneId;
    }

    public void ClearCommandOverlay()
    {
        CommandOverlayPaneId = null;
        CommandOverlayRestorePaneId = null;
        CommandOverlayTabId = null;
        CommandOverlayLayoutRestorePaneId = null;
        CommandOverlayGone = false;
    }

    private async Task RunPopupAsync(KeyCommandBinding command, CancellationToken ct)
    {
        if (PopupIsOpen || IsBusyClientMode(ClientMode))
        {
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidState, "ui_busy");
        }

        var env = PaneIdEnvironment.Strip(BuildCommandEnv());
        var open = new JsonObject
        {
            ["command"] = "/bin/sh",
            ["args"] = new JsonArray("-c", command.Command),
            ["client_mode"] = string.IsNullOrWhiteSpace(ClientMode) ? "terminal" : ClientMode,
            ["area_cols"] = Math.Max(1, AreaCols),
            ["area_rows"] = Math.Max(1, AreaRows),
        };
        if (env.Count > 0)
            open["env"] = EnvObject(env);
        if (!string.IsNullOrWhiteSpace(ActivePaneCwd) && Directory.Exists(ActivePaneCwd))
            open["cwd"] = ActivePaneCwd;
        WritePopupSize(open, "width", command.Width);
        WritePopupSize(open, "height", command.Height);

        await _port.CallAsync(ProtocolMethods.PopupOpen, open, ct).ConfigureAwait(false);
    }

    private IReadOnlyDictionary<string, string> BuildCommandEnv() =>
        CustomCommandEnvironment.Build(new CustomCommandEnvironmentRequest
        {
            SocketPath = SocketPath,
            BinPath = BinPath,
            WorkspaceId = WorkspaceId,
            TabId = TabId,
            PaneId = PaneId,
            PaneCwd = ActivePaneCwd,
        });

    private static JsonObject EnvObject(IReadOnlyDictionary<string, string> env)
    {
        var obj = new JsonObject();
        foreach (var kv in env)
            obj[kv.Key] = kv.Value;
        return obj;
    }

    private static void WritePopupSize(JsonObject target, string name, AttachPopupSize? size)
    {
        if (size is null)
            return;
        if (size.IsPercent)
        {
            target[name] = string.Create(CultureInfo.InvariantCulture, $"{size.Value}%");
            return;
        }

        target[name] = size.Value;
    }

    private static bool IsBusyClientMode(string? clientMode)
    {
        if (string.IsNullOrWhiteSpace(clientMode))
            return false;
        return !string.Equals(clientMode.Trim(), "terminal", StringComparison.OrdinalIgnoreCase);
    }

    private static ControlPlaneException FailedCustomCommand() =>
        new(ProtocolErrorCodes.InvalidState, CustomCommandFailed);

    internal async Task OpenNotificationTargetAsync(CancellationToken ct)
    {
        var target = LastNotificationPaneId;
        if (string.IsNullOrWhiteSpace(target))
            return;
        await _port.CallAsync(
                ProtocolMethods.PaneFocus,
                new JsonObject { ["pane_id"] = target },
                ct)
            .ConfigureAwait(false);
        RememberPane(target);
    }

    public void Seed(
        string? workspaceId,
        string? tabId,
        string? paneId,
        string? resizeLease = null,
        string? lastPaneId = null)
    {
        WorkspaceId = workspaceId;
        TabId = tabId;
        PaneId = paneId;
        if (resizeLease is not null)
            ResizeLease = resizeLease;
        if (lastPaneId is not null)
            LastPaneId = string.IsNullOrWhiteSpace(lastPaneId) ? null : lastPaneId;
    }

    public void RevertFocus(string? workspaceId, string? tabId, string? paneId, string? lastPaneId)
    {
        WorkspaceId = workspaceId;
        TabId = tabId;
        PaneId = paneId;
        LastPaneId = lastPaneId;
    }

    private async Task NewWorkspaceAsync(CancellationToken ct, string? label = null)
    {
        var created = await _port.CallAsync(
                ProtocolMethods.WorkspaceCreate,
                NewWorkspaceParams(label),
                ct)
            .ConfigureAwait(false);
        if (created.ValueKind == JsonValueKind.Object)
        {
            if (TryString(created, "workspace_id") is { } ws)
            {
                WorkspaceId = ws;
                await _port.CallAsync(
                        ProtocolMethods.WorkspaceFocus,
                        new JsonObject { ["workspace_id"] = ws },
                        ct)
                    .ConfigureAwait(false);
            }

            if (created.TryGetProperty("pane", out var pane)
                && pane.ValueKind == JsonValueKind.Object
                && TryString(pane, "pane_id") is { } paneId)
            {
                RememberPane(paneId);
            }

            if (TryString(created, "focused_tab_id") is { } tab)
                TabId = tab;
        }

        await RefreshFocusAsync(ct).ConfigureAwait(false);
    }

    private async Task CycleWorkspaceAsync(int delta, CancellationToken ct)
    {
        var listed = await _port.CallAsync(ProtocolMethods.WorkspaceList, null, ct)
            .ConfigureAwait(false);
        var ids = ReadIds(listed, "workspace_id");
        if (ids.Count == 0)
            return;
        var idx = IndexOf(ids, WorkspaceId);
        var next = ids[Wrap(idx + delta, ids.Count)];
        await _port.CallAsync(
                ProtocolMethods.WorkspaceFocus,
                new JsonObject { ["workspace_id"] = next },
                ct)
            .ConfigureAwait(false);
        WorkspaceId = next;
        await RefreshFocusAsync(ct).ConfigureAwait(false);
    }

    private async Task CycleTabAsync(int delta, CancellationToken ct)
    {
        var tabs = await ListTabsAsync(ct).ConfigureAwait(false);
        if (tabs.Count == 0)
            return;
        var idx = IndexOf(tabs, TabId);
        var next = tabs[Wrap(idx + delta, tabs.Count)];
        await FocusTabAsync(next, ct).ConfigureAwait(false);
    }

    private async Task FocusTabIndexAsync(int index, CancellationToken ct)
    {
        var tabs = await ListTabsAsync(ct).ConfigureAwait(false);
        if (index < 1 || index > tabs.Count)
            return;
        await FocusTabAsync(tabs[index - 1], ct).ConfigureAwait(false);
    }

    private async Task FocusWorkspaceIndexAsync(int index, CancellationToken ct)
    {
        var listed = await _port.CallAsync(ProtocolMethods.WorkspaceList, null, ct)
            .ConfigureAwait(false);
        var ids = ReadIds(listed, "workspace_id");
        if (index < 1 || index > ids.Count)
            return;
        await FocusWorkspaceAsync(ids[index - 1], ct).ConfigureAwait(false);
    }

    private async Task FocusAgentIndexAsync(int index, CancellationToken ct)
    {
        var agents = await ListAgentPaneIds(ct).ConfigureAwait(false);
        if (index < 1 || index > agents.Count)
            return;
        RememberPane(agents[index - 1]);
    }

    private async Task MoveTabAsync(int delta, CancellationToken ct)
    {
        if (TabId is null)
            return;
        var tabs = await ListTabsAsync(ct).ConfigureAwait(false);
        var idx = IndexOf(tabs, TabId);
        if (idx < 0)
            return;
        var next = idx + delta;
        if (next < 0 || next >= tabs.Count)
            return;
        await _port.CallAsync(
                ProtocolMethods.TabMove,
                new JsonObject
                {
                    ["tab_id"] = TabId,
                    ["index"] = next,
                },
                ct)
            .ConfigureAwait(false);
    }

    private async Task FocusTabAsync(string tabId, CancellationToken ct)
    {
        var focused = await _port.CallAsync(
                ProtocolMethods.TabFocus,
                new JsonObject { ["tab_id"] = tabId },
                ct)
            .ConfigureAwait(false);
        ApplyTab(focused);
        await RefreshFocusAsync(ct).ConfigureAwait(false);
    }

    private async Task FocusDirectionAsync(string direction, CancellationToken ct)
    {
        if (PaneId is null)
            return;

        var exported = await _port.CallAsync(
                ProtocolMethods.LayoutExport,
                TabId is { } tab
                    ? new JsonObject { ["tab_id"] = tab }
                    : new JsonObject { ["pane_id"] = PaneId },
                ct)
            .ConfigureAwait(false);
        if (exported.ValueKind != JsonValueKind.Object
            || !exported.TryGetProperty("root", out var rootEl)
            || rootEl.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var dto = AttachSession.DeserializePayload(rootEl, ProtocolJsonContext.Default.LayoutNodeDto);
        var root = LayoutTreeOperations.FromDto(dto, keepPaneId: true);
        if (root is null)
            return;

        var next = LayoutTreeOperations.FocusDirection(root, new PaneId(PaneId), direction);
        if (next?.PaneId is { } found)
            RememberPane(found.Value);
    }

    private async Task CyclePaneAsync(int delta, CancellationToken ct)
    {
        var exported = await _port.CallAsync(
                ProtocolMethods.LayoutExport,
                TabId is { } tab ? new JsonObject { ["tab_id"] = tab } : null,
                ct)
            .ConfigureAwait(false);
        var leaves = new List<string>();
        if (exported.ValueKind == JsonValueKind.Object
            && exported.TryGetProperty("root", out var root))
        {
            CollectLeaves(root, leaves);
        }

        if (leaves.Count == 0)
            return;
        var idx = IndexOf(leaves, PaneId);
        var next = leaves[Wrap(idx + delta, leaves.Count)];
        await FocusPaneAsync(next, ct).ConfigureAwait(false);
    }

    private Task FocusPaneAsync(string paneId, CancellationToken ct)
    {
        RememberPane(paneId);
        return Task.CompletedTask;
    }

    private async Task CycleAgentAsync(int delta, CancellationToken ct)
    {
        var agents = await ListAgentPaneIds(ct).ConfigureAwait(false);
        if (agents.Count == 0)
            return;

        // Shell pane or stale id is not in SortAgents.
        // treats that miss as first (next) / last (previous). IndexOf miss→0
        // would skip agents[0] on next.
        var idx = -1;
        if (PaneId is not null)
        {
            for (var i = 0; i < agents.Count; i++)
            {
                if (agents[i] == PaneId)
                {
                    idx = i;
                    break;
                }
            }
        }

        var next = idx < 0
            ? (delta < 0 ? agents[^1] : agents[0])
            : agents[Wrap(idx + delta, agents.Count)];
        await FocusPaneAsync(next, ct).ConfigureAwait(false);
    }

    private async Task<List<string>> ListAgentPaneIds(CancellationToken ct)
    {
        var snap = await _port.CallAsync(ProtocolMethods.SessionSnapshot, null, ct)
            .ConfigureAwait(false);
        return ListAgentPaneIds(snap, AgentPanelSort);
    }

    private static List<string> ListAgentPaneIds(JsonElement snap, AgentPanelSort sort)
    {
        var input = SidebarLiveModel.FromSnapshot(
            snap,
            AttachUiConfig.Default with { AgentPanelSort = sort },
            expanded: true,
            requestedWidth: 0);
        var agents = new List<string>();
        foreach (var pane in SidebarTokenGrammar.SortAgents(
                     input.Panes,
                     input.Workspaces,
                     input.Tabs,
                     sort))
        {
            agents.Add(pane.Id);
        }

        return agents;
    }

    private static JsonObject NewWorkspaceParams(string? label)
    {
        var obj = new JsonObject { ["create_pane"] = true };
        if (!string.IsNullOrEmpty(label))
            obj["label"] = label;
        return obj;
    }

    private async Task SwapAsync(string direction, CancellationToken ct)
    {
        if (PaneId is null)
            return;
        await _port.CallAsync(
                ProtocolMethods.PaneSwap,
                new JsonObject
                {
                    ["pane_id"] = PaneId,
                    ["direction"] = direction,
                },
                ct)
            .ConfigureAwait(false);
        await RefreshFocusAsync(ct).ConfigureAwait(false);
    }

    private async Task ResizeAsync(string direction, CancellationToken ct)
    {
        if (TabId is null || ResizeLease is null || PaneId is null)
            return;
        var exported = await _port.CallAsync(
                ProtocolMethods.LayoutExport,
                new JsonObject { ["tab_id"] = TabId },
                ct)
            .ConfigureAwait(false);
        if (exported.ValueKind != JsonValueKind.Object
            || !exported.TryGetProperty("root", out var root))
        {
            return;
        }

        if (!TryFindSplitOnAxis(root, PaneId, direction, [], out var path, out _, out var ratio))
            return;

        // Toward the shared split grows the focused pane. Right/down raise the
        var firstGrows = direction is "right" or "down";
        var next = ClampRatio(ratio + (firstGrows ? 0.05 : -0.05));
        var pathNodes = new JsonNode?[path.Count];
        for (var i = 0; i < path.Count; i++)
            pathNodes[i] = JsonValue.Create(path[i]);
        await _port.CallAsync(
                ProtocolMethods.LayoutSetSplitRatio,
                new JsonObject
                {
                    ["tab_id"] = TabId,
                    ["path"] = new JsonArray(pathNodes),
                    ["ratio"] = next,
                    ["lease_id"] = ResizeLease,
                },
                ct)
            .ConfigureAwait(false);
    }

    private async Task<List<string>> ListTabsAsync(CancellationToken ct)
    {
        var listed = await _port.CallAsync(
                ProtocolMethods.TabList,
                WorkspaceId is { } ws ? new JsonObject { ["workspace_id"] = ws } : null,
                ct)
            .ConfigureAwait(false);
        return ReadIds(listed, "tab_id");
    }

    private async Task RefreshFocusAsync(CancellationToken ct, bool recordLast = true)
    {
        var snap = await _port.CallAsync(ProtocolMethods.SessionSnapshot, null, ct)
            .ConfigureAwait(false);
        ApplyFocusFromSnapshot(snap, recordLast);
    }

    public void ApplyTab(JsonElement tab)
    {
        if (tab.ValueKind != JsonValueKind.Object)
            return;
        if (TryString(tab, "tab_id") is { } id)
            TabId = id;
        if (TryString(tab, "workspace_id") is { } ws)
            WorkspaceId = ws;
        if (TryString(tab, "focused_pane_id") is { } pane)
            RememberPane(pane);
    }

    public void ApplyPane(JsonElement pane)
    {
        if (pane.ValueKind != JsonValueKind.Object)
            return;
        if (TryString(pane, "workspace_id") is { } ws)
            WorkspaceId = ws;
        if (TryString(pane, "tab_id") is { } tab)
            TabId = tab;
        if (TryString(pane, "pane_id") is { } id)
            RememberPane(id);
    }

    public void ApplyGraphForPane(JsonElement snapshot, string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId) || snapshot.ValueKind != JsonValueKind.Object)
            return;
        if (snapshot.TryGetProperty("panes", out var panes) && panes.ValueKind == JsonValueKind.Array)
        {
            foreach (var pane in panes.EnumerateArray())
            {
                if (TryString(pane, "pane_id") != paneId)
                    continue;
                if (TryString(pane, "workspace_id") is { } ws)
                    WorkspaceId = ws;
                if (TryString(pane, "tab_id") is { } tab)
                    TabId = tab;
                return;
            }
        }
    }

    public Task RefreshFocusFromSnapshotAsync(CancellationToken ct) =>
        RefreshFocusAsync(ct);

    internal void PresentTab(string? workspaceId, string tabId, string? paneId)
    {
        if (!string.IsNullOrWhiteSpace(workspaceId))
            WorkspaceId = workspaceId;
        if (!string.IsNullOrWhiteSpace(tabId))
            TabId = tabId;
        if (!string.IsNullOrWhiteSpace(paneId))
            PaneId = paneId;
    }

    internal void ApplyFocusFromSnapshot(JsonElement snap, bool recordLast = false)
    {
        if (snap.ValueKind != JsonValueKind.Object)
            return;
        if (TryString(snap, "focused_workspace_id") is { } ws)
            WorkspaceId = ws;
        if (TryString(snap, "focused_tab_id") is { } tab)
            TabId = tab;
        PruneLastPane(snap);
        var pane = ChoosePane(snap);
        if (pane is not null)
        {
            if (recordLast)
                RememberPane(pane);
            else
                PaneId = pane;
        }
        else
        {
            PaneId = null;
            LastPaneId = null;
        }
    }

    private void RememberPane(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId))
            return;
        if (PaneId is { Length: > 0 } current && current != paneId)
            LastPaneId = current;
        PaneId = paneId;
    }

    private void ForgetClosedPane(string? closedPane)
    {
        if (string.IsNullOrWhiteSpace(closedPane))
            return;
        if (LastPaneId == closedPane)
            LastPaneId = null;
        if (LastPaneId == PaneId)
            LastPaneId = null;
    }

    private void PruneLastPane(JsonElement snap)
    {
        if (LastPaneId is not { Length: > 0 } last)
            return;
        if (!PaneExists(snap, last))
            LastPaneId = null;
    }

    private static string? ChoosePane(JsonElement snap)
    {
        if (TryString(snap, "focused_tab_id") is { } tabId
            && snap.TryGetProperty("tabs", out var tabs)
            && tabs.ValueKind == JsonValueKind.Array)
        {
            foreach (var tab in tabs.EnumerateArray())
            {
                if (TryString(tab, "tab_id") == tabId
                    && TryString(tab, "focused_pane_id") is { } pane)
                {
                    return pane;
                }
            }
        }

        if (snap.TryGetProperty("panes", out var panes) && panes.ValueKind == JsonValueKind.Array)
        {
            foreach (var pane in panes.EnumerateArray())
            {
                if (TryString(pane, "pane_id") is { } id)
                    return id;
            }
        }

        return null;
    }

    private static bool PaneExists(JsonElement snap, string paneId)
    {
        if (snap.TryGetProperty("panes", out var panes) && panes.ValueKind == JsonValueKind.Array)
        {
            foreach (var pane in panes.EnumerateArray())
            {
                if (TryString(pane, "pane_id") == paneId)
                    return true;
            }
        }

        if (snap.TryGetProperty("tabs", out var tabs) && tabs.ValueKind == JsonValueKind.Array)
        {
            foreach (var tab in tabs.EnumerateArray())
            {
                if (TryString(tab, "focused_pane_id") == paneId)
                    return true;
            }
        }

        return false;
    }

    private static List<string> ReadIds(JsonElement listed, string field)
    {
        var ids = new List<string>();
        if (listed.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in listed.EnumerateArray())
            {
                if (TryString(item, field) is { } id)
                    ids.Add(id);
            }

            return ids;
        }

        if (listed.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in listed.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var item in prop.Value.EnumerateArray())
                {
                    if (TryString(item, field) is { } id)
                        ids.Add(id);
                }
            }
        }

        return ids;
    }

    private static void CollectLeaves(JsonElement node, List<string> leaves)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return;
        var type = TryString(node, "type");
        if (string.Equals(type, "pane", StringComparison.Ordinal)
            || TryString(node, "pane_id") is not null && type is null)
        {
            if (TryString(node, "pane_id") is { } id)
                leaves.Add(id);
            return;
        }

        if (node.TryGetProperty("first", out var first))
            CollectLeaves(first, leaves);
        if (node.TryGetProperty("second", out var second))
            CollectLeaves(second, leaves);
    }

    private static bool TryFindSplitOnAxis(
        JsonElement node,
        string paneId,
        string direction,
        List<int> path,
        out List<int> splitPath,
        out bool isFirst,
        out double ratio)
    {
        splitPath = [];
        isFirst = true;
        ratio = 0.5;
        if (node.ValueKind != JsonValueKind.Object)
            return false;
        if (TryString(node, "pane_id") == paneId)
            return false;
        if (!node.TryGetProperty("first", out var first) || !node.TryGetProperty("second", out var second))
            return false;

        var downFirst = new List<int>(path) { 0 };
        if (TryFindSplitOnAxis(first, paneId, direction, downFirst, out splitPath, out isFirst, out ratio))
            return true;
        var downSecond = new List<int>(path) { 1 };
        if (TryFindSplitOnAxis(second, paneId, direction, downSecond, out splitPath, out isFirst, out ratio))
            return true;

        var inFirst = ContainsPane(first, paneId);
        var inSecond = ContainsPane(second, paneId);
        if (!inFirst && !inSecond)
            return false;
        if (!AxisMatches(direction, TryString(node, "direction")))
            return false;

        splitPath = [.. path];
        isFirst = inFirst;
        ratio = node.TryGetProperty("ratio", out var r) && r.TryGetDouble(out var d) ? d : 0.5;
        return true;
    }

    private static bool ContainsPane(JsonElement node, string paneId)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return false;
        if (TryString(node, "pane_id") == paneId)
            return true;
        if (node.TryGetProperty("first", out var first) && ContainsPane(first, paneId))
            return true;
        return node.TryGetProperty("second", out var second) && ContainsPane(second, paneId);
    }

    private static bool IsLeaf(JsonElement node) =>
        node.ValueKind == JsonValueKind.Object && TryString(node, "pane_id") is not null
        && (!node.TryGetProperty("first", out _) || TryString(node, "type") is "pane");

    private static bool AxisMatches(string direction, string? splitDir)
    {
        if (string.IsNullOrWhiteSpace(splitDir))
            return false;
        if (splitDir is "right" or "left" or "horizontal")
            return direction is "left" or "right";
        if (splitDir is "down" or "up" or "vertical")
            return direction is "up" or "down";
        return false;
    }

    private static double ClampRatio(double ratio)
    {
        if (ratio < 0.05)
            return 0.05;
        if (ratio > 0.95)
            return 0.95;
        return ratio;
    }

    private static int IndexOf(IReadOnlyList<string> ids, string? current)
    {
        if (current is null)
            return 0;
        for (var i = 0; i < ids.Count; i++)
        {
            if (ids[i] == current)
                return i;
        }

        return 0;
    }

    private static int Wrap(int index, int count)
    {
        if (count <= 0)
            return 0;
        var n = index % count;
        return n < 0 ? n + count : n;
    }

    private static string? TryString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return null;
        var value = prop.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

internal sealed class ControlPlaneAttachCommandPort : IAttachCommandPort
{
    private ControlPlane.ControlPlaneClient _client;
    private AttachLiveState? _shellLive;

    public ControlPlaneAttachCommandPort(ControlPlane.ControlPlaneClient client) =>
        _client = client ?? throw new ArgumentNullException(nameof(client));

    public void ReplaceClient(ControlPlane.ControlPlaneClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!ReferenceEquals(_client, client))
            _client.ClearShellChunkAdmit();
        _client = client;
        if (_shellLive is not null)
            BindShellReaderAdmit();
    }

    internal void UseShellLane(AttachLiveState live)
    {
        _shellLive = live ?? throw new ArgumentNullException(nameof(live));
        BindShellReaderAdmit();
    }

    private void BindShellReaderAdmit()
    {
        var live = _shellLive;
        if (live is null)
            return;
        var client = _client;
        client.SetShellChunkAdmit(ev =>
            AttachSession.TryAdmitReadLoopShellResponse(live, client, ev) is not null);
    }

    internal string? LastRequestId => _client.LastRequestId;

    internal string PeekNextRequestId() => _client.PeekNextRequestId();

    public async Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        var outcome = await CallWithRequestIdAsync(method, parameters, ct).ConfigureAwait(false);
        if (outcome.Error is not null)
        {
            ExceptionDispatchInfo.Capture(outcome.Error).Throw();
            return default;
        }

        return outcome.Result;
    }

    internal async Task<ControlPlaneCallResult> CallWithRequestIdAsync(
        string method,
        JsonObject? parameters,
        CancellationToken ct)
    {
        if (string.Equals(method, ProtocolMethods.PaneSendKeys, StringComparison.Ordinal))
        {
            try
            {
                await _client.NotifyAsync(ProtocolMethods.PaneSendKeys, parameters, ct)
                    .ConfigureAwait(false);
                return new ControlPlaneCallResult(default, null, null);
            }
            catch (Exception ex)
            {
                return new ControlPlaneCallResult(default, null, ex);
            }
        }

        // The attach lane owns the wait.
        // This client only writes the request line.
        if (_shellLive is { } live && AttachShellEndpointMethods.IsEndpointAction(method))
            return await AttachShellEndpointDispatch.Route(live, _client, method, parameters, ct)
                .ConfigureAwait(false);

        return await _client.CallWithRequestIdAsync(method, parameters, ct).ConfigureAwait(false);
    }
}
