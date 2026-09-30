using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Infrastructure.Config;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Attach.Sidebar;
using Xunit;

namespace Hypa.AgentRuntime.Tests.Sidebar;

public sealed class SidebarPluginSectionTests
{
    private const string PluginId = "atomic.workbench";
    private const string ResourceId = "plugin:atomic.workbench/queue";

    [Fact]
    public void Config_binds_resource_and_parses_section_fields()
    {
        var parsed = TomlAttachConfigBinder.Bind("""
            [[ui.sidebar.section]]
            id = "atomic"
            resource = "plugin:atomic.workbench/queue"
            title = "Atomic"
            rows = ["state_icon", "$title", "$age"]
            sort = "attention"
            collapsed = false
            order = 2
            """);
        Assert.True(parsed.IsOk, parsed.IsOk ? "" : parsed.Error.ToString());
        var section = Assert.Single(parsed.Value.Ui.Sidebar.Sections);
        Assert.Equal("atomic", section.Id);
        Assert.Equal(ResourceId, section.Resource);
        Assert.Equal("Atomic", section.Title);
        Assert.Equal(AttachSidebarSectionSort.Attention, section.Sort);
        Assert.True(section.RowsSpecified);
    }

    [Fact]
    public void Built_in_resource_and_unknown_rows_fail_closed()
    {
        var builtIn = TomlAttachConfigBinder.Bind("""
            [[ui.sidebar.section]]
            id = "agents"
            resource = "plugin:atomic.workbench/queue"
            """);
        Assert.Contains(
            builtIn.Errors,
            e => e.Code == AttachConfigError.SidebarSectionBuiltInResource);

        var missingResource = TomlAttachConfigBinder.Bind("""
            [[ui.sidebar.section]]
            id = "atomic"
            """);
        Assert.Contains(
            missingResource.Errors,
            e => e.Code == AttachConfigError.SidebarSectionPluginRequiresResource);

        var badResource = TomlAttachConfigBinder.Bind("""
            [[ui.sidebar.section]]
            id = "atomic"
            resource = "not-a-resource"
            """);
        Assert.Contains(
            badResource.Errors,
            e => e.Code == AttachConfigError.SidebarSectionResourceInvalid);

        var badRows = TomlAttachConfigBinder.Bind("""
            [[ui.sidebar.section]]
            id = "atomic"
            resource = "plugin:atomic.workbench/queue"
            rows = ["$title", "not_a_token"]
            """);
        Assert.False(badRows.IsOk);
    }

    [Fact]
    public void Missing_plugin_hides_section_without_config_error()
    {
        var ui = ConfigWithPluginSection();
        var frame = Compose(ui, linked: false);
        Assert.DoesNotContain(frame.Panes, p => p.Id == "atomic");
    }

