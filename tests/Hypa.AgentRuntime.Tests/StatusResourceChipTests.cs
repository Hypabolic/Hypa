using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Status;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol.Models;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class StatusResourceChipTests
{
    [Fact]
    public void Resource_chip_bind_formats_attention_count()
    {
        var entries = new[]
        {
            AttachTabBarRightEntry.ForResource(
                "plugin:atomic.workbench/queue",
                "$attention_count waiting",
                maxItems: 1),
        };
        var resources = new[]
        {
            ReadyResource("plugin:atomic.workbench/queue", attention: 2),
        };

        var chips = StatusResourceChipComposer.Compose(entries, resources);
        var chip = Assert.Single(chips);
        Assert.Equal(StatusChipKind.Resource, chip.Kind);
        Assert.Equal("2 waiting", chip.Text);
    }

    [Fact]
    public void Missing_resource_hides_chip_without_config_error()
    {
        var entries = new[]
        {
            AttachTabBarRightEntry.ForResource("plugin:atomic.workbench/queue", "$attention_count waiting"),
        };

        var chips = StatusResourceChipComposer.Compose(entries, resources: []);
        Assert.Empty(chips);
    }

    [Fact]
    public void Core_chips_stay_first_and_plugin_chip_is_clamped()
    {
        var wide = StatusChipComposer.Compose(new StatusChipComposeInput
        {
            SessionName = "default",
            WorkspaceLabel = "proj",
            PaneLabel = "main",
            PaneId = "p1",
            AgentName = "claude",
            AgentState = "working",
            Extras =
            [
                new StatusChip
                {
                    Kind = StatusChipKind.Resource,
                    Text = "9 waiting",
                    DisplayWidth = SafeDisplayText.Width("9 waiting"),
                },
            ],
            MaxCols = 80,
        });

        Assert.Equal(StatusChipKind.Session, wide.Chips[0].Kind);
        var resourceIndex = wide.Chips.ToList().FindIndex(c => c.Kind is StatusChipKind.Resource);
        var agentIndex = wide.Chips.ToList().FindIndex(c => c.Kind is StatusChipKind.Agent);
        Assert.True(resourceIndex > agentIndex);

        var narrow = StatusChipComposer.Compose(new StatusChipComposeInput
        {
            SessionName = "default",
            WorkspaceLabel = "proj",
            PaneLabel = "main",
            PaneId = "p1",
            AgentName = "claude",
            AgentState = "working",
            Extras =
            [
                new StatusChip
                {
                    Kind = StatusChipKind.Resource,
                    Text = "9 waiting",
                    DisplayWidth = SafeDisplayText.Width("9 waiting"),
                },
            ],
            MaxCols = 12,
        });

        Assert.Equal(StatusChipKind.Session, narrow.Chips[0].Kind);
        Assert.DoesNotContain(narrow.Chips, c => c.Kind is StatusChipKind.Resource);
        Assert.True(SafeDisplayText.Width(narrow.Text) <= 12);
    }

    [Fact]
    public void Unavailable_resource_hides_chip()
    {
        var entries = new[]
        {
            AttachTabBarRightEntry.ForResource("plugin:atomic.workbench/queue", "$attention_count waiting"),
        };
        var resources = new[]
        {
            new PluginResourceDto
            {
                ResourceId = "plugin:atomic.workbench/queue",
                Freshness = "unavailable",
                Value = new PluginCollectionValueDto { Summary = "gone" },
            },
        };

        Assert.Empty(StatusResourceChipComposer.Compose(entries, resources));
    }

    private static PluginResourceDto ReadyResource(string resourceId, long attention) =>
        new()
        {
            ResourceId = resourceId,
            Freshness = "ready",
            Value = new PluginCollectionValueDto
            {
                Summary = "3 need attention",
                Items =
                [
                    new PluginCollectionItemDto
                    {
                        Id = "work-42",
                        Status = "blocked",
                        Attention = attention,
                    },
                ],
            },
        };
}
