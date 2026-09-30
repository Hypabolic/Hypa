using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Hypa.AgentRuntime.Domain;

/// <summary>
/// Canonical fingerprint over session graph metadata used for checkpoint conflict detection.
/// Terminal journal advances after prepare do not change the fingerprint.
/// Session lifecycle is excluded so freeze itself is not a conflict.
/// Pane process lifecycle_state is excluded: natural exits while frozen must not
/// brick export with checkpoint_conflict. Stable identity is id/cwd/command/
/// binding/occupant_generation.
/// </summary>
public static class SessionGraphFingerprint
{
    /// <summary>
    /// Compute a stable lowercase hex SHA-256 over ordered session graph metadata.
    /// </summary>
    public static string Compute(SessionState session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var root = new JsonObject
        {
            ["session_id"] = session.Id.Value,
            ["placement"] = session.Placement,
            ["placement_generation"] = session.PlacementGeneration,
            ["governed"] = session.Governed,
            ["binding"] = BindingNode(session.Binding),
            ["process_tenant_id"] = session.ProcessTenantId,
            ["process_run_id"] = session.ProcessRunId,
            ["focused_workspace_id"] = session.FocusedWorkspaceId?.Value,
        };

        var workspaces = new JsonArray();
        foreach (var key in session.Workspaces.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var ws = session.Workspaces[key];
            workspaces.Add((JsonNode)new JsonObject
            {
                ["workspace_id"] = ws.Id.Value,
                ["ordinal"] = ws.Ordinal,
                ["label"] = ws.Label,
                ["cwd"] = ws.Cwd,
                ["focused_tab_id"] = ws.FocusedTabId?.Value,
                ["binding"] = BindingNode(ws.Binding),
                ["tab_ids"] = ToSortedArray(ws.TabIds.Select(t => t.Value)),
            });
        }

        root["workspaces"] = workspaces;

        var tabs = new JsonArray();
        foreach (var key in session.Tabs.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var tab = session.Tabs[key];
            tabs.Add((JsonNode)new JsonObject
            {
                ["tab_id"] = tab.Id.Value,
                ["workspace_id"] = tab.WorkspaceId.Value,
                ["label"] = tab.Label,
                ["ordinal"] = tab.Ordinal,
                ["pane_ids"] = ToSortedArray(tab.PaneIds.Select(p => p.Value)),
                ["hidden_pane_ids"] = ToSortedArray(tab.HiddenPaneIds.Select(p => p.Value)),
                ["focused_pane_id"] = tab.FocusedPaneId?.Value,
                ["zoomed"] = tab.Zoomed,
                ["zoomed_pane_id"] = tab.ZoomedPaneId?.Value,
                ["layout"] = tab.LayoutRoot?.ToJsonObject(includePaneId: true),
            });
        }

        root["tabs"] = tabs;

        var panes = new JsonArray();
        foreach (var key in session.Panes.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var pane = session.Panes[key];
            // Deliberately omit pane.LifecycleState (running/exited): process death after
            // prepare is not a graph mutation for G1 conflict detection.
            panes.Add((JsonNode)new JsonObject
            {
                ["pane_id"] = pane.Id.Value,
                ["tab_id"] = pane.TabId.Value,
                ["workspace_id"] = pane.WorkspaceId.Value,
                ["label"] = pane.Label,
                ["cwd"] = pane.Cwd,
                ["command"] = pane.Command,
                ["args"] = ToOrderedArray(pane.Args),
                ["occupant_generation"] = pane.OccupantGeneration,
                ["placement"] = PanePlacementWire.ToWire(pane.Placement),
                ["parent_pane_id"] = pane.ParentPaneId?.Value,
                ["binding"] = BindingNode(pane.Binding),
            });
        }

        root["panes"] = panes;

        // Compact canonical JSON (JsonObject.ToJsonString is stable for our construction order).
        var bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static JsonNode? BindingNode(AtomicBinding? b)
    {
        if (b is null)
            return null;
        return new JsonObject
        {
            ["agent_session_id"] = b.AgentSessionId,
            ["run_id"] = b.RunId,
            ["step_id"] = b.StepId,
            ["memory_id"] = b.MemoryId,
            ["project_root"] = b.ProjectRoot,
            ["tenant_id"] = b.TenantId,
        };
    }

    private static JsonArray ToSortedArray(IEnumerable<string> values)
    {
        // AOT-safe: construct from JsonNode[] (primitive JsonValue only) — avoid Add<T>.
        var ordered = values.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        return ToOrderedArray(ordered);
    }

    private static JsonArray ToOrderedArray(IReadOnlyList<string> values)
    {
        JsonNode?[] nodes = new JsonNode?[values.Count];
        for (var i = 0; i < values.Count; i++)
            nodes[i] = JsonValue.Create(values[i]);
        return new JsonArray(nodes);
    }
}
