using Hypa.Infrastructure.Hooks;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Hooks;

public sealed class TomlSectionHelperTests
{
    // IsDescendantSectionHeader

    [Theory]
    [InlineData("[mcp_servers.hypa]")]
    [InlineData("[mcp_servers.hypa] # inline comment")]
    [InlineData("[mcp_servers.hypa]  # spaced comment")]
    [InlineData("  [mcp_servers.hypa]  ")]
    public void IsDescendantSectionHeader_ExactAndCommentVariants_ReturnsTrue(string line)
    {
        Assert.True(TomlSectionHelper.IsDescendantSectionHeader(line, "mcp_servers.hypa"));
    }

    [Theory]
    [InlineData("[[mcp_servers.hypa]]")]
    [InlineData("[[mcp_servers.hypa]] # inline comment")]
    public void IsDescendantSectionHeader_ArrayTableVariants_ReturnsTrue(string line)
    {
        Assert.True(TomlSectionHelper.IsDescendantSectionHeader(line, "mcp_servers.hypa"));
    }

    [Theory]
    [InlineData("[mcp_servers.hypa.env]")]
    [InlineData("[mcp_servers.hypa.env] # comment")]
    public void IsDescendantSectionHeader_ChildSection_ReturnsTrue(string line)
    {
        Assert.True(TomlSectionHelper.IsDescendantSectionHeader(line, "mcp_servers.hypa"));
    }

    [Theory]
    [InlineData("[mcp_servers.other]")]
    [InlineData("[mcp_servers.other] # comment")]
    [InlineData("command = \"/usr/bin/hypa\"")]
    [InlineData("")]
    public void IsDescendantSectionHeader_UnrelatedLine_ReturnsFalse(string line)
    {
        Assert.False(TomlSectionHelper.IsDescendantSectionHeader(line, "mcp_servers.hypa"));
    }

    // TryParseHeaderPath

    [Theory]
    [InlineData("[mcp_servers.hypa]", "mcp_servers.hypa")]
    [InlineData("[mcp_servers.hypa] # comment", "mcp_servers.hypa")]
    [InlineData("[mcp_servers.hypa]  # spaced", "mcp_servers.hypa")]
    [InlineData("[[mcp_servers.hypa]]", "mcp_servers.hypa")]
    [InlineData("[[mcp_servers.hypa]] # comment", "mcp_servers.hypa")]
    [InlineData("[features]", "features")]
    public void TryParseHeaderPath_ValidHeaders_ExtractsPath(string line, string expected)
    {
        Assert.True(TomlSectionHelper.TryParseHeaderPath(line, out var path));
        Assert.Equal(expected, path);
    }

    [Theory]
    [InlineData("command = \"hypa\"")]
    [InlineData("")]
    [InlineData("# comment")]
    public void TryParseHeaderPath_NonHeaders_ReturnsFalse(string line)
    {
        Assert.False(TomlSectionHelper.TryParseHeaderPath(line, out var path));
        Assert.Equal(string.Empty, path);
    }
}