    [Fact]
    public void Linked_plugin_paints_collection_rows_with_token_grammar()
    {
        var ui = ConfigWithPluginSection();
        var frame = Compose(
            ui,
            resources:
            [
                Resource(
                    revision: 3,
                    Item("a", "Alpha", attention: 1, tokens: new Dictionary<string, string> { ["title"] = "Alpha", ["age"] = "12m" }),
                    Item("b", "Beta", attention: 5, tokens: new Dictionary<string, string> { ["title"] = "Beta", ["age"] = "1m" })),
            ]);
        var pane = Assert.Single(frame.Panes, p => p.Id == "atomic");
        Assert.Equal(2, pane.Rows.Count);
        Assert.Equal("atomic:b", pane.Rows[0].Id);
        Assert.Contains("Beta", pane.Rows[0].Label, StringComparison.Ordinal);
        Assert.Contains("1m", pane.Rows[0].Label, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", pane.Rows[0].Label, StringComparison.Ordinal);
    }

    [Fact]
    public void Sort_by_declared_order_uses_item_seq()
    {
        var ui = ConfigWithPluginSection() with
        {
            Sidebar = ConfigWithPluginSection().Sidebar with
            {
                Sections =
                [
                    ConfigWithPluginSection().Sidebar.Sections[0] with
                    {
                        Sort = AttachSidebarSectionSort.Order,
                    },
                ],
            },
        };
        var frame = Compose(
            ui,
            resources:
            [
                Resource(
                    revision: 1,
                    Item("a", "Alpha", seq: 2),
                    Item("b", "Beta", seq: 1)),
            ]);
        var pane = Assert.Single(frame.Panes, p => p.Id == "atomic");
        Assert.Equal("atomic:b", pane.Rows[0].Id);
        Assert.Equal("atomic:a", pane.Rows[1].Id);
    }

    [Fact]
    public void Collapse_and_empty_state_work()
    {
        var collapsed = ConfigWithPluginSection() with
        {
            Sidebar = ConfigWithPluginSection().Sidebar with
            {
                Sections =
                [
                    ConfigWithPluginSection().Sidebar.Sections[0] with { Collapsed = true },
                ],
            },
        };
        var collapsedFrame = Compose(collapsed);
        var collapsedPane = Assert.Single(collapsedFrame.Panes, p => p.Id == "atomic");
        Assert.True(collapsedPane.Collapsed);
        Assert.Empty(collapsedPane.Rows);

        var emptyFrame = Compose(ConfigWithPluginSection(), resources: [Resource(revision: 1)]);
        var emptyPane = Assert.Single(emptyFrame.Panes, p => p.Id == "atomic");
        Assert.Equal(SidebarPluginSectionRegistry.DefaultEmptyText, emptyPane.EmptyText);
    }

    [Fact]
    public void Primary_activation_prefers_target_secondary_runs_action()
    {
        var activation = new SidebarCollectionActivation
        {
            SectionId = "atomic",
            PluginId = PluginId,
            ItemId = "work-42",
            ResourceId = ResourceId,
            Revision = 9,
            ActionId = "open",
            Target = new SidebarCollectionTarget { PaneId = "w1:p3" },
        };
        var hit = new ChromeHit(
            ChromeHitKind.SidebarCollectionItem,
            PaneId: "atomic:work-42",
            CollectionActivation: activation);
        var primary = ChromeHitApply.Apply(hit, overflowOffset: 0, maxOverflowOffset: 0);
        Assert.Equal("w1:p3", primary.FocusPaneId);
        Assert.False(primary.InvokeCollectionAction);

        var secondary = ChromeHitApply.Apply(
            hit with { SecondaryActivation = true },
            overflowOffset: 0,
            maxOverflowOffset: 0);
        Assert.True(secondary.InvokeCollectionAction);
        Assert.Equal(ResourceId, secondary.CollectionActivation?.ResourceId);
        Assert.Equal(9, secondary.CollectionActivation?.Revision);
    }

    [Fact]
    public void Built_in_sections_stay_first_and_cannot_be_replaced()
    {
        var ui = ConfigWithPluginSection();
        var frame = Compose(ui);
        var ids = frame.Panes.Where(p => p.Visible).Select(p => p.Id).ToArray();
        Assert.Equal(SidebarTokenGrammar.SpacesId, ids[0]);
        Assert.Equal(SidebarTokenGrammar.AgentsId, ids[1]);
        Assert.False(ChromeSectionRegistry.Core().TryRegister(new ResourceChromeSectionStrategy(
            new SidebarPluginSectionBinding
            {
                Id = SidebarTokenGrammar.AgentsId,
                ResourceId = ResourceId,
                Title = "Agents",
            })));
    }

    [Fact]
    public void Snapshot_resources_restore_rows_on_compose()
    {
        const string json = """
            {
              "resources": [
                {
                  "resource_id": "plugin:atomic.workbench/queue",
                  "revision": 4,
                  "freshness": "ready",
                  "value": {
                    "items": [
                      {
                        "id": "work-42",
                        "status": "working",
                        "tokens": { "title": "Plan retry policy", "age": "12m" },
                        "target": { "pane_id": "w1:p3" }
                      }
                    ]
                  }
                }
              ]
            }
            """;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var input = SidebarLiveModel.FromSnapshot(
            doc.RootElement,
            ConfigWithPluginSection(),
            expanded: true,
            requestedWidth: 26,
            linkedPluginIds: new HashSet<string>(StringComparer.Ordinal) { PluginId });
        var frame = SidebarSectionComposer.Compose(
            input,
            SidebarPluginSectionRegistry.Build(ConfigWithPluginSection(), continuityEnabled: true, input.LinkedPluginIds));
        var pane = Assert.Single(frame.Panes, p => p.Id == "atomic");
        Assert.Single(pane.Rows);
        Assert.Contains("Plan retry policy", pane.Rows[0].Label, StringComparison.Ordinal);
    }

    [Fact]
    public void Mouse_and_keyboard_hit_collection_row()
    {
        var frame = Compose(
            ConfigWithPluginSection(),
            resources: [Resource(revision: 1, Item("a", "Alpha"))],
            width: 26);
        var geo = LayoutChromeGeometry.Compute(
            80,
            24,
            new Hypa.AgentRuntime.Protocol.Models.LayoutNodeDto { Type = "pane", PaneId = "p1" },
            zoomed: false,
            zoomedPaneId: null,
            focusedPaneId: "p1",
            ConfigWithPluginSection(),
            tabCount: 1,
            AttachClientMode.Terminal,
            sidebarOpen: true,
            sidebarWidth: 26,
            sidebarFrame: frame);
        var row = Assert.Single(
            geo.SidebarRows,
            h => h.Kind is SidebarStubKind.CollectionItem && h.Id == "atomic:a");
        var hit = ChromeHitTest.Hit(geo, row.Rect.Col + 1, row.Rect.Row);
        Assert.Equal(ChromeHitKind.SidebarCollectionItem, hit?.Kind);
    }

    [Fact]
    public void Narrow_layout_keeps_plugin_section_reachable()
    {
        var ui = ConfigWithPluginSection();
        var frame = Compose(ui, resources: [Resource(revision: 1, Item("a", "Alpha"))], width: 18);
        var geo = LayoutChromeGeometry.Compute(
            62,
            24,
            new Hypa.AgentRuntime.Protocol.Models.LayoutNodeDto { Type = "pane", PaneId = "p1" },
            zoomed: false,
            zoomedPaneId: null,
            focusedPaneId: "p1",
            ui,
            tabCount: 1,
            AttachClientMode.Terminal,
            sidebarOpen: true,
            sidebarWidth: 18,
            sidebarFrame: frame);
        Assert.True(geo.IsNarrow);
        var pane = Assert.Single(frame.Panes, p => p.Id == "atomic");
        Assert.Single(pane.Rows);
        var stubs = SidebarLiveModel.ToStubRows(frame);
        Assert.Contains(stubs, s => s.Id == "atomic:a");
    }

    [Fact]
    public void Width_clamp_truncates_token_text()
    {
        var frame = Compose(
            ConfigWithPluginSection(),
            resources:
            [
                Resource(
                    revision: 1,
                    Item(
                        "a",
                        "Alpha",
                        tokens: new Dictionary<string, string>
                        {
                            ["title"] = new string('w', 120),
                            ["age"] = "12m",
                        })),
            ],
            width: 18);
        var pane = Assert.Single(frame.Panes, p => p.Id == "atomic");
        Assert.True(SafeDisplayText.Width(pane.Rows[0].Label) <= 18);
    }

    [Fact]
    public void Control_characters_do_not_reach_painted_label()
    {
        var frame = Compose(
            ConfigWithPluginSection(),
            resources:
            [
                Resource(
                    revision: 1,
                    Item(
                        "a",
                        "Alpha",
                        tokens: new Dictionary<string, string>
                        {
                            ["title"] = "bad\u001b[31mtext",
                        })),
            ]);
        var pane = Assert.Single(frame.Panes, p => p.Id == "atomic");
        Assert.DoesNotContain("\u001b", pane.Rows[0].Label, StringComparison.Ordinal);
    }

    private static AttachUiConfig ConfigWithPluginSection() =>
        TomlAttachConfigBinder.Bind("""
            [[ui.sidebar.section]]
            id = "atomic"
            resource = "plugin:atomic.workbench/queue"
            title = "Atomic"
            rows = ["state_icon", "$title", "$age"]
            sort = "attention"
            order = 2
            """).Value.Ui;

    private static SidebarFrame Compose(
        AttachUiConfig ui,
        bool linked = true,
        IReadOnlyList<SidebarPluginResourceView>? resources = null,
        int width = 26)
    {
        var input = new SidebarComposeInput
        {
            Ui = ui,
            Expanded = true,
            RequestedWidth = width,
            LinkedPluginIds = linked
                ? new HashSet<string>(StringComparer.Ordinal) { PluginId }
                : new HashSet<string>(StringComparer.Ordinal),
            PluginResources = resources ?? [],
        };
        return SidebarSectionComposer.Compose(
            input,
            SidebarPluginSectionRegistry.Build(ui, continuityEnabled: true, input.LinkedPluginIds));
    }

    private static SidebarPluginResourceView Resource(
        long revision,
        params SidebarPluginCollectionItemView[] items) =>
        new()
        {
            ResourceId = ResourceId,
            Revision = revision,
            Freshness = PluginResourceLimits.FreshnessReady,
            Items = items,
        };

    private static SidebarPluginCollectionItemView Item(
        string id,
        string label,
        long attention = 0,
        long? seq = null,
        IReadOnlyDictionary<string, string>? tokens = null,
        SidebarCollectionTarget? target = null,
        string? action = null) =>
        new()
        {
            Id = id,
            Label = label,
            Status = SidebarTokenGrammar.Working,
            Attention = attention,
            Sequence = seq,
            Tokens = tokens ?? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["title"] = label,
            },
            Target = target,
            ActionId = action,
        };
}
