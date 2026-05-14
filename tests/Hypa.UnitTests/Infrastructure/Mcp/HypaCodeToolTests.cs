using Hypa.Infrastructure.CodeIntelligence;
using Hypa.Infrastructure.Mcp;
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

public sealed class HypaCodeToolTests
{
    private readonly IFileSystem _fileSystem = Substitute.For<IFileSystem>();
    private readonly IProjectRootDetector _rootDetector = Substitute.For<IProjectRootDetector>();
    private readonly CodeStructureProviderRegistry _registry = new([new RegexFallbackCodeStructureProvider()]);
    private readonly ICodeIndexRepository _codeIndex = Substitute.For<ICodeIndexRepository>();
    private readonly IEvidenceLedger _ledger = Substitute.For<IEvidenceLedger>();
    private readonly ISessionResolver _sessionResolver = Substitute.For<ISessionResolver>();
    private readonly McpRuntimeOptions _options = new();
    private static readonly NullLogger<HypaCodeTool> _logger = NullLogger<HypaCodeTool>.Instance;

    public HypaCodeToolTests()
    {
        _rootDetector.Detect(Arg.Any<string>()).Returns("/project");
        _fileSystem.GetFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>()).Returns([]);
        _ledger.RecordToolCallAsync(Arg.Any<ToolCallRecord>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _sessionResolver.ResolveAsync(Arg.Any<SessionResolveOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<ContextSession, Error>.Fail(new Error("NONE", "no session")));
        _codeIndex.QuerySymbolsAsync(Arg.Any<CodeSymbolQuery>(), Arg.Any<CancellationToken>()).Returns([]);
        _codeIndex.QueryGraphAsync(Arg.Any<CodeGraphQuery>(), Arg.Any<CancellationToken>())
            .Returns(new CodeGraphResult());
        _codeIndex.QueryDiagnosticsAsync(Arg.Any<CancellationToken>()).Returns([]);
    }

    private Task<CallToolResult> Execute(string action, string? path = null, string? symbol = null) =>
        HypaCodeTool.ExecuteAsync(
            _fileSystem, _rootDetector, _registry, _codeIndex, _ledger, _sessionResolver, _logger, _options,
            CancellationToken.None, action, path, symbol);

    private static string TextOf(CallToolResult r) =>
        string.Concat(r.Content.OfType<TextContentBlock>().Select(c => c.Text));

    [Fact]
    public async Task ReadOnly_IndexAction_ReturnsError_WithIsErrorTrue()
    {
        _options.ReadOnly = true;
        var result = await Execute("index");
        Assert.True(result.IsError);
        Assert.Contains("Read-only mode", TextOf(result));
    }

    [Fact]
    public async Task UnknownAction_ReturnsError_WithIsErrorTrue()
    {
        var result = await Execute("frobulate");
        Assert.True(result.IsError);
        Assert.Contains("Unknown action", TextOf(result));
    }

    [Fact]
    public async Task Index_PathEscapesRoot_ReturnsError_WithIsErrorTrue()
    {
        var result = await Execute("index", path: "../../etc");
        Assert.True(result.IsError);
        Assert.Contains("path escapes", TextOf(result));
    }

    [Fact]
    public async Task Symbols_NoResults_ReturnsSuccess_NotError()
    {
        var result = await Execute("symbols");
        Assert.True(result.IsError is not true);
        Assert.Contains("No symbols", TextOf(result));
    }

    [Fact]
    public async Task Diagnostics_NoResults_ReturnsSuccess_NotError()
    {
        var result = await Execute("diagnostics");
        Assert.True(result.IsError is not true);
        Assert.Contains("No diagnostics", TextOf(result));
    }

    [Fact]
    public async Task Index_WithNoFiles_ReturnsSuccess()
    {
        _codeIndex.SaveDocumentsAsync(Arg.Any<IReadOnlyList<Hypa.Sdk.CodeIntelligence.CodeStructureDocument>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var result = await Execute("index");
        Assert.True(result.IsError is not true);
        Assert.Contains("Indexed", TextOf(result));
    }
}
