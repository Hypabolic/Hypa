using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.AgentRuntime.Application.Sidebar;

/// <summary>
/// Paints a plugin collection in a sidebar section slot. Per
/// <c>docs/plans/AgentRuntime/chrome-slots.md</c> section 5.5 the
/// client paints rows with the token grammar. The plugin does
/// not send glyphs, colours, or widths.
/// </summary>
public sealed class ResourceChromeSectionStrategy : IChromeSectionStrategy
{
    private readonly SidebarPluginSectionBinding _binding;

    public ResourceChromeSectionStrategy(SidebarPluginSectionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        _binding = binding;
    }

    public string Id => _binding.Id;

    public bool IsBuiltIn => false;

    public SidebarPaneSlot Slot => SidebarPaneSlot.Resource;

    public SidebarPaneView Compose(
        SidebarComposeInput input,
        ResolvedSidebarSection resolved,
        bool collapsed,
        bool visible,
        int width,
        SidebarCollapseDisplay display)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(resolved);
        if (!SidebarPluginSectionRegistry.IsPluginLinked(_binding, input.LinkedPluginIds))
        {
            return HiddenPane(collapsed, visible);
        }

        var resource = SidebarPluginSectionRegistry.FindResource(input, _binding.ResourceId);
        var rows = new List<SidebarPaintedRow>();
        if (!collapsed)
        {
            var items = SortItems(resource?.Items ?? []);
            var index = 1;
            var compact = display is SidebarCollapseDisplay.Compact;
            foreach (var item in items)
            {
                var values = ValuesForItem(item, input.Ui.StatusIndicators);
                var rowId = RowId(item.Id);
                var compactLabel = SidebarSectionComposer.CompactLabel(
                    index,
                    SidebarTokenGrammar.StateIcon(item.Status, input.Ui.StatusIndicators));
                SidebarSectionComposer.AppendTokenRows(
                    rows,
                    resolved.Config.Rows,
                    values,
                    width,
                    compact,
                    resolved.Config.RowGap,
                    new SidebarPaintedRow
                    {
                        Id = rowId,
                        Kind = SidebarRowKind.CollectionItem,
                        Label = compactLabel,
                        CompactLabel = compactLabel,
                        State = SidebarTokenGrammar.CanonicalState(item.Status),
                        PaneSlot = Slot,
                        ScrollId = Id,
                        CollectionActivation = BuildActivation(item, resource?.Revision ?? 0),
                    });
                index++;
            }
        }

        return new SidebarPaneView
        {
            Id = resolved.Id,
            Slot = Slot,
            Header = " " + resolved.Title,
            Title = resolved.Title,
            Order = resolved.Order,
            Visible = visible,
            Collapsed = collapsed,
            EmptyText = SidebarPluginSectionRegistry.DefaultEmptyText,
            ScrollId = Id,
            Rows = rows,
        };
    }

    private SidebarPaneView HiddenPane(bool collapsed, bool visible) =>
        new()
        {
            Id = _binding.Id,
            Slot = Slot,
            Header = "",
            Title = "",
            Order = _binding.Order,
            Visible = false,
            Collapsed = collapsed,
            Rows = [],
        };

    private string RowId(string itemId) => Id + ":" + itemId;

    private IReadOnlyList<SidebarPluginCollectionItemView> SortItems(
        IReadOnlyList<SidebarPluginCollectionItemView> items)
    {
        if (items.Count <= 1)
            return items;
        if (_binding.Sort is SidebarSectionSort.Attention)
        {
            return items
                .OrderByDescending(i => i.Attention)
                .ThenBy(i => i.Sequence ?? long.MaxValue)
                .ThenBy(i => i.Id, StringComparer.Ordinal)
                .ToArray();
        }

        return items
            .OrderBy(i => i.Sequence ?? long.MaxValue)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private SidebarCollectionActivation BuildActivation(
        SidebarPluginCollectionItemView item,
        long revision) =>
        new()
        {
            SectionId = Id,
            PluginId = PluginResourceId.TryParse(_binding.ResourceId, out var parsed)
                ? parsed.PluginId
                : string.Empty,
            ItemId = item.Id,
            ResourceId = _binding.ResourceId,
            Revision = revision,
            ActionId = item.ActionId,
            Target = item.Target,
        };

    private static SidebarTokenValues ValuesForItem(
        SidebarPluginCollectionItemView item,
        StatusIndicatorStyle indicators) =>
        new()
        {
            StateIcon = SidebarTokenGrammar.StateIcon(item.Status, indicators),
            StateText = SidebarTokenGrammar.StateText(item.Status),
            Custom = item.Tokens,
        };
}
