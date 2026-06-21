using Hypa.Infrastructure.Skills;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Skills;

public sealed class SkillRendererTests
{
    private readonly SkillRenderer _renderer = new();

    [Fact]
    public void GetRulesContent_MentionsHypaShell()
    {
        var content = _renderer.GetRulesContent();
        Assert.Contains("hypa_shell", content);
    }

    [Fact]
    public void Render_WithMcp_ContainsMcpSections()
    {
        var content = _renderer.Render(fullSections: true, includeMcp: true);
        Assert.Contains("MCP Tools Reference", content);
        Assert.Contains("MCP Server Management", content);
    }

    [Fact]
    public void Render_WithMcp_DoesNotContainMarkerTags()
    {
        var content = _renderer.Render(fullSections: true, includeMcp: true);
        Assert.DoesNotContain("mcp-start", content);
        Assert.DoesNotContain("mcp-end", content);
    }

    [Fact]
    public void Render_WithoutMcp_StripsMcpToolsReferenceSection()
    {
        var content = _renderer.Render(fullSections: true, includeMcp: false);
        Assert.DoesNotContain("MCP Tools Reference", content);
        Assert.DoesNotContain("hypa_read", content);
    }

    [Fact]
    public void Render_WithoutMcp_StripsMcpServerManagementSection()
    {
        var content = _renderer.Render(fullSections: true, includeMcp: false);
        Assert.DoesNotContain("MCP Server Management", content);
    }

    [Fact]
    public void Render_WithoutMcp_KeepsNonMcpSections()
    {
        var content = _renderer.Render(fullSections: true, includeMcp: false);
        Assert.Contains("Code Intelligence", content);
        Assert.Contains("Markdown Queries", content);
        Assert.Contains("Session + Trust + Filters", content);
    }

    [Fact]
    public void Render_TrimmedWithMcp_SectionsOneAndTwoOnly()
    {
        // fullSections: false cuts at section 2 — sections 3+ (including MCP) are already excluded
        var content = _renderer.Render(fullSections: false, includeMcp: true);
        Assert.DoesNotContain("MCP Tools Reference", content);
        Assert.Contains("Command Reference", content);
    }
}
