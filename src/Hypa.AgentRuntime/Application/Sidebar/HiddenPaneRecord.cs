using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application.Sidebar;

public enum HiddenPaneRowKind
{
    Parent,
    Child,
    Background,
    Revealed,
}

public sealed record HiddenPaneRecord
{
    public required string PaneId { get; init; }
    public required string TabId { get; init; }
    public required string WorkspaceId { get; init; }
    public string Label { get; init; } = "";
    public string? AgentKind { get; init; }
    public string State { get; init; } = SidebarTokenGrammar.Unknown;
    public bool Hidden { get; init; }
    public string Placement { get; init; } = PanePlacementWire.Tiled;
    public string? ParentPaneId { get; init; }

    public bool InCatalog =>
        Hidden || !string.IsNullOrWhiteSpace(ParentPaneId);
}

public sealed record HiddenPaneTreeRow
{
    public required string Id { get; init; }
    public string? PaneId { get; init; }
    public string? WorkspaceId { get; init; }
    public required HiddenPaneRowKind Kind { get; init; }
    public int Depth { get; init; }
    public bool Expandable { get; init; }
    public bool Collapsed { get; init; }
    public bool Hidden { get; init; }
    public string State { get; init; } = SidebarTokenGrammar.Unknown;
    public string Label { get; init; } = "";
    public string? AgentKind { get; init; }
    public string? ParentPaneId { get; init; }
    public string? TabId { get; init; }
    public bool LastChild { get; init; }
}

public sealed record HiddenPaneTree
{
    public IReadOnlyList<HiddenPaneTreeRow> Rows { get; init; } = [];
    public IReadOnlyList<HiddenPaneRecord> Catalog { get; init; } = [];
}

public static class HiddenPaneIds
{
    public const string BackgroundPrefix = "background:";
    public const string BackgroundLabel = "Background";

    public static string BackgroundGroup(string workspaceId) =>
        BackgroundPrefix + workspaceId;

    public static bool IsBackgroundGroup(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.StartsWith(BackgroundPrefix, StringComparison.Ordinal);
}
