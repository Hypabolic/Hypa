using Hypa.Infrastructure.Mcp.Import;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Mcp;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Mcp;

[Trait("Category", "McpImport")]
public sealed class CodexMcpConnectionImportSourceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public CodexMcpConnectionImportSourceTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private CodexMcpConnectionImportSource Sut(string? globalConfigPath = null)
    {
        var path = globalConfigPath ?? Path.Combine(_tempDir, "codex_home", "config.toml");
        return new CodexMcpConnectionImportSource(path);
    }

    private string WriteGlobalConfig(string toml)
    {
        var dir = Path.Combine(_tempDir, "codex_home");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, toml);
        return path;
    }

    private void WriteProjectConfig(string projectRoot, string toml)
    {
        var dir = Path.Combine(projectRoot, ".codex");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.toml"), toml);
    }

    [Fact]
    public async Task DiscoverAsync_MissingConfigToml_ReturnsEmpty()
    {
        var sut = Sut(Path.Combine(_tempDir, "nonexistent", "config.toml"));
        var result = await sut.DiscoverAsync(
            new McpImportDiscoveryRequest(McpImportScope.Global, null), default);
        Assert.Empty(result);
    }

    [Fact]
    public async Task DiscoverAsync_StdioEntry_ParsesCommand()
    {
        var path = WriteGlobalConfig("""
            [mcp_servers.github]
            command = "gh-mcp"
            """);

        var result = await Sut(path).DiscoverAsync(
            new McpImportDiscoveryRequest(McpImportScope.Global, null), default);

        Assert.Single(result);
        var conn = result[0];
        Assert.Equal("github", conn.SourceName);
        Assert.Equal(McpImportCandidateStatus.Importable, conn.Status);
        Assert.Equal(McpTransportKind.Stdio, conn.Server?.Transport.Kind);
        Assert.Equal("gh-mcp", conn.Server?.Transport.Endpoint);
    }

    [Fact]
    public async Task DiscoverAsync_StdioEntry_WithArgs_ParsesInlineArray()
    {
        var path = WriteGlobalConfig("""
            [mcp_servers.github]
            command = "gh-mcp"
            args = ["--stdio", "--verbose"]
            """);

        var result = await Sut(path).DiscoverAsync(
            new McpImportDiscoveryRequest(McpImportScope.Global, null), default);

        Assert.Single(result);
        Assert.Equal("gh-mcp --stdio --verbose", result[0].Server?.Transport.Endpoint);
    }

    [Fact]
    public async Task DiscoverAsync_RemoteEntry_ParsesUrl()
    {
        var path = WriteGlobalConfig("""
            [mcp_servers.remote]
            url = "https://tools.example.com/mcp"
            """);

        var result = await Sut(path).DiscoverAsync(
            new McpImportDiscoveryRequest(McpImportScope.Global, null), default);

        Assert.Single(result);
        var conn = result[0];
        Assert.Equal(McpImportCandidateStatus.Importable, conn.Status);
        Assert.Equal(McpTransportKind.HttpAutoDetect, conn.Server?.Transport.Kind);
        Assert.Equal("https://tools.example.com/mcp", conn.Server?.Transport.Endpoint);
    }

    [Fact]
    public async Task DiscoverAsync_HypaSection_SkippedSelf()
    {
        var path = WriteGlobalConfig("""
            [mcp_servers.hypa]
            command = "hypa"
            args = ["serve"]
            """);

        var result = await Sut(path).DiscoverAsync(
            new McpImportDiscoveryRequest(McpImportScope.Global, null), default);

        Assert.Single(result);
        Assert.Equal(McpImportCandidateStatus.SkippedSelf, result[0].Status);
    }

    [Fact]
    public async Task DiscoverAsync_CommandIsHypaServe_SkippedSelf()
    {
        var path = WriteGlobalConfig("""
            [mcp_servers.mymcp]
            command = "hypa serve"
            """);

        var result = await Sut(path).DiscoverAsync(
            new McpImportDiscoveryRequest(McpImportScope.Global, null), default);

        Assert.Single(result);
        Assert.Equal(McpImportCandidateStatus.SkippedSelf, result[0].Status);
    }

    [Fact]
    public async Task DiscoverAsync_MultipleServers_ParsesAll()
    {
        var path = WriteGlobalConfig("""
            [mcp_servers.github]
            command = "gh-mcp"

            [mcp_servers.linear]
            command = "linear-mcp"
            """);

        var result = await Sut(path).DiscoverAsync(
            new McpImportDiscoveryRequest(McpImportScope.Global, null), default);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, c => c.SourceName == "github");
        Assert.Contains(result, c => c.SourceName == "linear");
    }

    [Fact]
    public async Task DiscoverAsync_ProjectScope_ReadsProjectConfigToml()
    {
        var globalPath = Path.Combine(_tempDir, "codex_no_global", "config.toml");
        var projectRoot = Path.Combine(_tempDir, "myproject");
        WriteProjectConfig(projectRoot, """
            [mcp_servers.local_tool]
            command = "local-mcp"
            """);

        var result = await new CodexMcpConnectionImportSource(globalPath).DiscoverAsync(
            new McpImportDiscoveryRequest(McpImportScope.Project, projectRoot), default);

        Assert.Single(result);
        Assert.Equal("local_tool", result[0].SourceName);
        Assert.Equal("project", result[0].SourceScope);
    }

    [Fact]
    public async Task DiscoverAsync_MalformedToml_MultilineArray_ReturnsParseError()
    {
        var path = WriteGlobalConfig("""
            [mcp_servers.broken]
            command = "my-mcp"
            args = [
              "--arg1",
              "--arg2"
            ]
            """);

        var result = await Sut(path).DiscoverAsync(
            new McpImportDiscoveryRequest(McpImportScope.Global, null), default);

        Assert.Single(result);
        Assert.Equal(McpImportCandidateStatus.ParseError, result[0].Status);
    }

    [Fact]
    public async Task DiscoverAsync_RawSecretBearerToken_SkippedUnsupported()
    {
        var path = WriteGlobalConfig("""
            [mcp_servers.unsafe-server]
            command = "my-mcp"
            bearer_token = "sk-1234567890abcdefghij"
            """);

        var result = await Sut(path).DiscoverAsync(
            new McpImportDiscoveryRequest(McpImportScope.Global, null), default);

        Assert.Single(result);
        var conn = result[0];
        Assert.Equal(McpImportCandidateStatus.SkippedUnsupported, conn.Status);
        Assert.Contains("raw secret", conn.Detail ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("env:", conn.Detail ?? "");
        Assert.Null(conn.Server);
    }

    [Fact]
    public async Task DiscoverAsync_EnvBearerToken_Importable()
    {
        var path = WriteGlobalConfig("""
            [mcp_servers.safe-server]
            command = "my-mcp"
            bearer_token = "env:MCP_TOKEN"
            """);

        var result = await Sut(path).DiscoverAsync(
            new McpImportDiscoveryRequest(McpImportScope.Global, null), default);

        Assert.Single(result);
        var conn = result[0];
        Assert.Equal(McpImportCandidateStatus.Importable, conn.Status);
        Assert.NotNull(conn.Server);
        Assert.IsType<BearerAuthConfig>(conn.Server.Auth);
        Assert.Equal("env:MCP_TOKEN", ((BearerAuthConfig)conn.Server.Auth).TokenRef);
    }

    [Fact]
    public async Task DiscoverAsync_FileBearerToken_Importable()
    {
        var path = WriteGlobalConfig("""
            [mcp_servers.safe-server]
            url = "https://api.example.com"
            bearer_token = "file:/path/to/token"
            """);

        var result = await Sut(path).DiscoverAsync(
            new McpImportDiscoveryRequest(McpImportScope.Global, null), default);

        Assert.Single(result);
        var conn = result[0];
        Assert.Equal(McpImportCandidateStatus.Importable, conn.Status);
        Assert.NotNull(conn.Server);
        Assert.IsType<BearerAuthConfig>(conn.Server.Auth);
        Assert.Equal("file:/path/to/token", ((BearerAuthConfig)conn.Server.Auth).TokenRef);
    }
}
