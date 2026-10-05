namespace Hypa.AgentRuntime.Application.Sidebar;

/// <summary>
/// What the sidebar update row offers. Today the only state is a mux that
/// must restart to run the installed Hypa. An auto-updater adds its states
/// (update available, installing) here and reuses the same row and restart.
/// </summary>
public enum SidebarUpdateNoticeKind
{
    RestartRequired,
}

/// <param name="Kind">Which action a click on the row runs.</param>
/// <param name="Label">Row text, for example <c>↻ restart to update</c>.</param>
/// <param name="Prompt">Confirm prompt shown before the action runs.</param>
public sealed record SidebarUpdateNotice(
    SidebarUpdateNoticeKind Kind,
    string Label,
    string Prompt);

/// <summary>
/// Built-in section above the others. It is hidden unless the client has an
/// <see cref="SidebarComposeInput.UpdateNotice"/> to show.
/// </summary>
public sealed class UpdateChromeSectionStrategy : IChromeSectionStrategy
{
    public const string SectionId = "update";
    public const string RowId = "update:notice";
    public const string NoticeTokenId = "notice";
    public const string CompactGlyph = "↻";

    public string Id => SectionId;

    public bool IsBuiltIn => true;

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
        var notice = input.UpdateNotice;
        IReadOnlyList<SidebarPaintedRow> rows = notice is null
            ? []
            :
            [
                new SidebarPaintedRow
                {
                    Id = RowId,
                    Kind = SidebarRowKind.Notice,
                    Label = notice.Label,
                    CompactLabel = CompactGlyph,
                    PaneSlot = Slot,
                    ScrollId = Id,
                    Tokens = [new SidebarRowToken(NoticeTokenId, notice.Label)],
                },
            ];

        return new SidebarPaneView
        {
            Id = SectionId,
            Slot = Slot,
            Header = " Update",
            Title = "Update",
            Order = resolved.Order,
            Visible = visible && notice is not null,
            // A notice the user collapsed would hide the only way to act on it.
            Collapsed = false,
            ScrollId = Id,
            Rows = rows,
        };
    }
}
