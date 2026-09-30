namespace Hypa.AgentRuntime.Application.Sidebar;

/// <summary>
// / Bounded client view preference.
/// <c>src/client/shell/preferences.rs:11-21</c> stores an optional
/// section split and <c>collapsed_groups</c>.
/// </summary>
public sealed record ClientViewPreferences
{
    public float? SidebarSectionSplit { get; init; }

    /// <summary>
    /// <c>collapsed_groups</c>. Empty after normalize becomes null.
    /// </summary>
    public string[]? CollapsedGroups { get; init; }

    /// <summary>
    // / Last selected cube.
    /// <c>src/client/shell/endpoints.rs:173-178</c> keeps
    /// <c>active_endpoint_id</c> and applies that endpoint snapshot.
    /// </summary>
    public string? LastPlacementId { get; init; }

    public static ClientViewPreferences Empty { get; } = new();
}

/// <summary>
/// Port for client-view preference load and save. Tests inject a temp
/// directory. Production uses the attach config XDG path tree.
/// </summary>
public interface IClientViewPreferencesStore
{
    string ResolvePath();

    ClientViewPreferences Load();

    bool Save(ClientViewPreferences preferences);
}
