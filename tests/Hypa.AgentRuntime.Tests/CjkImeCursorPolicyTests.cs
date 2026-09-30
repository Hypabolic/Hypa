using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class CjkImeCursorPolicyTests
{
    [Fact]
    public void Disabled_filter_does_not_reveal()
    {
        var filter = CjkImeRevealFilter.From(AttachExperimentalConfig.Default);
        Assert.False(filter.Enabled);
        Assert.False(filter.ShouldReveal("claude"));
        Assert.False(filter.ShouldReveal(null));
    }

    [Fact]
    public void Empty_agent_list_reveals_any_focused_pane()
    {
        var filter = CjkImeRevealFilter.From(new AttachExperimentalConfig
        {
            RevealHiddenCursorForCjkIme = true,
        });
        Assert.True(filter.ShouldReveal("claude"));
        Assert.True(filter.ShouldReveal(null));
        Assert.True(filter.ShouldReveal("vim"));
        Assert.Equal(2, filter.CursorShape);
    }

    [Fact]
    public void Agent_allow_list_restricts_reveal()
    {
        var filter = CjkImeRevealFilter.From(new AttachExperimentalConfig
        {
            RevealHiddenCursorForCjkIme = true,
            CjkImeAgents = ["claude", "codex", "not-an-agent"],
        });
        Assert.True(filter.FilterConfigured);
        Assert.True(filter.ShouldReveal("claude"));
        Assert.True(filter.ShouldReveal("CLAUDE"));
        Assert.False(filter.ShouldReveal("pi"));
        Assert.False(filter.ShouldReveal(null));
        Assert.Equal(new[] { "claude", "codex" }, filter.Agents);
    }

    [Fact]
    public void Allow_list_with_no_valid_names_does_not_reveal()
    {
        var filter = CjkImeRevealFilter.From(new AttachExperimentalConfig
        {
            RevealHiddenCursorForCjkIme = true,
            CjkImeAgents = ["not-an-agent"],
        });
        Assert.True(filter.FilterConfigured);
        Assert.Empty(filter.Agents);
        Assert.False(filter.ShouldReveal("claude"));
    }

    [Theory]
    [InlineData("block", 1)]
    [InlineData("steady_block", 2)]
    [InlineData("underline", 3)]
    [InlineData("steady_underline", 4)]
    [InlineData("bar", 5)]
    [InlineData("steady_bar", 6)]
    [InlineData("unknown", 2)]
    public void Cursor_shape_maps_to_decscusr(string shape, int expected)
    {
        Assert.Equal(expected, CjkImeRevealFilter.ToDecscusr(shape));
    }
}
