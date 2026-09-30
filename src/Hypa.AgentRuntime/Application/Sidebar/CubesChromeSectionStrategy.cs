using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.AgentRuntime.Application.Sidebar;

public sealed class CubesChromeSectionStrategy : IChromeSectionStrategy
{
    public const string HeaderText = " cubes";

    public string Id => SidebarTokenGrammar.CubesId;

    public bool IsBuiltIn => true;

    public SidebarPaneSlot Slot => SidebarPaneSlot.Cubes;

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
        var rows = new List<SidebarPaintedRow>();
        if (!collapsed)
        {
            var index = 1;
            var compact = display is SidebarCollapseDisplay.Compact;
            foreach (var cube in input.Cubes)
            {
                var kind = SidebarTokenGrammar.CubeKindText(cube.Kind);
                var reachability = string.IsNullOrWhiteSpace(cube.ProviderSuffix)
                    ? SidebarTokenGrammar.CubeReachabilityText(cube.Reachability)
                    : cube.ProviderSuffix;
                var displayName = cube.ShowPlacementId ? $"{cube.Name} ({cube.Id})" : cube.Name;
                var values = new SidebarTokenValues
                {
                    Name = displayName,
                    Kind = kind,
                    Reachability = reachability,
                    WorkTitle = cube.WorkTitle ?? "",
                };
                var compactLabel = SidebarSectionComposer.CompactLabel(index, CubeCompactMark(cube.Kind));
                // active_endpoint_id. Selected-unconnected is trailing status.
                var selected = !string.IsNullOrWhiteSpace(input.ConnectedCubeId)
                    && string.Equals(cube.Id, input.ConnectedCubeId, StringComparison.Ordinal);
                var status = TrailingStatus(cube, input.FocusedCubeId, input.ConnectedCubeId);
                SidebarSectionComposer.AppendTokenRows(
                    rows,
                    resolved.Config.Rows,
                    values,
                    width,
                    compact,
                    resolved.Config.RowGap,
                    new SidebarPaintedRow
                    {
                        Id = cube.Id,
                        Kind = SidebarRowKind.Cube,
                        Label = compactLabel,
                        CompactLabel = compactLabel,
                        PlacementId = cube.Id,
                        Selected = selected,
                        TrailingStatus = status.Glyph,
                        TrailingStatusKind = status.Kind,
                        Actions = SidebarCubeRowActions.ForCube(cube, input.ContinuityEnabled),
                        PaneSlot = Slot,
                        ScrollId = Id,
                    });
                index++;
            }
        }

        IReadOnlyList<SidebarActionHit> actions = [];
        if (!collapsed && display is not SidebarCollapseDisplay.Compact)
        {
            actions =
            [
                new SidebarActionHit("add_cube", " add", SidebarActionAlign.Left, 5),
                new SidebarActionHit("share_mux", " share", SidebarActionAlign.Right, 7),
            ];
        }

        return new SidebarPaneView
        {
            Id = resolved.Id,
            Slot = Slot,
            Header = HeaderText,
            Title = resolved.Title,
            Order = resolved.Order,
            Visible = visible,
            Collapsed = collapsed,
            EmptyText = input.CubesState is SidebarCubeCatalogState.Unavailable
                ? SidebarTokenGrammar.CubesUnavailableText
                : SidebarTokenGrammar.CubesEmptyText,
            ScrollId = Id,
            Actions = actions,
            Rows = rows,
        };
    }

    private static string CubeCompactMark(SidebarCubeKind kind) => kind switch
    {
        SidebarCubeKind.Local => "L",
        SidebarCubeKind.Peer => "P",
        SidebarCubeKind.Cube => "C",
        _ => "·",
    };

    /// <summary>
    /// <c>endpoint_sidebar.rs:561-567</c>: local has no signal;
    /// others paint a right-aligned status glyph.
    /// </summary>
    internal static (string? Glyph, CubeTrailingStatusKind Kind) TrailingStatus(
        SidebarCubeItem cube,
        string? focusedCubeId,
        string? connectedCubeId)
    {
        ArgumentNullException.ThrowIfNull(cube);
        if (cube.Kind == SidebarCubeKind.Local)
            return (null, CubeTrailingStatusKind.None);

        var selected = !string.IsNullOrWhiteSpace(focusedCubeId)
            && string.Equals(cube.Id, focusedCubeId, StringComparison.Ordinal);
        var connected = !string.IsNullOrWhiteSpace(connectedCubeId)
            && string.Equals(cube.Id, connectedCubeId, StringComparison.Ordinal);
        if (cube.Reachability is SidebarCubeReachability.Unreachable
            or SidebarCubeReachability.Asleep)
        {
            return ("·", CubeTrailingStatusKind.Disabled);
        }

        if (selected && !connected)
            return ("◐", CubeTrailingStatusKind.Connecting);
        if (connected || cube.Reachability is SidebarCubeReachability.Reachable)
            return ("●", CubeTrailingStatusKind.Online);
        return ("·", CubeTrailingStatusKind.Disabled);
    }
}
