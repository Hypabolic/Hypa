using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Sidebar;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class AgentsChromeSectionStrategyTests
{
    [Fact]
    public void Compose_ExcludesOrdinaryShellAndKeepsUnknownKinds()
    {
        var input = new SidebarComposeInput
        {
            Ui = AttachUiConfig.Default,
            Expanded = true,
            MouseCapture = true,
            Workspaces =
            [
                new SidebarWorkspaceItem { Id = "ws", Label = "main", Order = 0 },
            ],
            Tabs =
            [
                new SidebarTabItem { Id = "tab", WorkspaceId = "ws", Label = "one", Ordinal = 0 },
            ],
            Panes =
            [
                Pane("shell-1", agent: null),
                Pane("shell-2", agent: "shell"),
                Pane("claude-1", agent: "claude", state: SidebarTokenGrammar.Working),
                Pane("custom-1", agent: "my-agent", state: SidebarTokenGrammar.Unknown),
            ],
            FocusedPaneId = "claude-1",
        };
        var resolved = new ResolvedSidebarSection
        {
            Id = SidebarTokenGrammar.AgentsId,
            Title = "Agents",
            Order = 0,
            Collapsed = false,
            Config = AttachSidebarSectionConfig.AgentsDefault,
        };
        var view = new AgentsChromeSectionStrategy().Compose(
            input,
            resolved,
            collapsed: false,
            visible: true,
            width: 26,
            SidebarCollapseDisplay.Expanded);

        Assert.Equal(["claude-1", "custom-1"], view.Rows.Select(r => r.Id).Distinct().ToArray());
        Assert.Equal("claude-1", view.SelectedId);
        Assert.Contains(view.Rows, r => r.Selected);
        Assert.Equal("grouped", view.Actions.Single().Label);
        Assert.Equal(" agents", view.Header);
        Assert.DoesNotContain(view.Rows, r => r.Id.StartsWith("shell", StringComparison.Ordinal));
        Assert.Contains(view.Rows, r => r.Selected && r.Id == "claude-1");
        Assert.DoesNotContain(view.Rows, r => r.Navigated);
    }

    [Fact]
    public void SortAgents_PriorityOrdersBlockedBeforeIdle()
    {
        var panes = new[]
        {
            Pane("idle", agent: "claude", state: SidebarTokenGrammar.Idle),
            Pane("blocked", agent: "codex", state: SidebarTokenGrammar.Blocked),
        };
        var workspaces = new[] { new SidebarWorkspaceItem { Id = "ws", Label = "main" } };
        var tabs = new[] { new SidebarTabItem { Id = "tab", WorkspaceId = "ws", Label = "one" } };
        var ordered = SidebarTokenGrammar.SortAgents(panes, workspaces, tabs, AgentPanelSort.Priority);
        Assert.Equal(["blocked", "idle"], ordered.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void Compose_OrdinaryEmptyAgents_LeavesEmptyTextBlank()
    {
        var input = new SidebarComposeInput
        {
            Ui = AttachUiConfig.Default,
            Expanded = true,
            MouseCapture = true,
            Workspaces = [new SidebarWorkspaceItem { Id = "ws", Label = "main", Order = 0 }],
            Tabs = [new SidebarTabItem { Id = "tab", WorkspaceId = "ws", Label = "one", Ordinal = 0 }],
            Panes =
            [
                Pane("shell-1", agent: null),
                Pane("shell-2", agent: "shell"),
            ],
        };
        var resolved = new ResolvedSidebarSection
        {
            Id = SidebarTokenGrammar.AgentsId,
            Title = "Agents",
            Order = 0,
            Collapsed = false,
            Config = AttachSidebarSectionConfig.AgentsDefault,
        };
        var view = new AgentsChromeSectionStrategy().Compose(
            input,
            resolved,
            collapsed: false,
            visible: true,
            width: 26,
            SidebarCollapseDisplay.Expanded);
        Assert.Empty(view.Rows);
        Assert.Equal("", view.EmptyText);
    }

    [Fact]
    public void Compose_HiddenChildUsesOwnLabelNotWorkspaceTab()
    {
        var input = new SidebarComposeInput
        {
            Ui = AttachUiConfig.Default,
            Expanded = true,
            MouseCapture = true,
            Workspaces =
            [
                new SidebarWorkspaceItem { Id = "ws", Label = "Modal acceptance", Order = 0 },
            ],
            Tabs =
            [
                new SidebarTabItem { Id = "tab", WorkspaceId = "ws", Label = "1", Ordinal = 0 },
            ],
            Panes =
            [
                Pane("parent", agent: null, label: "cat"),
                new SidebarPaneItem
                {
                    Id = "child",
                    TabId = "tab",
                    WorkspaceId = "ws",
                    Label = "reviewer",
                    Agent = "shell",
                    Hidden = true,
                    Placement = PanePlacementWire.Hidden,
                    ParentPaneId = "parent",
                    State = SidebarTokenGrammar.Idle,
                },
            ],
            FocusedPaneId = "parent",
        };
        var resolved = new ResolvedSidebarSection
        {
            Id = SidebarTokenGrammar.AgentsId,
            Title = "Agents",
            Order = 0,
            Collapsed = false,
            Config = AttachSidebarSectionConfig.AgentsDefault,
        };
        var view = new AgentsChromeSectionStrategy().Compose(
            input,
            resolved,
            collapsed: false,
            visible: true,
            width: 26,
            SidebarCollapseDisplay.Expanded);

        var childRows = view.Rows.Where(r => r.Id == "child").ToArray();
        Assert.NotEmpty(childRows);
        Assert.Contains(childRows, r => r.Label.Contains("reviewer", StringComparison.Ordinal));
        Assert.DoesNotContain(childRows, r => r.Label.Contains("Modal acceptance", StringComparison.Ordinal));
        Assert.Equal(1, childRows.Count(r => r.Label.Length > 0));
    }

    [Fact]
    public void Compose_HiddenBlockedChildKeepsLabelKindAndAttention()
    {
        var input = HiddenFamilyInput(
            childId: "child-codex",
            childLabel: "reviewer",
            childAgent: "codex",
            childState: SidebarTokenGrammar.Blocked);
        var view = ComposeAgents(input, width: 32);
        var child = Assert.Single(view.Rows, r => r.Id == "child-codex");
        Assert.StartsWith("reviewer", child.Label, StringComparison.Ordinal);
        Assert.Contains("codex", child.Label, StringComparison.Ordinal);
        Assert.Contains("blocked", child.Label, StringComparison.Ordinal);
        Assert.DoesNotContain("Modal acceptance", child.Label, StringComparison.Ordinal);
        Assert.Equal(["state_icon", "pane", "agent", "state_text"], child.Tokens.Select(t => t.Id).ToArray());
        Assert.Equal("reviewer", child.Tokens.Single(t => t.Id == "pane").Text);
        Assert.Equal("codex", child.Tokens.Single(t => t.Id == "agent").Text);
        Assert.Equal("blocked", child.Tokens.Single(t => t.Id == "state_text").Text);

        var painted = PaintRow(child, width: 32);
        Assert.Contains("reviewer", painted, StringComparison.Ordinal);
        Assert.Contains("codex", painted, StringComparison.Ordinal);
        Assert.Contains("blocked", painted, StringComparison.Ordinal);
        Assert.DoesNotContain("Modal acceptance", painted, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_HiddenClaudeChildPaintsThroughHostFrame()
    {
        var input = HiddenFamilyInput(
            childId: "child-claude",
            childLabel: "reviewer",
            childAgent: "claude",
            childState: SidebarTokenGrammar.Blocked);
        var hostText = StampHiddenChildHost(input, "child-claude", width: 32);
        Assert.Contains("reviewer", hostText, StringComparison.Ordinal);
        Assert.Contains("claude", hostText, StringComparison.Ordinal);
        Assert.Contains("block", hostText, StringComparison.Ordinal);
        Assert.DoesNotContain("Modal acceptance", hostText, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_HiddenShellFallbackKeepsLabelFirst()
    {
        var input = HiddenFamilyInput(
            childId: "child-shell",
            childLabel: "notes",
            childAgent: "shell",
            childState: SidebarTokenGrammar.Idle);
        var view = ComposeAgents(input, width: 26);
        var child = Assert.Single(view.Rows, r => r.Id == "child-shell");
        Assert.StartsWith("notes", child.Label, StringComparison.Ordinal);
        Assert.DoesNotContain("shell", child.Label, StringComparison.Ordinal);
        Assert.DoesNotContain("Modal acceptance", child.Label, StringComparison.Ordinal);
        Assert.DoesNotContain(child.Tokens, t => t.Id == "agent");
        Assert.Equal("notes", child.Tokens.Single(t => t.Id == "pane").Text);
        Assert.Contains("idle", child.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_HiddenChildNarrowWidthKeepsLabelFirst()
    {
        var input = HiddenFamilyInput(
            childId: "child-narrow",
            childLabel: "reviewer",
            childAgent: "codex",
            childState: SidebarTokenGrammar.Blocked);
        var view = ComposeAgents(input, width: 10);
        var child = Assert.Single(view.Rows, r => r.Id == "child-narrow");
        Assert.StartsWith("rev", child.Label, StringComparison.Ordinal);
        Assert.DoesNotContain("Modal acceptance", child.Label, StringComparison.Ordinal);
        Assert.Equal("revi", HiddenPaneLabelFit.Fit("reviewer", "codex", "blocked", 4));
        var painted = PaintRow(child, width: 10);
        Assert.Contains("rev", painted, StringComparison.Ordinal);
        Assert.DoesNotContain("Modal acceptance", painted, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_CustomVisibleAgentRowsStayOnTemplate()
    {
        var input = new SidebarComposeInput
        {
            Ui = AttachUiConfig.Default with
            {
                Sidebar = AttachUiConfig.Default.Sidebar with
                {
                    Agents = AttachSidebarSectionConfig.AgentsDefault with
                    {
                        RowsByAgent = new Dictionary<string, IReadOnlyList<IReadOnlyList<SidebarTokenSpec>>>(StringComparer.Ordinal)
                        {
                            ["claude"] = [["agent"], ["terminal_title"]],
                        },
                    },
                },
            },
            Expanded = true,
            MouseCapture = true,
            Workspaces = [new SidebarWorkspaceItem { Id = "ws", Label = "Modal acceptance", Order = 0 }],
            Tabs = [new SidebarTabItem { Id = "tab", WorkspaceId = "ws", Label = "1", Ordinal = 0 }],
            Panes =
            [
                Pane("claude-1", agent: "claude", state: SidebarTokenGrammar.Working, label: "visible-claude"),
            ],
            FocusedPaneId = "claude-1",
        };
        var view = ComposeAgents(input, width: 26, config: input.Ui.Sidebar.Agents);
        var claudeRows = view.Rows.Where(r => r.Id == "claude-1").ToArray();
        Assert.True(claudeRows.Length >= 2);
        Assert.Equal("claude", claudeRows[0].Label);
        Assert.Equal("visible-claude", claudeRows[1].Label);
        Assert.Contains(claudeRows.SelectMany(r => r.Tokens), t => t.Id == "agent" && t.Text == "claude");
        Assert.DoesNotContain(claudeRows.SelectMany(r => r.Tokens), t => t.Id == "workspace");
    }

    [Fact]
    public void Compose_CollapsedParentKeepsChildBlockedAttention()
    {
        var input = HiddenFamilyInput(
            childId: "child-block",
            childLabel: "reviewer",
            childAgent: "codex",
            childState: SidebarTokenGrammar.Blocked) with
        {
            CollapsedTreeIds = new HashSet<string>(StringComparer.Ordinal) { "parent" },
        };
        var view = ComposeAgents(input, width: 26);
        var parentRows = view.Rows.Where(r => r.Id == "parent").ToArray();
        Assert.NotEmpty(parentRows);
        Assert.All(parentRows, row => Assert.Equal(SidebarTokenGrammar.Blocked, row.State));
        Assert.DoesNotContain(view.Rows, r => r.Id == "child-block");
        Assert.Contains(parentRows.SelectMany(r => r.Tokens), t => t.Id == "state_icon");
    }

    [Fact]
    public void IsOrdinaryShellKind_DoesNotHideUnknownRealAgents()
    {
        Assert.True(SidebarTokenGrammar.IsOrdinaryShellKind(null));
        Assert.True(SidebarTokenGrammar.IsOrdinaryShellKind(""));
        Assert.True(SidebarTokenGrammar.IsOrdinaryShellKind("shell"));
        Assert.False(SidebarTokenGrammar.IsOrdinaryShellKind("unknown-agent"));
        Assert.False(SidebarTokenGrammar.IsOrdinaryShellKind("claude"));
    }

    private static SidebarPaneItem Pane(
        string id,
        string? agent,
        string state = SidebarTokenGrammar.Idle,
        string? label = null) =>
        new()
        {
            Id = id,
            TabId = "tab",
            WorkspaceId = "ws",
            Label = label ?? id,
            Agent = agent,
            State = state,
        };

    private static SidebarComposeInput HiddenFamilyInput(
        string childId,
        string childLabel,
        string? childAgent,
        string childState) =>
        new()
        {
            Ui = AttachUiConfig.Default,
            Expanded = true,
            MouseCapture = true,
            Workspaces =
            [
                new SidebarWorkspaceItem { Id = "ws", Label = "Modal acceptance", Order = 0 },
            ],
            Tabs =
            [
                new SidebarTabItem { Id = "tab", WorkspaceId = "ws", Label = "1", Ordinal = 0 },
            ],
            Panes =
            [
                Pane("parent", agent: "claude", label: "cat"),
                new SidebarPaneItem
                {
                    Id = childId,
                    TabId = "tab",
                    WorkspaceId = "ws",
                    Label = childLabel,
                    Agent = childAgent,
                    Hidden = true,
                    Placement = PanePlacementWire.Hidden,
                    ParentPaneId = "parent",
                    State = childState,
                },
            ],
            FocusedPaneId = "parent",
        };

    private static SidebarPaneView ComposeAgents(
        SidebarComposeInput input,
        int width,
        AttachSidebarSectionConfig? config = null)
    {
        var resolved = new ResolvedSidebarSection
        {
            Id = SidebarTokenGrammar.AgentsId,
            Title = "Agents",
            Order = 0,
            Collapsed = false,
            Config = config ?? AttachSidebarSectionConfig.AgentsDefault,
        };
        return new AgentsChromeSectionStrategy().Compose(
            input,
            resolved,
            collapsed: false,
            visible: true,
            width,
            SidebarCollapseDisplay.Expanded);
    }

    private static string PaintRow(SidebarPaintedRow row, int width)
    {
        var view = new SidebarPaneView
        {
            Id = SidebarTokenGrammar.AgentsId,
            Slot = SidebarPaneSlot.Agents,
            Header = AgentsChromeSectionStrategy.HeaderText,
            Visible = true,
            Rows = [row],
        };
        var frame = new SidebarFrame
        {
            Width = width,
            Display = SidebarCollapseDisplay.Expanded,
            Panes = [view],
            Sections = [view.ToSection()],
        };
        return SidebarPainter.Paint(frame, col: 1, row: 1, cols: width, rows: 6);
    }

    private static string StampHiddenChildHost(SidebarComposeInput input, string childId, int width)
    {
        var frame = SidebarSectionComposer.Compose(
            input with { RequestedWidth = width, Expanded = true },
            ChromeSectionRegistry.MuxRelease());
        var geo = LayoutChromeGeometry.Compute(
            80,
            36,
            new LayoutNodeDto { Type = "pane", PaneId = "parent" },
            zoomed: false,
            zoomedPaneId: null,
            focusedPaneId: "parent",
            AttachUiConfig.Default,
            tabCount: 1,
            AttachClientMode.Terminal,
            sidebarOpen: true,
            sidebarWidth: width,
            sidebarFrame: frame,
            sidebarSectionSplit: 0.2f);
        var host = new HostFrame();
        host.Resize(geo.Cols, geo.Rows);
        LayoutChromePainter.Stamp(
            new HostFrameCellSink(host),
            geo,
            AttachUiConfig.Default);
        var child = Assert.Single(geo.SidebarRows, h => h.Id == childId);
        return ReadHostRow(host, child.Rect.Col, child.Rect.Row, child.Rect.Cols);
    }

    private static string ReadHostRow(HostFrame host, int col, int row, int cols)
    {
        var chars = new char[Math.Max(0, cols)];
        for (var i = 0; i < chars.Length; i++)
        {
            var text = host.CellAt(col + i, row).Text;
            chars[i] = text.Length > 0 ? text[0] : ' ';
        }

        return new string(chars);
    }
}
