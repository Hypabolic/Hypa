using Hypa.Infrastructure.Mcp.Tools;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Sessions;
using Hypa.Sdk.CodeIntelligence;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Mcp;

public sealed class HypaSearchToolTests : IDisposable
{
    private readonly IFileSystem _fileSystem = Substitute.For<IFileSystem>();
    private readonly IProjectRootDetector _rootDetector = Substitute.For<IProjectRootDetector>();
    private readonly ICodeIndexRepository _codeIndex = Substitute.For<ICodeIndexRepository>();
    private readonly IEvidenceLedger _ledger = Substitute.For<IEvidenceLedger>();
    private readonly ISessionResolver _sessionResolver = Substitute.For<ISessionResolver>();
    private static readonly NullLogger<SearchService> _logger = NullLogger<SearchService>.Instance;
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var path in _tempFiles)
            try { File.Delete(path); } catch { /* best-effort */ }
    }

    public HypaSearchToolTests()
    {
        _rootDetector.Detect(Arg.Any<string>()).Returns("/project");
        _fileSystem.GetFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>()).Returns([]);
        _ledger.RecordToolCallAsync(Arg.Any<ToolCallRecord>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _sessionResolver.ResolveAsync(Arg.Any<SessionResolveOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<ContextSession, Error>.Fail(new Error("NONE", "no session")));
    }

    private Task<CallToolResult> Execute(string query, string? kind = null) =>
        HypaSearchTool.ExecuteAsync(
            MakeService(), CancellationToken.None, query, null, kind);

    private SearchService MakeService() =>
        new(_fileSystem, _rootDetector, _codeIndex, _ledger, _sessionResolver, _logger);

    private static string TextOf(CallToolResult r) =>
        string.Concat(r.Content.OfType<TextContentBlock>().Select(c => c.Text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyQuery_ReturnsError_WithIsErrorTrue(string query)
    {
        var result = await Execute(query);
        Assert.True(result.IsError);
        Assert.Contains("query is required", TextOf(result));
    }

    [Fact]
    public async Task InvalidRegex_ReturnsError_WithIsErrorTrue()
    {
        var result = await Execute("[unclosed", kind: "regex");
        Assert.True(result.IsError);
        Assert.Contains("invalid regex", TextOf(result));
    }

    [Fact]
    public async Task TextSearch_NoMatches_ReturnsSuccess_NotError()
    {
        var result = await Execute("sometoken", kind: "text");
        Assert.True(result.IsError is not true);
        Assert.Contains("No matches", TextOf(result));
    }

    [Fact]
    public async Task TextSearch_WithMatch_ReturnsReference()
    {
        var tmp = System.IO.Path.GetTempFileName();
        _tempFiles.Add(tmp);
        System.IO.File.WriteAllText(tmp, "hello world line\n");
        var dir = System.IO.Path.GetDirectoryName(tmp)!;
        _rootDetector.Detect(Arg.Any<string>()).Returns(dir);
        _fileSystem.GetFiles(dir, "*.*", recursive: true).Returns([tmp]);
        _fileSystem.ReadAllBytes(tmp).Returns(System.Text.Encoding.UTF8.GetBytes("hello world line\n"));

        try
        {
            var result = await HypaSearchTool.ExecuteAsync(
                MakeService(), CancellationToken.None, "hello");
            Assert.True(result.IsError is not true);
            Assert.Contains("hello", TextOf(result));
        }
        catch (Exception ex)
        {
            Assert.Fail($"Expected no exception, but got: {ex}");
        }
    }

    [Fact]
    public async Task SymbolSearch_NoResults_ReturnsSuccess_NotError()
    {
        _codeIndex.QuerySymbolsAsync(Arg.Any<CodeSymbolQuery>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await Execute("MyClass", kind: "symbol");
        Assert.True(result.IsError is not true);
        Assert.Contains("No symbols", TextOf(result));
    }
}
