using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Pure BSP tree algebra. No IO. Control plane calls these under the AppState lock.
/// </summary>
public static class LayoutTreeOperations
{
    public static LayoutPaneNode PaneLeaf(
        PaneId? paneId,
        string? label = null,
        string? cwd = null,
        IReadOnlyList<string>? command = null) =>
        new()
        {
            PaneId = paneId,
            Label = label ?? string.Empty,
            Cwd = cwd ?? string.Empty,
            Command = command ?? [],
        };

    public static LayoutPaneNode FromPane(PaneState pane) =>
        PaneLeaf(pane.Id, pane.Label, pane.Cwd, Argv(pane.Command, pane.Args));

    /// <summary>Full argv for a layout leaf: argv0 plus optional args tail.</summary>
    public static IReadOnlyList<string> Argv(string? command, IReadOnlyList<string>? args)
    {
        var argv0 = command ?? string.Empty;
        if (args is null || args.Count == 0)
            return string.IsNullOrEmpty(argv0) ? [] : [argv0];
        if (string.IsNullOrEmpty(argv0))
            return args;
        var list = new string[1 + args.Count];
        list[0] = argv0;
        for (var i = 0; i < args.Count; i++)
            list[i + 1] = args[i];
        return list;
    }

    /// <summary>
    /// Old rows with null layout: one pane → one leaf. Zero panes → null.
    /// Multiple panes become a right-split chain ordered by pane_id.
    /// </summary>
    public static LayoutNode? SynthesizeFromPaneIds(IReadOnlyList<PaneState> panes)
    {
        if (panes.Count == 0)
            return null;
        var ordered = panes.OrderBy(p => p.Id.Value, StringComparer.Ordinal).ToList();
        LayoutNode root = FromPane(ordered[0]);
        for (var i = 1; i < ordered.Count; i++)
        {
            root = new LayoutSplitNode
            {
                Direction = LayoutNode.DirectionRight,
                Ratio = 0.5,
                First = root,
                Second = FromPane(ordered[i]),
            };
        }

        return root;
    }

    public static LayoutNode Split(
        LayoutNode root,
        PaneId target,
        LayoutPaneNode incoming,
        string direction,
        double ratio)
    {
        if (!LayoutNode.IsSplitDirection(direction))
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "direction must be right or down");
        if (!LayoutNode.IsValidRatio(ratio))
            throw new ArgumentOutOfRangeException(nameof(ratio), ratio, "ratio must be in (0,1)");

