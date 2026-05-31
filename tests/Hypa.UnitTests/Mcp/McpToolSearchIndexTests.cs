using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Mcp;
using Xunit;

namespace Hypa.UnitTests.Mcp;

public sealed class McpToolSearchIndexTests
{
    private readonly McpToolSearchIndex _sut = new();

    private static McpSchemaManifest Manifest(params (string server, string tool, string desc, string schema)[] entries)
    {
        var servers = entries
            .GroupBy(e => e.server)
            .Select(g => new McpServerSchema(
                g.Key,
                g.Select(e => new McpToolSchema(e.tool, e.desc, new JsonPayload(e.schema))).ToList()))
            .ToList();
        return new McpSchemaManifest(servers);
    }

    [Fact]
    public void Empty_query_returns_no_results()
    {
        var manifest = Manifest(("srv", "echo", "echoes text", "{}"));
        Assert.Empty(_sut.Search(manifest, ""));
    }

    [Fact]
    public void Whitespace_query_returns_no_results()
    {
        var manifest = Manifest(("srv", "echo", "echoes text", "{}"));
        Assert.Empty(_sut.Search(manifest, "   "));
    }

    [Fact]
    public void Exact_tool_name_match_returns_result_with_positive_score()
    {
        var manifest = Manifest(("srv", "read_file", "reads a file", "{}"));
        var results = _sut.Search(manifest, "read_file");
        Assert.Single(results);
        Assert.True(results[0].Score > 0);
    }

    [Fact]
    public void Irrelevant_query_returns_empty()
    {
        var manifest = Manifest(("srv", "read_file", "reads a file", "{}"));
        Assert.Empty(_sut.Search(manifest, "xyzzy_totally_unrelated"));
    }

    [Fact]
    public void Higher_scoring_result_is_ranked_first()
    {
        var manifest = Manifest(
            ("srv", "search_text", "searches text content", "{}"),
            ("srv", "read_file", "reads a file from disk", "{}"));

        var results = _sut.Search(manifest, "search text");
        Assert.Equal("search_text", results[0].ToolName);
    }

    [Fact]
    public void Server_name_is_included_in_search()
    {
        var manifest = Manifest(("filesystem", "list_dir", "lists directory", "{}"));
        var results = _sut.Search(manifest, "filesystem");
        Assert.Single(results);
        Assert.Equal("list_dir", results[0].ToolName);
    }

    [Fact]
    public void Schema_text_is_included_in_search()
    {
        var schema = "{\"properties\":{\"targetPath\":{\"type\":\"string\"}}}";
        var manifest = Manifest(("srv", "move_file", "moves a file", schema));
        var results = _sut.Search(manifest, "targetPath");
        Assert.Single(results);
    }

    [Fact]
    public void Search_is_case_insensitive()
    {
        var manifest = Manifest(("srv", "ReadFile", "Reads A File", "{}"));
        var results = _sut.Search(manifest, "readfile");
        Assert.Single(results);
    }

    [Fact]
    public void Results_are_deterministic_for_same_input()
    {
        var manifest = Manifest(
            ("a", "alpha", "first tool", "{}"),
            ("b", "beta", "second tool", "{}"));

        var r1 = _sut.Search(manifest, "tool");
        var r2 = _sut.Search(manifest, "tool");

        Assert.Equal(r1.Select(x => x.ToolName), r2.Select(x => x.ToolName));
    }
}
