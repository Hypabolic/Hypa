using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.AgentRuntime.Application.Sidebar;

/// <summary>
/// Resolves plugin sidebar sections from attach config. Per
/// <c>docs/plans/AgentRuntime/chrome-slots.md</c> section 5.5: a
/// missing plugin hides the section. It does not fail attach config.
/// </summary>
public static class SidebarPluginSectionRegistry
{
    public const string DefaultEmptyText = "No items";

    public static IReadOnlyList<SidebarPluginSectionBinding> Bindings(AttachUiConfig ui)
    {
        ArgumentNullException.ThrowIfNull(ui);
        var list = new List<SidebarPluginSectionBinding>();
        foreach (var section in ui.Sidebar.Sections)
        {
            if (string.IsNullOrWhiteSpace(section.Id)
                || string.IsNullOrWhiteSpace(section.Resource)
                || SidebarTokenGrammar.IsBuiltInSection(section.Id))
            {
                continue;
            }

            if (!PluginResourceId.TryParse(section.Resource, out _))
                continue;

            list.Add(new SidebarPluginSectionBinding
            {
                Id = section.Id,
                ResourceId = section.Resource,
                Title = string.IsNullOrWhiteSpace(section.Title) ? section.Id : section.Title.Trim(),
                Sort = section.Sort is AttachSidebarSectionSort.Attention
                    ? SidebarSectionSort.Attention
                    : SidebarSectionSort.Order,
                Order = section.Order,
                Collapsed = section.Collapsed,
                RowGap = section.RowGap,
                Rows = section.RowsSpecified && section.Rows.Count > 0
                    ? section.Rows
                    :
                    [
                        ["state_icon", "$title"],
                    ],
            });
        }

        return list
            .OrderBy(s => s.Order)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public static ChromeSectionRegistry Build(
        AttachUiConfig ui,
        bool continuityEnabled,
        IReadOnlySet<string>? linkedPluginIds = null)
    {
        ArgumentNullException.ThrowIfNull(ui);
        var registry = continuityEnabled
            ? ChromeSectionRegistry.Core()
            : ChromeSectionRegistry.MuxRelease();
        foreach (var binding in Bindings(ui))
        {
            if (!IsPluginLinked(binding, linkedPluginIds))
                continue;
            registry.TryRegister(new ResourceChromeSectionStrategy(binding));
        }

        return registry;
    }

    public static bool IsPluginLinked(
        SidebarPluginSectionBinding binding,
        IReadOnlySet<string>? linkedPluginIds)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!PluginResourceId.TryParse(binding.ResourceId, out var id))
            return false;
        if (linkedPluginIds is null || linkedPluginIds.Count == 0)
            return false;
        return linkedPluginIds.Contains(id.PluginId);
    }

    public static SidebarPluginResourceView? FindResource(
        SidebarComposeInput input,
        string resourceId)
    {
        ArgumentNullException.ThrowIfNull(input);
        foreach (var resource in input.PluginResources)
        {
            if (string.Equals(resource.ResourceId, resourceId, StringComparison.Ordinal))
                return resource;
        }

        return null;
    }
}
