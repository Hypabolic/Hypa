using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.AgentRuntime.Infrastructure.Config;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
// / Optional per-token sidebar style.
/// <c>src/config/sidebar.rs:569-610</c> and <c>:665-677</c>.
/// </summary>
public class SidebarTokenStyleConfigTests
{
    [Fact]
    public void Styled_token_binds_without_changing_plain_tokens()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [ui.sidebar.agents]
            rows = [[{ token = "workspace", fg = "#abc", bold = false }, "workspace"], [{ token = "$summary", dim = false }]]

            [ui.sidebar.agents.rows_by_agent]
            claude = [[{ token = "agent", fg = "#112233", bold = true, dim = false }]]

            [ui.sidebar.spaces]
            rows = [[{ token = "git_status", fg = "#ff00aa" }], [{ token = "$jj", bold = true }]]
            """);
        Assert.True(result.IsOk, result.IsOk ? "" : result.Error.ToString());

        var workspace = result.Value.Ui.Sidebar.Agents.Rows[0][0];
        Assert.Equal("workspace", workspace.Id);
        Assert.Equal(false, workspace.Style.Bold);
        Assert.Equal(ThemeColor.Rgb(0xaa, 0xbb, 0xcc), workspace.Style.Fg);
        Assert.Equal("workspace", result.Value.Ui.Sidebar.Agents.Rows[0][1].Id);
        Assert.True(result.Value.Ui.Sidebar.Agents.Rows[0][1].Style.IsEmpty);

        var agent = result.Value.Ui.Sidebar.Agents.RowsByAgent["claude"][0][0];
        Assert.Equal("agent", agent.Id);
        Assert.Equal(true, agent.Style.Bold);
        Assert.Equal(false, agent.Style.Dim);
        Assert.Equal(ThemeColor.Rgb(0x11, 0x22, 0x33), agent.Style.Fg);

        var git = result.Value.Ui.Sidebar.Spaces.Rows[0][0];
        Assert.Equal("git_status", git.Id);
        Assert.Equal(ThemeColor.Rgb(0xff, 0x00, 0xaa), git.Style.Fg);
        Assert.Equal("$jj", result.Value.Ui.Sidebar.Spaces.Rows[1][0].Id);
        Assert.Equal(true, result.Value.Ui.Sidebar.Spaces.Rows[1][0].Style.Bold);
    }

    [Fact]
    public void Named_and_short_hex_and_unknown_fields_fail_closed()
    {
        foreach (var entry in new[]
        {
            """{ token = "workspace", fg = "red" }""",
            """{ token = "workspace", fg = "#abcd" }""",
            """{ token = "workspace", underline = true }""",
        })
        {
            var result = TomlAttachConfigBinder.Bind($"""
                [ui.sidebar.agents]
                rows = [[{entry}]]
                """);
            Assert.False(result.IsOk);
        }
    }

    [Fact]
    public void Conditional_rules_bind_and_reject_non_text_tokens()
    {
        var ok = TomlAttachConfigBinder.Bind("""
            [ui.sidebar.agents]
            rows = [[{ token = "machine", fg = "#fff", rules = [{ equals = "Local", fg = "#f00" }, { starts_with = "fed", ignore_case = true, bold = true }] }]]
            """);
        Assert.True(ok.IsOk, ok.IsOk ? "" : ok.Error.ToString());
        var spec = ok.Value.Ui.Sidebar.Agents.Rows[0][0];
        Assert.Equal("machine", spec.Id);
        Assert.Equal(2, spec.Rules.Count);
        Assert.Equal(ThemeColor.Rgb(0xff, 0x00, 0x00), spec.StyleForValue("Local").Fg);
        Assert.Equal(true, spec.StyleForValue("Federal").Bold);

        var blocked = TomlAttachConfigBinder.Bind("""
            [ui.sidebar.agents]
            rows = [[{ token = "state_icon", rules = [{ equals = "x" }] }]]
            """);
        Assert.False(blocked.IsOk);
        Assert.Contains(blocked.Errors, e =>
            e.Message.Contains("text-valued token", StringComparison.Ordinal));
    }

    [Fact]
    public void Numeric_rule_threshold_is_strict_and_finite()
    {
        var ok = TomlAttachConfigBinder.Bind("""
            [ui.sidebar.agents]
            rows = [[{ token = "$load", rules = [{ gt = 80, dim = false }, { lt = 20.5, dim = true }] }]]
            """);
        Assert.True(ok.IsOk, ok.IsOk ? "" : ok.Error.ToString());
        var spec = ok.Value.Ui.Sidebar.Agents.Rows[0][0];
        Assert.Equal(false, spec.StyleForValue("90").Dim);
        Assert.Equal(true, spec.StyleForValue("20").Dim);
        Assert.Null(spec.StyleForValue("80").Dim);
        Assert.Null(spec.StyleForValue("90%").Dim);
    }
}
