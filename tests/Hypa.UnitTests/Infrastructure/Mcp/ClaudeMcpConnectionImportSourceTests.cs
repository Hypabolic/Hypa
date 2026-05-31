using System.Text.Json;
using Hypa.Infrastructure.Mcp.Import;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Mcp;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Mcp;

[Trait("Category", "McpImport")]
public sealed class ClaudeMcpConnectionImportSourceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public ClaudeMcpConnectionImportSourceTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private ClaudeMcpConnectionImportSource Sut(string? globalHome = null)
    {
        var home = globalHome ?? Path.Combine(_tempDir, "claude_home");
        Directory.CreateDirectory(home);
        return new ClaudeMcpConnectionImportSource(home);
    }

    private void WriteSettings(string claudeDir, object mcpServers)
    {
        Directory.CreateDirectory(claudeDir);
        var json = JsonSerializer.Serialize(new { mcpServers });
        File.WriteAllText(Path.Combine(claudeDir, "settings.json"), json);
    }

    private void WriteLocalSettings(string projectRoot, object mcpServers)
    {
        var claudeDir = Path.Combine(projectRoot, ".claude");
        Directory.CreateDirectory(claudeDir);
        var json = JsonSerializer.Serialize(new { mcpServers });
        File.WriteAllText(Path.Combine(claudeDir, "settings.local.json"), json);
    }

    [Fact]
    public async Task DiscoverAsync_MissingSettingsFile_ReturnsEmpty()
    {
        var home = Path.Combine(_tempDir, "no_claude");
        var sut = new ClaudeMcpConnectionImportSource(home);

        var result = await sut.DiscoverAsync(
            new McpImportDiscoveryRequest(McpImportScope.Global, null), default);

        Assert.Empty(result);
    }

    [Fact]
    public async Task DiscoverAsync_StdioEntry_ParsesCommand()
    {
        var home = Path.Combine(_tempDir, "claude_stdio");
        WriteSettings(home, new
        {
            github = new { type = "stdio", command = "gh-mcp" },
        });

        var sut = new ClaudeMcpConnectionImportSource(home);
        var result = await sut.DiscoverAsync(
            new McpImportDiscoveryRequest(McpImportScope.Global, null), default);

        Assert.Single(result);
        var conn = result[0];
        Assert.Equal("github", conn.SourceName);
        Assert.Equal(McpImportCandidateStatus.Importable, conn.Status);
        Assert.NotNull(conn.Server);
        Assert.Equal(McpTransportKind.Stdio, conn.Server.Transport.Kind);
        Assert.Equal("gh-mcp", conn.Server.Transport.Endpoint);
    }

    [Fact]
    public async Task DiscoverAsync_StdioEntry_WithArgs_JoinsCommandAndArgs()
    {
        var home = Path.Combine(_tempDir, "claude_args");
        WriteSettings(home, new
        {
            github = new { type = "stdio", command = "gh-mcp", args = new[] { "--stdio", "--port", "8080" } },
        });

        var sut = new ClaudeMcpConnectionImportSource(home);
        var result = await sut.DiscoverAsync(
            new McpImportDiscoveryRequest(McpImportScope.Global, null), default);

        Assert.Single(result);
        Assert.Equal("gh-mcp --stdio --port 8080", result[0].Server?.Transport.Endpoint);
    }

    [Fact]
    public async Task DiscoverAsync_HttpEntry_WithUrl_ParsesRemote()
    {
        var home = Path.Combine(_tempDir, "claude_http");
        WriteSettings(home, new
        {
            remote = new { type = "streamableHttp", url = "https://tools.example.com/mcp" },
        });

        var sut = new ClaudeMcpConnectionImportSource(home);
        var result = await sut.DiscoverAsync(
            new McpImportDiscoveryRequest(McpImportScope.Global, null), default);

        Assert.Single(result);
        var conn = result[0];
        Assert.Equal(McpImportCandidateStatus.Importable, conn.Status);
        Assert.Equal(McpTransportKind.Http, conn.Server?.Transport.Kind);
        Assert.Equal("https://tools.example.com/mcp", conn.Server?.Transport.Endpoint);
    }

    [Fact]
    public async Task DiscoverAsync_SseEntry_WithEndpoint_ParsesRemote()
    {
        var home = Path.Combine(_tempDir, "claude_sse");
        WriteSettings(home, new
        {
            events = new { type = "sse", endpoint = "https://events.example.com/mcp" },
        });

        var sut = new ClaudeMcpConnectionImportSource(home);
        var result = await sut.DiscoverAsync(
            new McpImportDiscoveryRequest(McpImportScope.Global, null), default);

        Assert.Single(result);
        Assert.Equal(McpTransportKind.Sse, result[0].Server?.Transport.Kind);
        Assert.Equal("https://events.example.com/mcp", result[0].Server?.Transport.Endpoint);
    }

    [Fact]
    public async Task DiscoverAsync_HypaEntry_SkippedSelf()
    {
        var home = Path.Combine(_tempDir, "claude_self");
        WriteSettings(home, new
        {
            hypa = new { type = "stdio", command = "hypa serve" },
        });

        var sut = new ClaudeMcpConnectionImportSource(home);
        var result = await sut.DiscoverAsync(
            new McpImportDiscoveryRequest(McpImportScope.Global, null), default);

        Assert.Single(result);
        Assert.Equal(McpImportCandidateStatus.SkippedSelf, result[0].Status);
    }

    [Fact]
    public async Task DiscoverAsync_CommandIsHypaServe_SkippedSelf()
    {
        var home = Path.Combine(_tempDir, "claude_selfcmd");
        WriteSettings(home, new
        {
            mymcp = new { type = "stdio", command = "hypa serve" },
        });

        var sut = new ClaudeMcpConnectionImportSource(home);
        var result = await sut.DiscoverAsync(
            new McpImportDiscoveryRequest(McpImportScope.Global, null), default);

        Assert.Single(result);
        Assert.Equal(McpImportCandidateStatus.SkippedSelf, result[0].Status);
    }

    [Fact]
    public async Task DiscoverAsync_EntryWithRawEnvValue_SkippedUnsafeSecret()
    {
        var home = Path.Combine(_tempDir, "claude_rawenv");
        WriteSettings(home, new
        {
            secured = new
            {
                type = "stdio",
                command = "secure-mcp",
                env = new { API_TOKEN = "raw-secret-value" },
            },
        });

        var sut = new ClaudeMcpConnectionImportSource(home);
        var result = await sut.DiscoverAsync(
            new McpImportDiscoveryRequest(McpImportScope.Global, null), default);

        Assert.Single(result);
        Assert.Equal(McpImportCandidateStatus.SkippedUnsafeSecret, result[0].Status);
    }

    [Fact]
    public async Task DiscoverAsync_EntryWithNoEnv_AuthNone_Importable()
    {
        var home = Path.Combine(_tempDir, "claude_noenv");
        WriteSettings(home, new
        {
            plain = new { type = "stdio", command = "plain-mcp" },
        });

        var sut = new ClaudeMcpConnectionImportSource(home);
        var result = await sut.DiscoverAsync(
            new McpImportDiscoveryRequest(McpImportScope.Global, null), default);

        Assert.Single(result);
        Assert.Equal(McpImportCandidateStatus.Importable, result[0].Status);
        Assert.IsType<NoneAuthConfig>(result[0].Server?.Auth);
    }

    [Fact]
    public async Task DiscoverAsync_ProjectScope_ReadsLocalSettings()
    {
        var home = Path.Combine(_tempDir, "claude_proj_home");
        var projectRoot = Path.Combine(_tempDir, "myproject");
        WriteLocalSettings(projectRoot, new
        {
            local_tool = new { type = "stdio", command = "local-mcp" },
        });

        var sut = new ClaudeMcpConnectionImportSource(home);
        var result = await sut.DiscoverAsync(
            new McpImportDiscoveryRequest(McpImportScope.Project, projectRoot), default);

        Assert.Single(result);
        Assert.Equal("local_tool", result[0].SourceName);
        Assert.Equal("project", result[0].SourceScope);
    }

    [Fact]
    public async Task DiscoverAsync_MissingCommand_StdioEntry_SkippedIncomplete()
    {
        var home = Path.Combine(_tempDir, "claude_nocommand");
        WriteSettings(home, new
        {
            broken = new { type = "stdio" },
        });

        var sut = new ClaudeMcpConnectionImportSource(home);
        var result = await sut.DiscoverAsync(
            new McpImportDiscoveryRequest(McpImportScope.Global, null), default);

        Assert.Single(result);
        Assert.Equal(McpImportCandidateStatus.SkippedIncomplete, result[0].Status);
    }

    [Fact]
    public async Task DiscoverAsync_MalformedJson_ReturnsParseError()
    {
        var home = Path.Combine(_tempDir, "claude_bad");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "settings.json"), "{ not valid json {{");

        var sut = new ClaudeMcpConnectionImportSource(home);
        var result = await sut.DiscoverAsync(
            new McpImportDiscoveryRequest(McpImportScope.Global, null), default);

        Assert.Single(result);
        Assert.Equal(McpImportCandidateStatus.ParseError, result[0].Status);
    }
}
