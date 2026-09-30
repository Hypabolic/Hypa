using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.AgentRuntime.Application.Sidebar;

public enum SidebarSectionSort
{
    Order,
    Attention,
}

public sealed record SidebarCollectionTarget
{
    public string? PaneId { get; init; }

    public string? WorkspaceId { get; init; }

    public string? TabId { get; init; }
}

public sealed record SidebarCollectionActivation
{
    public required string SectionId { get; init; }

    public required string PluginId { get; init; }

    public required string ItemId { get; init; }

    public required string ResourceId { get; init; }

    public required long Revision { get; init; }

    public string? ActionId { get; init; }

    public SidebarCollectionTarget? Target { get; init; }
}

public sealed record SidebarPluginCollectionItemView
{
    public required string Id { get; init; }

    public string? Label { get; init; }

    public required string Status { get; init; }

    public long Attention { get; init; }

    public long? Sequence { get; init; }

    public IReadOnlyDictionary<string, string> Tokens { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string? ActionId { get; init; }

    public SidebarCollectionTarget? Target { get; init; }
}

public sealed record SidebarPluginResourceView
{
    public required string ResourceId { get; init; }

    public required long Revision { get; init; }

    public required string Freshness { get; init; }

    public string? Summary { get; init; }

    public IReadOnlyList<SidebarPluginCollectionItemView> Items { get; init; } = [];
}

public sealed record SidebarPluginSectionBinding
{
    public required string Id { get; init; }

    public required string ResourceId { get; init; }

    public required string Title { get; init; }

    public SidebarSectionSort Sort { get; init; } = SidebarSectionSort.Order;

    public int Order { get; init; }

    public bool Collapsed { get; init; }

    public int RowGap { get; init; }

    public IReadOnlyList<IReadOnlyList<SidebarTokenSpec>> Rows { get; init; } = [];
}
