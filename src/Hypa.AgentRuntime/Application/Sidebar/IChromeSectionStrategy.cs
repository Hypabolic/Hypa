namespace Hypa.AgentRuntime.Application.Sidebar;

/// <summary>
/// Emits sidebar pane structure. Strategies do not stamp cells.
/// </summary>
public interface IChromeSectionStrategy
{
    string Id { get; }

    bool IsBuiltIn { get; }

    SidebarPaneSlot Slot { get; }

    SidebarPaneView Compose(
        SidebarComposeInput input,
        ResolvedSidebarSection resolved,
        bool collapsed,
        bool visible,
        int width,
        SidebarCollapseDisplay display);
}
