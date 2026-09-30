using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class SidebarTokenSpanTests
{
    [Fact]
    public void Fit_drops_secondary_agent_kind_before_state_icon()
    {
        var tokens = new List<SidebarRowToken>
        {
            new("state_icon", "●"),
            new("workspace", "hypa"),
            new("agent", "claude"),
        };
        var fitted = SidebarTokenGrammar.FitVisibleTokens(tokens, maxWidth: 2);
        Assert.Contains(fitted, t => t.Id == "state_icon");
        Assert.DoesNotContain(fitted, t => t.Id == "agent");
    }

    [Fact]
    public void Fit_keeps_agent_kind_when_width_allows()
    {
        var tokens = new List<SidebarRowToken>
        {
            new("state_icon", "●"),
            new("agent", "claude"),
        };
        var fitted = SidebarTokenGrammar.FitVisibleTokens(tokens, maxWidth: 20);
        Assert.Equal(2, fitted.Count);
        Assert.Equal("agent", fitted[1].Id);
    }

    [Fact]
    public void Fit_keeps_ellipsis_when_flex_grapheme_is_wider_than_budget()
    {
        var tokens = new List<SidebarRowToken>
        {
            new("agent", "日"),
        };
        var fitted = SidebarTokenGrammar.FitVisibleTokens(tokens, maxWidth: 1);
        var agent = Assert.Single(fitted);
        Assert.Equal("agent", agent.Id);
        Assert.Equal("…", agent.Text);
    }

    [Fact]
    public void Symbols_stay_distinct_from_dots()
    {
        Assert.Equal("●", SidebarTokenGrammar.StateIcon(SidebarTokenGrammar.Working, StatusIndicatorStyle.Dots));
        Assert.Equal("*", SidebarTokenGrammar.StateIcon(SidebarTokenGrammar.Working, StatusIndicatorStyle.Symbols));
        Assert.Equal("◆", SidebarTokenGrammar.StateIcon(SidebarTokenGrammar.Blocked, StatusIndicatorStyle.Dots));
        Assert.Equal("!", SidebarTokenGrammar.StateIcon(SidebarTokenGrammar.Blocked, StatusIndicatorStyle.Symbols));
        Assert.Equal("○", SidebarTokenGrammar.StateIcon(SidebarTokenGrammar.Idle, StatusIndicatorStyle.Dots));
        Assert.Equal("-", SidebarTokenGrammar.StateIcon(SidebarTokenGrammar.Idle, StatusIndicatorStyle.Symbols));
        Assert.Equal("●", SidebarTokenGrammar.StateIcon(SidebarTokenGrammar.Done, StatusIndicatorStyle.Dots));
        Assert.Equal("+", SidebarTokenGrammar.StateIcon(SidebarTokenGrammar.Done, StatusIndicatorStyle.Symbols));
        Assert.Equal("·", SidebarTokenGrammar.StateIcon(SidebarTokenGrammar.Unknown, StatusIndicatorStyle.Dots));
        Assert.Equal("?", SidebarTokenGrammar.StateIcon(SidebarTokenGrammar.Unknown, StatusIndicatorStyle.Symbols));
    }

    [Fact]
    public void Custom_token_unresolved_stays_empty()
    {
        var values = new SidebarTokenValues { Agent = "claude" };
        var tokens = SidebarTokenGrammar.TokensForRow(["$summary", "agent"], values);
        Assert.Single(tokens);
        Assert.Equal("agent", tokens[0].Id);
        Assert.Equal("claude", tokens[0].Text);
    }

    [Fact]
    public void Token_rule_first_match_overrides_base_style()
    {
        var spec = new SidebarTokenSpec
        {
            Id = "machine",
            Style = new SidebarTokenStyle { Fg = ThemeColor.Rgb(0xff, 0xff, 0xff) },
            Rules =
            [
                new SidebarTokenRule
                {
                    Kind = SidebarTokenRuleKind.Equals,
                    Text = "Local",
                    Style = new SidebarTokenStyle { Fg = ThemeColor.Rgb(0xff, 0x00, 0x00) },
                },
            ],
        };
        var matched = spec.StyleForValue("Local");
        Assert.Equal(ThemeColor.Rgb(0xff, 0x00, 0x00), matched.Fg);
        var missed = spec.StyleForValue("Remote");
        Assert.Equal(ThemeColor.Rgb(0xff, 0xff, 0xff), missed.Fg);
    }
}