        var replaced = ReplacePane(root, target, current => new LayoutSplitNode
        {
            Direction = direction,
            Ratio = ratio,
            First = current,
            Second = incoming,
        });
        if (replaced is null)
            throw new InvalidOperationException($"Pane {target} not found in layout.");
        return replaced;
    }

    public static (LayoutNode? Root, LayoutPaneNode? Removed) RemovePane(LayoutNode? root, PaneId id)
    {
        if (root is null)
            return (null, null);
        if (root is LayoutPaneNode pane)
            return pane.PaneId?.Value == id.Value ? (null, pane) : (root, null);
        if (root is not LayoutSplitNode split)
            return (root, null);

        var (first, removedFirst) = RemovePane(split.First, id);
        if (removedFirst is not null)
            return (first ?? split.Second, removedFirst);
        var (second, removedSecond) = RemovePane(split.Second, id);
        if (removedSecond is not null)
            return (second ?? split.First, removedSecond);
        return (root, null);
    }

    /// <summary>
    // / Drop every listed id from the tree.
    /// owns pane ids as BSP leaves; hidden occupants must not remain as leaves.
    /// </summary>
    public static LayoutNode? WithoutPanes(LayoutNode? root, IEnumerable<PaneId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var next = root;
        foreach (var id in ids)
            (next, _) = RemovePane(next, id);
        return next;
    }

    public static LayoutNode SetSplitRatio(LayoutNode root, IReadOnlyList<int> path, double ratio)
    {
        if (!LayoutNode.IsValidRatio(ratio))
            throw new ArgumentOutOfRangeException(nameof(ratio), ratio, "ratio must be in (0,1)");
        return SetSplitRatioAt(root, path, 0, ratio);
    }

    public static LayoutPaneNode? FocusDirection(LayoutNode root, PaneId from, string direction)
    {
        if (!LayoutNode.IsFocusDirection(direction))
            return null;
        var occupancy = Neighbor(root, from, direction);
        if (occupancy is not null)
            return occupancy;

        var leaves = LeavesInOrder(root);
        var index = leaves.FindIndex(p => p.PaneId?.Value == from.Value);
        if (index < 0)
            return null;

        // Fallback: walk the in-order leaf list for a simple neighbor.
        return direction switch
        {
            LayoutNode.DirectionRight or LayoutNode.DirectionDown =>
                index + 1 < leaves.Count ? leaves[index + 1] : null,
            LayoutNode.DirectionLeft or LayoutNode.DirectionUp =>
                index > 0 ? leaves[index - 1] : null,
            _ => null,
        };
    }

    /// <summary>
    /// Occupancy neighbor on <paramref name="direction"/> (BSP, no in-order fallback).
    /// Does not mutate focus.
    /// </summary>
    public static LayoutPaneNode? Neighbor(LayoutNode root, PaneId from, string direction)
    {
        if (!LayoutNode.IsFocusDirection(direction))
            return null;
        var neighbors = CollectNeighbors(root, from, direction);
        return neighbors.Count > 0 ? neighbors[0] : null;
    }

    /// <summary>
    /// BSP occupancy edges. A side is true when <see cref="Neighbor"/> is empty.
    /// </summary>
    public static (bool Left, bool Right, bool Up, bool Down) Edges(LayoutNode root, PaneId from) =>
        (
            Neighbor(root, from, LayoutNode.DirectionLeft) is null,
            Neighbor(root, from, LayoutNode.DirectionRight) is null,
            Neighbor(root, from, LayoutNode.DirectionUp) is null,
            Neighbor(root, from, LayoutNode.DirectionDown) is null
        );

    public static IReadOnlyList<LayoutPaneNode> Leaves(LayoutNode? root) =>
        root is null ? [] : LeavesInOrder(root);

    public static LayoutNode StripIds(LayoutNode node) => node switch
    {
        LayoutPaneNode pane => pane with { PaneId = null },
        LayoutSplitNode split => split with
        {
            First = StripIds(split.First),
            Second = StripIds(split.Second),
        },
        _ => node,
    };

    public static string CanonicalExport(LayoutNode? root, bool includePaneId = false) =>
        root?.ToCanonicalJson(includePaneId) ?? "null";

    public static LayoutNode RewritePaneIds(LayoutNode node, IReadOnlyDictionary<string, PaneId> map) =>
        node switch
        {
            LayoutPaneNode pane when pane.PaneId is { } id && map.TryGetValue(id.Value, out var next) =>
                pane with { PaneId = next },
            LayoutPaneNode pane => pane,
            LayoutSplitNode split => split with
            {
                First = RewritePaneIds(split.First, map),
                Second = RewritePaneIds(split.Second, map),
            },
            _ => node,
        };

    public static LayoutNode AttachPane(LayoutNode? root, LayoutPaneNode leaf)
    {
        if (root is null)
            return leaf;
        return new LayoutSplitNode
        {
            Direction = LayoutNode.DirectionRight,
            Ratio = 0.5,
            First = root,
            Second = leaf,
        };
    }

    /// <summary>
    // / Parent split of <paramref name="id"/>.
    /// <c>remove_pane</c> replaces that split with the sibling.
    /// </summary>
    public static LayoutInsertion? DescribeInsertion(LayoutNode? root, PaneId id)
    {
        if (root is not LayoutSplitNode split)
            return null;

        if (split.First is LayoutPaneNode first && first.PaneId?.Value == id.Value)
        {
            return new LayoutInsertion
            {
                Direction = split.Direction,
                Ratio = split.Ratio,
                IncomingIsSecond = false,
                SiblingPaneIds = LeafIds(split.Second),
            };
        }

        if (split.Second is LayoutPaneNode second && second.PaneId?.Value == id.Value)
        {
            return new LayoutInsertion
            {
                Direction = split.Direction,
                Ratio = split.Ratio,
                IncomingIsSecond = true,
                SiblingPaneIds = LeafIds(split.First),
            };
        }

        return DescribeInsertion(split.First, id) ?? DescribeInsertion(split.Second, id);
    }

    /// <summary>
    /// Inverse of hide: wrap the sibling with the captured side, direction,
    // / and ratio.
    /// pane second. Restore of a first leaf keeps first.
    /// </summary>
    public static LayoutNode Reinsert(LayoutNode? current, LayoutPaneNode incoming, LayoutInsertion insertion)
    {
        ArgumentNullException.ThrowIfNull(insertion);
        if (current is null)
            return incoming;

        var siblingIds = insertion.SiblingPaneIds
            .Select(id => id.Value)
            .ToHashSet(StringComparer.Ordinal);
        if (siblingIds.Count > 0)
        {
            var covering = FindCovering(current, siblingIds);
            if (covering is not null)
                return ReplaceCovering(current, siblingIds, sibling => Wrap(sibling, incoming, insertion));

            var anchor = insertion.SiblingPaneIds.FirstOrDefault(id => ContainsPane(current, id));
            if (anchor.Value is not null)
            {
                var replaced = ReplacePane(
                    current,
                    anchor,
                    sibling => Wrap(sibling, incoming, insertion));
                if (replaced is not null)
                    return replaced;
            }
        }

        return Wrap(current, incoming, insertion);
    }

    private static IReadOnlyList<PaneId> LeafIds(LayoutNode? root)
    {
        var ids = new List<PaneId>();
        foreach (var leaf in Leaves(root))
        {
            if (leaf.PaneId is { Value: not null } id)
                ids.Add(id);
        }

        return ids;
    }

    private static HashSet<string> LeafIdSet(LayoutNode? root)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var leaf in Leaves(root))
        {
            if (leaf.PaneId is { Value: not null } id)
                ids.Add(id.Value);
        }

        return ids;
    }

    private static LayoutNode? FindCovering(LayoutNode? node, HashSet<string> ids)
    {
        if (node is null)
            return null;
        var leaves = LeafIdSet(node);
        if (ids.SetEquals(leaves))
            return node;
        if (node is LayoutSplitNode split)
            return FindCovering(split.First, ids) ?? FindCovering(split.Second, ids);
        return null;
    }

    private static LayoutNode ReplaceCovering(
        LayoutNode root,
        HashSet<string> ids,
        Func<LayoutNode, LayoutNode> replace)
    {
        if (ids.SetEquals(LeafIdSet(root)))
            return replace(root);
        if (root is not LayoutSplitNode split)
            return root;
        if (FindCovering(split.First, ids) is not null)
            return split with { First = ReplaceCovering(split.First, ids, replace) };
        if (FindCovering(split.Second, ids) is not null)
            return split with { Second = ReplaceCovering(split.Second, ids, replace) };
        return root;
    }

    private static LayoutNode Wrap(LayoutNode sibling, LayoutPaneNode incoming, LayoutInsertion insertion) =>
        insertion.IncomingIsSecond
            ? new LayoutSplitNode
            {
                Direction = insertion.Direction,
                Ratio = insertion.Ratio,
                First = sibling,
                Second = incoming,
            }
            : new LayoutSplitNode
            {
                Direction = insertion.Direction,
                Ratio = insertion.Ratio,
                First = incoming,
                Second = sibling,
            };

    /// <summary>
    /// Rewrite the matching leaf label. Returns <paramref name="root"/> when the pane is absent.
    /// </summary>
    public static LayoutNode? SetPaneLabel(LayoutNode? root, PaneId id, string label)
    {
        if (root is null)
            return null;
        return ReplacePane(root, id, current => current with { Label = label }) ?? root;
    }

    public static bool ContainsPane(LayoutNode? root, PaneId id)
    {
        if (root is LayoutPaneNode pane)
            return pane.PaneId?.Value == id.Value;
        if (root is LayoutSplitNode split)
            return ContainsPane(split.First, id) || ContainsPane(split.Second, id);
        return false;
    }

    public static int LeafCount(LayoutNode? root) => Leaves(root).Count;

    /// <summary>
    /// Exchange two leaf records in place. Split directions and ratios stay.
    /// </summary>
    public static LayoutNode SwapLeaves(LayoutNode root, PaneId a, PaneId b)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (a.Value == b.Value)
            return root;
        LayoutPaneNode? leafA = null;
        LayoutPaneNode? leafB = null;
        foreach (var leaf in LeavesInOrder(root))
        {
            if (leaf.PaneId?.Value == a.Value)
                leafA = leaf;
            else if (leaf.PaneId?.Value == b.Value)
                leafB = leaf;
        }

        if (leafA is null)
            throw new InvalidOperationException($"Pane {a} not found in layout.");
        if (leafB is null)
            throw new InvalidOperationException($"Pane {b} not found in layout.");
        return SwapLeavesAt(root, a, b, leafA, leafB);
    }

    private static LayoutNode SwapLeavesAt(
        LayoutNode node,
        PaneId a,
        PaneId b,
        LayoutPaneNode leafA,
        LayoutPaneNode leafB)
    {
        if (node is LayoutPaneNode pane)
        {
            if (pane.PaneId?.Value == a.Value)
                return leafB;
            if (pane.PaneId?.Value == b.Value)
                return leafA;
            return pane;
        }

        if (node is LayoutSplitNode split)
        {
            return split with
            {
                First = SwapLeavesAt(split.First, a, b, leafA, leafB),
                Second = SwapLeavesAt(split.Second, a, b, leafA, leafB),
            };
        }

        return node;
    }

    public static LayoutNodeDto? ToDto(LayoutNode? node, bool includePaneId)
    {
        return node switch
        {
            LayoutPaneNode pane => new LayoutNodeDto
            {
                Type = LayoutNode.TypePane,
                PaneId = includePaneId ? pane.PaneId?.Value : null,
                Label = string.IsNullOrEmpty(pane.Label) ? null : pane.Label,
                Cwd = string.IsNullOrEmpty(pane.Cwd) ? null : pane.Cwd,
                Command = pane.Command.Count == 0 ? null : pane.Command,
            },
            LayoutSplitNode split => new LayoutNodeDto
            {
                Type = LayoutNode.TypeSplit,
                Direction = split.Direction,
                Ratio = split.Ratio,
                First = ToDto(split.First, includePaneId),
                Second = ToDto(split.Second, includePaneId),
            },
            _ => null,
        };
    }

    public static LayoutNode? FromDto(LayoutNodeDto? dto, bool keepPaneId)
    {
        if (dto is null || string.IsNullOrWhiteSpace(dto.Type))
            return null;
        if (dto.Type == LayoutNode.TypeSplit)
        {
            var first = FromDto(dto.First, keepPaneId);
            var second = FromDto(dto.Second, keepPaneId);
            if (first is null || second is null)
                return null;
            var direction = dto.Direction ?? LayoutNode.DirectionRight;
            var ratio = dto.Ratio ?? 0.5;
            if (!LayoutNode.IsSplitDirection(direction) || !LayoutNode.IsValidRatio(ratio))
                return null;
            return new LayoutSplitNode
            {
                Direction = direction,
                Ratio = ratio,
                First = first,
                Second = second,
            };
        }

        if (dto.Type != LayoutNode.TypePane)
            return null;
        return new LayoutPaneNode
        {
            PaneId = keepPaneId && !string.IsNullOrWhiteSpace(dto.PaneId)
                ? new PaneId(dto.PaneId)
                : null,
            Label = dto.Label ?? string.Empty,
            Cwd = dto.Cwd ?? string.Empty,
            Command = dto.Command ?? [],
        };
    }

    private static LayoutNode SetSplitRatioAt(LayoutNode node, IReadOnlyList<int> path, int index, double ratio)
    {
        if (node is not LayoutSplitNode split)
            throw new InvalidOperationException("path does not address a split node");
        if (index >= path.Count)
            return split with { Ratio = ratio };

        var step = path[index];
        if (step == 0)
            return split with { First = SetSplitRatioAt(split.First, path, index + 1, ratio) };
        if (step == 1)
            return split with { Second = SetSplitRatioAt(split.Second, path, index + 1, ratio) };
        throw new InvalidOperationException("path step must be 0 (first) or 1 (second)");
    }

    private static LayoutNode? ReplacePane(LayoutNode node, PaneId target, Func<LayoutPaneNode, LayoutNode> replace)
    {
        if (node is LayoutPaneNode pane)
            return pane.PaneId?.Value == target.Value ? replace(pane) : null;
        if (node is not LayoutSplitNode split)
            return null;
        var first = ReplacePane(split.First, target, replace);
        if (first is not null)
            return split with { First = first };
        var second = ReplacePane(split.Second, target, replace);
        return second is null ? null : split with { Second = second };
    }

    private static List<LayoutPaneNode> LeavesInOrder(LayoutNode node)
    {
        var list = new List<LayoutPaneNode>();
        Walk(node, list);
        return list;
    }

    private static void Walk(LayoutNode node, List<LayoutPaneNode> list)
    {
        switch (node)
        {
            case LayoutPaneNode pane:
                list.Add(pane);
                break;
            case LayoutSplitNode split:
                Walk(split.First, list);
                Walk(split.Second, list);
                break;
        }
    }

    private static List<LayoutPaneNode> CollectNeighbors(LayoutNode node, PaneId from, string direction)
    {
        var found = new List<LayoutPaneNode>();
        Collect(node, from, direction, found);
        return found;
    }

    private static bool Collect(LayoutNode node, PaneId from, string direction, List<LayoutPaneNode> found)
    {
        if (node is LayoutPaneNode pane)
            return pane.PaneId?.Value == from.Value;
        if (node is not LayoutSplitNode split)
            return false;

        var inFirst = Collect(split.First, from, direction, found);
        var inSecond = Collect(split.Second, from, direction, found);
        if (inFirst && IsTowardSecond(split.Direction, direction))
            found.AddRange(LeavesInOrder(split.Second));
        if (inSecond && IsTowardFirst(split.Direction, direction))
            found.AddRange(LeavesInOrder(split.First));
        return inFirst || inSecond;
    }

    private static bool IsTowardSecond(string splitDirection, string walk) =>
        (splitDirection == LayoutNode.DirectionRight && walk == LayoutNode.DirectionRight)
        || (splitDirection == LayoutNode.DirectionDown && walk == LayoutNode.DirectionDown);

    private static bool IsTowardFirst(string splitDirection, string walk) =>
        (splitDirection == LayoutNode.DirectionRight && walk == LayoutNode.DirectionLeft)
        || (splitDirection == LayoutNode.DirectionDown && walk == LayoutNode.DirectionUp);
}
