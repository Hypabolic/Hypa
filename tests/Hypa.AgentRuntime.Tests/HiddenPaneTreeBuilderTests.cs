using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class HiddenPaneTreeBuilderTests
{
    [Fact]
    public void Parse_ReadsValidatedParentOnlyFromPaneObject()
    {
        var snap = Snapshot(
            Pane("child", hidden: true, parent: "parent", agent: "shell", state: "blocked"),
            Pane("parent", hidden: false, parent: null, agent: "claude"));
        var records = HiddenPaneSnapshotMapper.Parse(snap);
        var child = records.Single(p => p.PaneId == "child");
        Assert.Equal("parent", child.ParentPaneId);
        Assert.True(child.Hidden);
        Assert.Equal(PanePlacementWire.Hidden, child.Placement);
        Assert.Equal("shell", child.AgentKind);
        Assert.Equal(SidebarTokenGrammar.Blocked, child.State);
        Assert.DoesNotContain(records, p => p.ParentPaneId == "tab");
    }

    [Fact]
    public void Parse_IgnoresSelfParentAndBlank()
    {
        var snap = Snapshot(
            Pane("loop", hidden: true, parent: "loop"),
            Pane("blank", hidden: true, parent: "  "));
        var records = HiddenPaneSnapshotMapper.Parse(snap);
        Assert.Null(records.Single(p => p.PaneId == "loop").ParentPaneId);
        Assert.Null(records.Single(p => p.PaneId == "blank").ParentPaneId);
    }

    [Fact]
    public void MissingParent_GoesToWorkspaceBackground()
    {
        var records = new[]
        {
            Rec("orphan", hidden: true, parent: "gone", workspace: "ws-a", agent: "shell"),
            Rec("agent", hidden: false, parent: null, workspace: "ws-a", agent: "claude"),
        };
        var tree = HiddenPaneTreeBuilder.Build(records, [records[1]]);
        Assert.Contains(tree.Rows, r => r.Id == HiddenPaneIds.BackgroundGroup("ws-a"));
        var child = tree.Rows.Single(r => r.PaneId == "orphan");
        Assert.Equal(HiddenPaneRowKind.Child, child.Kind);
        Assert.Equal(1, child.Depth);
        Assert.Equal(["agent", HiddenPaneIds.BackgroundGroup("ws-a"), "orphan"],
            tree.Rows.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void HiddenShell_StaysUnderValidatedParent()
    {
        var records = new[]
        {
            Rec("parent", hidden: false, parent: null, agent: "claude"),
            Rec("shell", hidden: true, parent: "parent", agent: "shell"),
        };
        var tree = HiddenPaneTreeBuilder.Build(records, [records[0]]);
        Assert.Equal(["parent", "shell"], tree.Rows.Select(r => r.Id).ToArray());
        Assert.Equal(1, tree.Rows[1].Depth);
        Assert.True(tree.Rows[1].Hidden);
    }

    [Fact]
    public void Cycle_FinishesWithoutDuplicateRows()
    {
        var records = new[]
        {
            Rec("a", hidden: true, parent: "b"),
            Rec("b", hidden: true, parent: "a"),
        };
        var tree = HiddenPaneTreeBuilder.Build(records, []);
        Assert.Equal(tree.Rows.Select(r => r.Id).Distinct().Count(), tree.Rows.Count);
        Assert.DoesNotContain(tree.Rows, r => r.ParentPaneId is not null && r.Kind is HiddenPaneRowKind.Parent);
        Assert.Contains(tree.Rows, r => r.Kind is HiddenPaneRowKind.Background);
        Assert.Contains(tree.Rows, r => r.PaneId == "a");
        Assert.Contains(tree.Rows, r => r.PaneId == "b");
    }

    [Fact]
    public void NestedGenerations_KeepOrderAndNoDupes()
    {
        var records = new[]
        {
            Rec("root", hidden: false, parent: null, agent: "claude"),
            Rec("mid", hidden: true, parent: "root", agent: "codex"),
            Rec("leaf", hidden: true, parent: "mid", agent: "shell"),
        };
        var tree = HiddenPaneTreeBuilder.Build(records, [records[0]]);
        Assert.Equal(["root", "mid", "leaf"], tree.Rows.Select(r => r.Id).ToArray());
        Assert.Equal([0, 1, 2], tree.Rows.Select(r => r.Depth).ToArray());
        Assert.Equal(3, tree.Rows.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void ReversedLexicalIds_KeepValidatedParentTopology()
    {
        var records = new[]
        {
            Rec("z-parent", hidden: false, parent: null, agent: "shell"),
            Rec("a-child", hidden: true, parent: "z-parent", agent: "shell"),
            Rec("b-grandchild", hidden: true, parent: "a-child", agent: "shell"),
        };
        var tree = HiddenPaneTreeBuilder.Build(records);
        Assert.Equal(["z-parent", "a-child", "b-grandchild"], tree.Rows.Select(r => r.Id).ToArray());
        Assert.Equal([0, 1, 2], tree.Rows.Select(r => r.Depth).ToArray());
        Assert.Equal("z-parent", tree.Rows.Single(r => r.PaneId == "a-child").ParentPaneId);
        Assert.Equal("a-child", tree.Rows.Single(r => r.PaneId == "b-grandchild").ParentPaneId);
        Assert.Equal(HiddenPaneRowKind.Parent, tree.Rows[0].Kind);
        Assert.DoesNotContain(tree.Rows, r => r.Kind is HiddenPaneRowKind.Background);
    }

    [Fact]
    public void ReorderedSnapshots_KeepTheSameNestedRows()
    {
        var first = HiddenPaneTreeBuilder.Build(
            [
                Rec("z-parent", hidden: false, parent: null, agent: "shell"),
                Rec("a-child", hidden: true, parent: "z-parent"),
                Rec("b-grandchild", hidden: true, parent: "a-child"),
            ]);
        var second = HiddenPaneTreeBuilder.Build(
            [
                Rec("b-grandchild", hidden: true, parent: "a-child"),
                Rec("a-child", hidden: true, parent: "z-parent"),
                Rec("z-parent", hidden: false, parent: null, agent: "shell"),
            ]);
        Assert.Equal(first.Rows.Select(r => (r.Id, r.Depth, r.ParentPaneId)),
            second.Rows.Select(r => (r.Id, r.Depth, r.ParentPaneId)));
        Assert.Equal([0, 1, 2], second.Rows.Select(r => r.Depth).ToArray());
    }

    [Fact]
    public void CollapsedTrueRoot_HidesNestedDescendants()
    {
        var records = new[]
        {
            Rec("z-parent", hidden: false, parent: null, agent: "shell", state: SidebarTokenGrammar.Idle),
            Rec("a-child", hidden: true, parent: "z-parent", agent: "shell", state: SidebarTokenGrammar.Idle),
            Rec("b-grandchild", hidden: true, parent: "a-child", agent: "shell", state: SidebarTokenGrammar.Blocked),
        };
        var collapsed = new HashSet<string>(StringComparer.Ordinal) { "z-parent" };
        var tree = HiddenPaneTreeBuilder.Build(records, collapsedIds: collapsed);
        var root = tree.Rows.Single(r => r.Id == "z-parent");
        Assert.True(root.Collapsed);
        Assert.Equal(0, root.Depth);
        Assert.Equal(SidebarTokenGrammar.Blocked, root.State);
        Assert.DoesNotContain(tree.Rows, r => r.PaneId == "a-child");
        Assert.DoesNotContain(tree.Rows, r => r.PaneId == "b-grandchild");
        Assert.Single(tree.Rows);
    }

    [Fact]
    public void CollapsedParent_AggregatesBlockedChild()
    {
        var records = new[]
        {
            Rec("root", hidden: false, parent: null, agent: "claude", state: SidebarTokenGrammar.Idle),
            Rec("child", hidden: true, parent: "root", agent: "shell", state: SidebarTokenGrammar.Blocked),
        };
        var collapsed = new HashSet<string>(StringComparer.Ordinal) { "root" };
        var tree = HiddenPaneTreeBuilder.Build(records, [records[0]], collapsed);
        var parent = tree.Rows.Single(r => r.Id == "root");
        Assert.True(parent.Collapsed);
        Assert.Equal(SidebarTokenGrammar.Blocked, parent.State);
        Assert.DoesNotContain(tree.Rows, r => r.PaneId == "child");
    }

    [Fact]
    public void Reorder_KeepsStablePaneIds()
    {
        var first = HiddenPaneTreeBuilder.Build(
            [Rec("p", hidden: false, parent: null, agent: "claude"), Rec("c", hidden: true, parent: "p")],
            [Rec("p", hidden: false, parent: null, agent: "claude")]);
        var second = HiddenPaneTreeBuilder.Build(
            [Rec("c", hidden: true, parent: "p"), Rec("p", hidden: false, parent: null, agent: "claude")],
            [Rec("p", hidden: false, parent: null, agent: "claude")]);
        Assert.Equal(first.Rows.Select(r => r.Id), second.Rows.Select(r => r.Id));
        Assert.Equal("c", second.Rows.Single(r => r.PaneId == "c").PaneId);
    }

    [Fact]
    public void LabelFit_ClipsWideAndCombiningByCellWidth()
    {
        var wide = HiddenPaneLabelFit.Fit("東京エージェント", "claude", "blocked", 4);
        Assert.True(SafeDisplayText.Width(wide) <= 4);
        var combining = HiddenPaneLabelFit.Fit("e\u0301e\u0301e\u0301", null, null, 2);
        Assert.True(SafeDisplayText.Width(combining) <= 2);
    }

    private static HiddenPaneRecord Rec(
        string id,
        bool hidden,
        string? parent,
        string workspace = "ws",
        string? agent = null,
        string state = SidebarTokenGrammar.Idle) =>
        new()
        {
            PaneId = id,
            TabId = "tab",
            WorkspaceId = workspace,
            Label = id,
            AgentKind = agent,
            State = state,
            Hidden = hidden,
            Placement = hidden ? PanePlacementWire.Hidden : PanePlacementWire.Tiled,
            ParentPaneId = parent,
        };

    private static JsonObject Pane(
        string id,
        bool hidden,
        string? parent,
        string? agent = null,
        string state = "idle")
    {
        var obj = new JsonObject
        {
            ["pane_id"] = id,
            ["tab_id"] = "tab",
            ["workspace_id"] = "ws",
            ["label"] = id,
            ["agent"] = agent,
            ["state"] = state,
            ["hidden"] = hidden,
            ["placement"] = hidden ? "hidden" : "tiled",
        };
        if (parent is not null)
            obj["parent_pane_id"] = parent;
        return obj;
    }

    private static JsonElement Snapshot(params JsonObject[] panes)
    {
        var arr = new JsonArray();
        foreach (var pane in panes)
            arr.Add(pane);
        return JsonDocument.Parse(new JsonObject { ["panes"] = arr }.ToJsonString()).RootElement.Clone();
    }
}
