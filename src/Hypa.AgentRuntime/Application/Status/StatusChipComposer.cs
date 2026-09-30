using Hypa.AgentRuntime.Application.Sidebar;

namespace Hypa.AgentRuntime.Application.Status;

/// <summary>
/// Bounded left-to-right status chips. Core order is session, workspace,
/// pane, agent. Resource extras come from <see cref="StatusResourceChipComposer"/>.
/// </summary>
public static class StatusChipComposer
{
    public static StatusChipLine Compose(StatusChipComposeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var extras = input.Extras ?? [];
        var core = new List<StatusChip>(4)
        {
            Chip(StatusChipKind.Session, SessionText(input.SessionName)),
        };

        var workspace = DisplayName(input.WorkspaceLabel, null);
        if (workspace.Length > 0)
            core.Add(Chip(StatusChipKind.Workspace, workspace));

        var pane = PaneText(input.PaneLabel, input.PaneId);
        if (pane.Length > 0)
            core.Add(Chip(StatusChipKind.Pane, pane));

        var agent = AgentText(input);
        if (agent.Length > 0)
            core.Add(Chip(StatusChipKind.Agent, agent));

        var resourceExtras = new List<StatusChip>();
        foreach (var extra in extras)
        {
            if (extra.Kind is StatusChipKind.Resource)
                resourceExtras.Add(extra with { DisplayWidth = WidthOf(extra.Text) });
        }

        var error = SafeDisplayText.Encode(input.Error);
        StatusChip? errorChip = error.Length > 0
            ? Chip(StatusChipKind.Error, error, "error")
            : null;

        return Clamp(core, resourceExtras, errorChip, input.MaxCols);
    }

    public static string Join(IReadOnlyList<StatusChip> chips)
    {
        if (chips is null || chips.Count == 0)
            return "";
        var parts = new List<string>(chips.Count);
        foreach (var chip in chips)
        {
            if (chip.Text.Length == 0)
                continue;
            parts.Add(chip.Text);
        }

        return string.Join(' ', parts);
    }

    public static string SessionText(string? name)
    {
        var encoded = SafeDisplayText.Encode(name);
        return encoded.Length > 0 ? encoded : "session";
    }

    public static string PaneText(string? label, string? paneId)
    {
        var id = SafeDisplayText.Encode(paneId);
        var name = SafeDisplayText.Encode(label);
        if (name.Length == 0)
            return id;
        if (id.Length == 0 || string.Equals(name, id, StringComparison.Ordinal))
            return name;
        return $"{name} ({id})";
    }

    public static string AgentText(StatusChipComposeInput input)
    {
        if (!HasOccupant(input))
            return "";

        var icon = SidebarTokenGrammar.StateIcon(input.AgentState, input.StatusIndicators);
        var state = SidebarTokenGrammar.StateText(input.AgentState);
        var name = SafeDisplayText.Encode(input.AgentName);
        var parts = new List<string>(3);
        if (icon.Length > 0)
            parts.Add(icon);
        if (state.Length > 0)
            parts.Add(state);
        if (name.Length > 0)
            parts.Add(name);
        return string.Join(' ', parts);
    }

    internal static bool HasOccupant(StatusChipComposeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var name = SafeDisplayText.Encode(input.AgentName);
        if (name.Length > 0)
            return true;
        if (string.IsNullOrWhiteSpace(input.AgentState))
            return false;
        return SidebarTokenGrammar.CanonicalState(input.AgentState) != SidebarTokenGrammar.Unknown;
    }

    private static StatusChipLine Clamp(
        List<StatusChip> core,
        List<StatusChip> extras,
        StatusChip? error,
        int maxCols)
    {
        var cols = Math.Max(0, maxCols);
        var selected = new List<StatusChip>(core.Count + extras.Count + 1);
        selected.AddRange(core);
        selected.AddRange(extras);
        if (error is not null)
            selected.Add(error);

        while (WidthOfJoined(selected) > cols && extras.Count > 0)
        {
            extras.RemoveAt(extras.Count - 1);
            selected = Rebuild(core, extras, error);
        }

        while (WidthOfJoined(selected) > cols && TryDropKind(core, StatusChipKind.Agent))
            selected = Rebuild(core, extras, error);

        while (WidthOfJoined(selected) > cols && TryDropKind(core, StatusChipKind.Pane))
            selected = Rebuild(core, extras, error);

        var text = SafeDisplayText.Clip(Join(selected), cols);
        return new StatusChipLine { Chips = selected, Text = text };
    }

    private static List<StatusChip> Rebuild(
        List<StatusChip> core,
        List<StatusChip> extras,
        StatusChip? error)
    {
        var selected = new List<StatusChip>(core.Count + extras.Count + 1);
        selected.AddRange(core);
        selected.AddRange(extras);
        if (error is not null)
            selected.Add(error);
        return selected;
    }

    private static bool TryDropKind(List<StatusChip> core, StatusChipKind kind)
    {
        for (var i = core.Count - 1; i >= 0; i--)
        {
            if (core[i].Kind != kind)
                continue;
            core.RemoveAt(i);
            return true;
        }

        return false;
    }

    private static int WidthOfJoined(IReadOnlyList<StatusChip> chips) =>
        SafeDisplayText.Width(Join(chips));

    private static StatusChip Chip(StatusChipKind kind, string text, string? severity = null)
    {
        var encoded = SafeDisplayText.Encode(text);
        return new StatusChip
        {
            Kind = kind,
            Text = encoded,
            Severity = severity,
            DisplayWidth = WidthOf(encoded),
        };
    }

    private static int WidthOf(string text) => SafeDisplayText.Width(text);

    private static string DisplayName(string? label, string? id)
    {
        var name = SafeDisplayText.Encode(label);
        return name.Length > 0 ? name : SafeDisplayText.Encode(id);
    }
}
