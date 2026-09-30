using Hypa.AgentRuntime.Application.Metadata;
using Hypa.AgentRuntime.Application.Plugins;

namespace Hypa.AgentRuntime.Application.Sidebar;

/// <summary>Maps plugin resource records to sidebar views.</summary>
public static class SidebarPluginResourceMapper
{
    public static SidebarPluginResourceView? FromRecord(PluginResourceRecord? record)
    {
        if (record is null)
            return null;
        return new SidebarPluginResourceView
        {
            ResourceId = record.Id.Value,
            Revision = record.Revision,
            Freshness = record.Freshness,
            Summary = record.Value.Summary,
            Items = record.Value.Items.Select(FromItem).ToArray(),
        };
    }

    public static SidebarPluginCollectionItemView FromItem(PluginCollectionItem item) =>
        new()
        {
            Id = item.Id,
            Label = item.Label,
            Status = item.Status,
            Attention = item.Attention,
            Sequence = item.Sequence,
            Tokens = item.Tokens,
            ActionId = item.Action,
            Target = item.Target is null
                ? null
                : new SidebarCollectionTarget
                {
                    PaneId = item.Target.PaneId,
                    WorkspaceId = item.Target.WorkspaceId,
                    TabId = item.Target.TabId,
                },
        };

    public static bool ContainsControlCharacters(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        foreach (var ch in value)
        {
            if (char.IsControl(ch) && ch is not '\n' and not '\r' and not '\t')
                return true;
        }

        return false;
    }

    public static string NormalizeForDisplay(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        return MetadataTokenNormalizer.NormalizeValue(value);
    }
}
