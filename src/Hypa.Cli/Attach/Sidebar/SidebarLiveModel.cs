using System.Text.Json;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach.Mouse;

namespace Hypa.Cli.Attach.Sidebar;

/// <summary>Maps a session snapshot and composer frame to live sidebar rows.</summary>
public static class SidebarLiveModel
{
    public static SidebarComposeInput FromSnapshot(
        JsonElement snapshot,
        AttachUiConfig ui,
        bool expanded,
        int requestedWidth,
        ISidebarGitStatus? git = null,
        IReadOnlySet<string>? collapsedSectionIds = null,
        IReadOnlySet<string>? collapsedWorktreeGroups = null,
        IReadOnlySet<string>? collapsedTreeIds = null,
        IReadOnlyList<SidebarCubeItem>? cubes = null,
        SidebarCubeCatalogState cubesState = SidebarCubeCatalogState.Ready,
        bool globalMenuAttentionBadgeVisible = false,
        string? navigatedPaneId = null,
        IReadOnlyList<SidebarPluginResourceView>? pluginResources = null,
        IReadOnlySet<string>? linkedPluginIds = null,
        string? focusedCubeId = null,
        string? connectedCubeId = null)
    {
        var workspaces = new List<SidebarWorkspaceItem>();
        var tabs = new List<SidebarTabItem>();
        var panes = new List<SidebarPaneItem>();
        string? focusedWorkspace = null;
        string? focusedTab = null;

        if (snapshot.ValueKind == JsonValueKind.Object)
        {
            focusedWorkspace = ReadString(snapshot, "focused_workspace_id");
            focusedTab = ReadString(snapshot, "focused_tab_id");
            if (snapshot.TryGetProperty("workspaces", out var ws) && ws.ValueKind == JsonValueKind.Array)
            {
                var order = 0;
                foreach (var item in ws.EnumerateArray())
                {
                    var id = ReadString(item, "workspace_id");
                    if (string.IsNullOrWhiteSpace(id))
                        continue;
                    var worktree = ReadWorktree(item);
                    workspaces.Add(new SidebarWorkspaceItem
                    {
                        Id = id,
                        Label = ReadString(item, "label") ?? id,
                        Cwd = ReadString(item, "cwd") ?? "",
                        FocusedTabId = ReadString(item, "focused_tab_id"),
                        Order = order++,
                        WorktreeKey = worktree.Key,
                        WorktreeLabel = worktree.Label,
                        IsLinkedWorktree = worktree.IsLinked,
                        Branch = ReadString(item, "branch") ?? "",
                        CustomLabel = ReadBool(item, "custom_label"),
                        Tokens = ReadTokens(item),
                    });
                }
            }

            if (snapshot.TryGetProperty("tabs", out var tabEl) && tabEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in tabEl.EnumerateArray())
                {
                    var id = ReadString(item, "tab_id");
                    if (string.IsNullOrWhiteSpace(id))
                        continue;
                    tabs.Add(new SidebarTabItem
                    {
                        Id = id,
                        WorkspaceId = ReadString(item, "workspace_id") ?? "",
                        Label = ReadString(item, "label") ?? id,
                        FocusedPaneId = ReadString(item, "focused_pane_id"),
                        Ordinal = ReadInt(item, "ordinal"),
                    });
                }
            }

            if (snapshot.TryGetProperty("panes", out var paneEl) && paneEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in paneEl.EnumerateArray())
                {
                    var id = ReadString(item, "pane_id");
                    if (string.IsNullOrWhiteSpace(id))
                        continue;
                    var pane = new SidebarPaneItem
                    {
                        Id = id,
                        TabId = ReadString(item, "tab_id") ?? "",
                        WorkspaceId = ReadString(item, "workspace_id") ?? "",
                        Label = ReadString(item, "label") ?? "",
                        Agent = ReadString(item, "agent"),
                        State = ReadString(item, "state") ?? SidebarTokenGrammar.Unknown,
                        TerminalTitle = ReadString(item, "terminal_title") ?? "",
                        Tokens = ReadTokens(item),
                    };
                    HiddenPaneSnapshotMapper.ApplyToPaneItem(item, ref pane);
                    panes.Add(pane);
                }
            }
        }

        var focusedPane = tabs.FirstOrDefault(t => t.Id == focusedTab)?.FocusedPaneId;
        var agentViewLabel = ReadAgentViewLabel(snapshot);
        var agentOrder = ReadStringList(snapshot, "agent_order");
        return new SidebarComposeInput
        {
            Ui = ui,
            Workspaces = workspaces,
            Tabs = tabs,
            Panes = panes,
            Cubes = cubes ?? [],
            CubesState = cubesState,
            FocusedWorkspaceId = focusedWorkspace,
            FocusedTabId = focusedTab,
            FocusedPaneId = focusedPane,
            FocusedCubeId = focusedCubeId,
            ConnectedCubeId = connectedCubeId,
            NavigatedPaneId = navigatedPaneId,
            Expanded = expanded,
            MouseCapture = ui.MouseCapture,
            RequestedWidth = requestedWidth,
            Git = git,
            CollapsedSectionIds = collapsedSectionIds,
            CollapsedWorktreeGroups = collapsedWorktreeGroups,
            CollapsedTreeIds = collapsedTreeIds,
            GlobalMenuAttentionBadgeVisible = globalMenuAttentionBadgeVisible,
            AgentOrder = agentOrder,
            AgentViewLabel = agentViewLabel,
            AgentViewHasSort = ReadAgentViewHasSort(snapshot),
            PluginResources = pluginResources ?? ReadPluginResources(snapshot),
            LinkedPluginIds = linkedPluginIds,
        };
    }

    public static IReadOnlyList<SidebarPluginResourceView> ReadPluginResources(JsonElement snapshot)
    {
        if (snapshot.ValueKind != JsonValueKind.Object
            || !snapshot.TryGetProperty("resources", out var resources)
            || resources.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<SidebarPluginResourceView>();
        foreach (var item in resources.EnumerateArray())
        {
            var mapped = MapResource(item);
            if (mapped is not null)
                list.Add(mapped);
        }

        return list;
    }

    public static IReadOnlySet<string> ReadLinkedPluginIds(JsonElement listResult)
    {
        if (listResult.ValueKind != JsonValueKind.Object
            || !listResult.TryGetProperty("plugins", out var plugins)
            || plugins.ValueKind != JsonValueKind.Array)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plugin in plugins.EnumerateArray())
        {
            if (plugin.TryGetProperty("plugin_id", out var id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() is { Length: > 0 } text)
            {
                ids.Add(text);
            }
        }

        return ids;
    }

    public static IReadOnlyList<SidebarStubRow> ToStubRows(SidebarFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Display is SidebarCollapseDisplay.Hidden || frame.Width <= 0)
            return [];

        var rows = new List<SidebarStubRow>();
        var offset = 0;
        var compact = frame.Display is SidebarCollapseDisplay.Compact;
        foreach (var pane in frame.Panes)
        {
            if (!pane.Visible)
                continue;
            if (!compact)
            {
                rows.Add(new SidebarStubRow(
                    SidebarStubKind.Section,
                    pane.Id,
                    pane.Title.Length > 0 ? pane.Title : pane.Header.Trim(),
                    offset));
                offset++;
            }

            if (pane.Collapsed)
                continue;

            if (pane.Rows.Count == 0
                && (!compact
                    || string.Equals(pane.Id, SidebarTokenGrammar.CubesId, StringComparison.Ordinal)))
            {
                rows.Add(new SidebarStubRow(
                    SidebarStubKind.Empty,
                    pane.Id + ":empty",
                    pane.EmptyText,
                    offset));
                offset++;
                continue;
            }

            foreach (var row in pane.Rows)
            {
                var label = compact ? row.CompactLabel : row.Label;
                var kind = compact || label.Length > 0 || row.Selected
                    ? row.Kind switch
                    {
                        SidebarRowKind.Workspace => SidebarStubKind.Workspace,
                        SidebarRowKind.Cube => SidebarStubKind.Cube,
                        SidebarRowKind.Group => SidebarStubKind.Group,
                        SidebarRowKind.HiddenPane => SidebarStubKind.HiddenPane,
                        SidebarRowKind.CollectionItem => SidebarStubKind.CollectionItem,
                        _ => SidebarStubKind.Agent,
                    }
                    : SidebarStubKind.Gap;
                rows.Add(new SidebarStubRow(kind, row.Id, label, offset, row.Selected));
                offset++;
            }
        }

        return rows;
    }

    public static IReadOnlyList<GotoTarget> WorkspaceCatalog(SidebarComposeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var list = new List<GotoTarget>();
        foreach (var workspace in input.Workspaces)
        {
            list.Add(new GotoTarget(
                GotoTargetKind.Workspace,
                workspace.Id,
                string.IsNullOrWhiteSpace(workspace.Label) ? workspace.Id : workspace.Label));
        }

        return list;
    }

    public static IReadOnlyList<GotoTarget> Catalog(
        SidebarComposeInput input,
        IEnumerable<Hypa.AgentRuntime.Application.Plugins.InstalledPlugin>? plugins = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var list = new List<GotoTarget>();
        foreach (var workspace in input.Workspaces)
        {
            list.Add(new GotoTarget(
                GotoTargetKind.Workspace,
                workspace.Id,
                string.IsNullOrWhiteSpace(workspace.Label) ? workspace.Id : workspace.Label));
        }

        foreach (var tab in input.Tabs)
        {
            list.Add(new GotoTarget(
                GotoTargetKind.Tab,
                tab.Id,
                string.IsNullOrWhiteSpace(tab.Label) ? tab.Id : tab.Label));
        }

        foreach (var pane in input.Panes)
        {
            list.Add(new GotoTarget(
                GotoTargetKind.Pane,
                pane.Id,
                string.IsNullOrWhiteSpace(pane.Label) ? pane.Id : pane.Label,
                SidebarTokenGrammar.CanonicalState(pane.State)));
            if (!string.IsNullOrWhiteSpace(pane.Agent))
            {
                list.Add(new GotoTarget(
                    GotoTargetKind.Agent,
                    pane.Id,
                    pane.Agent,
                    SidebarTokenGrammar.CanonicalState(pane.State)));
            }
        }

        if (plugins is not null)
            list.AddRange(Hypa.Cli.Attach.Plugins.PluginChromeCatalog.PaletteTargets(plugins));

        return list;
    }

    private static (string Key, string Label, bool IsLinked) ReadWorktree(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("worktree", out var worktree)
            || worktree.ValueKind != JsonValueKind.Object)
        {
            return ("", "", false);
        }

        return (
            ReadString(worktree, "key") ?? "",
            ReadString(worktree, "label") ?? "",
            ReadBool(worktree, "is_linked_worktree"));
    }

    private static bool ReadBool(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True;

    private static string? ReadString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadAgentViewLabel(JsonElement snapshot)
    {
        if (snapshot.ValueKind != JsonValueKind.Object
            || !snapshot.TryGetProperty("agent_view", out var view)
            || view.ValueKind != JsonValueKind.Object)
            return null;
        if (view.TryGetProperty("active", out var active)
            && active.ValueKind == JsonValueKind.False)
            return null;
        return ReadString(view, "label") ?? "filtered";
    }

    private static bool? ReadAgentViewHasSort(JsonElement snapshot)
    {
        if (snapshot.ValueKind != JsonValueKind.Object
            || !snapshot.TryGetProperty("agent_view", out var view)
            || view.ValueKind != JsonValueKind.Object)
            return null;
        if (!view.TryGetProperty("has_sort", out var hasSort))
            return null;
        if (hasSort.ValueKind == JsonValueKind.True)
            return true;
        if (hasSort.ValueKind == JsonValueKind.False)
            return false;
        return null;
    }

    private static IReadOnlyList<string> ReadStringList(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object
            || !el.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text)
                list.Add(text);
        }

        return list;
    }

    private static int ReadInt(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var value)
        && value.TryGetInt32(out var n)
            ? n
            : 0;

    private static SidebarPluginResourceView? MapResource(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;
        var resourceId = ReadString(item, "resource_id");
        if (string.IsNullOrWhiteSpace(resourceId))
            return null;
        var revision = ReadLong(item, "revision");
        var freshness = ReadString(item, "freshness") ?? PluginResourceLimits.FreshnessReady;
        if (!item.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return new SidebarPluginResourceView
            {
                ResourceId = resourceId,
                Revision = revision,
                Freshness = freshness,
            };
        }

        var summary = ReadString(value, "summary");
        var items = new List<SidebarPluginCollectionItemView>();
        if (value.TryGetProperty("items", out var rawItems) && rawItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var raw in rawItems.EnumerateArray())
            {
                var mapped = MapCollectionItem(raw);
                if (mapped is not null)
                    items.Add(mapped);
            }
        }

        return new SidebarPluginResourceView
        {
            ResourceId = resourceId,
            Revision = revision,
            Freshness = freshness,
            Summary = summary,
            Items = items,
        };
    }

    private static SidebarPluginCollectionItemView? MapCollectionItem(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object)
            return null;
        var id = ReadString(raw, "id");
        if (string.IsNullOrWhiteSpace(id))
            return null;
        SidebarCollectionTarget? target = null;
        if (raw.TryGetProperty("target", out var rawTarget) && rawTarget.ValueKind == JsonValueKind.Object)
        {
            target = new SidebarCollectionTarget
            {
                PaneId = ReadString(rawTarget, "pane_id"),
                WorkspaceId = ReadString(rawTarget, "workspace_id"),
                TabId = ReadString(rawTarget, "tab_id"),
            };
        }

        return new SidebarPluginCollectionItemView
        {
            Id = id,
            Label = ReadString(raw, "label"),
            Status = ReadString(raw, "status") ?? SidebarTokenGrammar.Unknown,
            Attention = ReadLong(raw, "attention"),
            Sequence = ReadNullableLong(raw, "seq"),
            Tokens = ReadTokens(raw),
            ActionId = ReadString(raw, "action"),
            Target = target,
        };
    }

    private static long ReadLong(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var value)
        && value.TryGetInt64(out var n)
            ? n
            : 0;

    private static long? ReadNullableLong(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var value)
        && value.TryGetInt64(out var n)
            ? n
            : null;

    private static IReadOnlyDictionary<string, string> ReadTokens(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object
            || !el.TryGetProperty("tokens", out var tokens)
            || tokens.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in tokens.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.String)
                result[property.Name] = property.Value.GetString() ?? string.Empty;
        return result;
    }
}
