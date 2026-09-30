using System.Text.Json;
using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application.Sidebar;

/// <summary>
/// Reads validated parent provenance from the actual snapshot pane objects.
/// Does not infer a parent from tab, cwd, or label.
/// </summary>
public static class HiddenPaneSnapshotMapper
{
    public static IReadOnlyList<HiddenPaneRecord> Parse(JsonElement snapshot)
    {
        if (snapshot.ValueKind != JsonValueKind.Object
            || !snapshot.TryGetProperty("panes", out var panes)
            || panes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<HiddenPaneRecord>();
        foreach (var item in panes.EnumerateArray())
        {
            var record = ReadPane(item);
            if (record is not null)
                list.Add(record);
        }

        return list;
    }

    public static HiddenPaneRecord FromPaneItem(SidebarPaneItem pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        var hidden = pane.Hidden
            || string.Equals(pane.Placement, PanePlacementWire.Hidden, StringComparison.OrdinalIgnoreCase);
        return new HiddenPaneRecord
        {
            PaneId = pane.Id,
            TabId = pane.TabId,
            WorkspaceId = pane.WorkspaceId,
            Label = pane.Label,
            AgentKind = pane.Agent,
            State = SidebarTokenGrammar.CanonicalState(pane.State),
            Hidden = hidden,
            Placement = hidden ? PanePlacementWire.Hidden : PanePlacementWire.Tiled,
            ParentPaneId = ValidateParentId(pane.Id, pane.ParentPaneId),
        };
    }

    public static IReadOnlyList<HiddenPaneRecord> FromPaneItems(IReadOnlyList<SidebarPaneItem> panes)
    {
        ArgumentNullException.ThrowIfNull(panes);
        var list = new List<HiddenPaneRecord>(panes.Count);
        foreach (var pane in panes)
            list.Add(FromPaneItem(pane));
        return list;
    }

    public static void ApplyToPaneItem(JsonElement item, ref SidebarPaneItem pane)
    {
        var hidden = ReadBool(item, "hidden");
        var placement = ReadString(item, "placement");
        if (string.Equals(placement, PanePlacementWire.Hidden, StringComparison.OrdinalIgnoreCase))
            hidden = true;
        pane = pane with
        {
            Hidden = hidden,
            Placement = hidden ? PanePlacementWire.Hidden : (placement ?? PanePlacementWire.Tiled),
            ParentPaneId = ValidateParentId(pane.Id, ReadString(item, "parent_pane_id")),
        };
    }

    internal static string? ValidateParentId(string paneId, string? claimed)
    {
        if (string.IsNullOrWhiteSpace(claimed))
            return null;
        var parent = claimed.Trim();
        if (string.Equals(parent, paneId, StringComparison.Ordinal))
            return null;
        return parent;
    }

    private static HiddenPaneRecord? ReadPane(JsonElement item)
    {
        var id = ReadString(item, "pane_id");
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var hidden = ReadBool(item, "hidden");
        var placement = ReadString(item, "placement");
        if (string.Equals(placement, PanePlacementWire.Hidden, StringComparison.OrdinalIgnoreCase))
            hidden = true;

        return new HiddenPaneRecord
        {
            PaneId = id,
            TabId = ReadString(item, "tab_id") ?? "",
            WorkspaceId = ReadString(item, "workspace_id") ?? "",
            Label = ReadString(item, "label") ?? "",
            AgentKind = ReadString(item, "agent"),
            State = SidebarTokenGrammar.CanonicalState(ReadString(item, "state")),
            Hidden = hidden,
            Placement = hidden ? PanePlacementWire.Hidden : PanePlacementWire.Tiled,
            ParentPaneId = ValidateParentId(id, ReadString(item, "parent_pane_id")),
        };
    }

    private static string? ReadString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBool(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True;
}
