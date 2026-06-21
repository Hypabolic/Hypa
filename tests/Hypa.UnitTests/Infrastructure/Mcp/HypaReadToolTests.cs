using Hypa.Infrastructure.CodeIntelligence;
using Hypa.Infrastructure.Mcp.Tools;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Mcp;

public sealed class HypaReadToolTests : IDisposable
{
    private readonly IFileSystem _fileSystem = Substitute.For<IFileSystem>();
    private readonly IProjectRootDetector _rootDetector = Substitute.For<IProjectRootDetector>();
    private readonly CodeStructureProviderRegistry _registry = new([new RegexFallbackCodeStructureProvider()]);
    private readonly IEvidenceLedger _ledger = Substitute.For<IEvidenceLedger>();
    private readonly ISessionResolver _sessionResolver = Substitute.For<ISessionResolver>();
    private readonly ITokenCounter _tokenCounter = Substitute.For<ITokenCounter>();
    private static readonly NullLogger<FileReadService> _logger = NullLogger<FileReadService>.Instance;
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var path in _tempFiles)
            try { File.Delete(path); } catch { /* best-effort */ }
    }

    public HypaReadToolTests()
    {
        _rootDetector.Detect(Arg.Any<string>()).Returns("/project");
        _ledger.RecordToolCallAsync(Arg.Any<ToolCallRecord>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _sessionResolver.ResolveAsync(Arg.Any<SessionResolveOptions>(), Arg.Any<CancellationToken>())
            .Returns(Result<ContextSession, Error>.Fail(new Error("NONE", "no session")));
        _tokenCounter.EstimateTokens(Arg.Any<string>()).Returns(10);
    }

    private Task<CallToolResult> Execute(string path, string? mode = null) =>
        HypaReadTool.ExecuteAsync(
            MakeService(), CancellationToken.None, path, mode);

    private FileReadService MakeService() =>
        new(_fileSystem, _rootDetector, _registry, _ledger, _sessionResolver, _tokenCounter, _logger);

    private static string TextOf(CallToolResult r) =>
        string.Concat(r.Content.OfType<TextContentBlock>().Select(c => c.Text));

    [Fact]
    public async Task EmptyPath_ReturnsError_WithIsErrorTrue()
    {
        var result = await Execute("   ");
        Assert.True(result.IsError);
        Assert.Contains("path is required", TextOf(result));
    }

    [Fact]
    public async Task PathEscapesRoot_ReturnsError_WithIsErrorTrue()
    {
        // "../secret" resolves outside /project
        var result = await Execute("../../etc/passwd");
        Assert.True(result.IsError);
        Assert.Contains("path escapes", TextOf(result));
    }

    [Fact]
    public async Task FileNotFound_ReturnsError_WithIsErrorTrue()
    {
        // Path is inside root but doesn't exist on disk
        var result = await Execute("/project/missing.cs");
        Assert.True(result.IsError);
        Assert.Contains("file not found", TextOf(result));
    }

    [Fact]
    public async Task ValidFile_ReturnsSuccess_NotError()
    {
        var tmpFile = System.IO.Path.GetTempFileName();
        _tempFiles.Add(tmpFile);
        System.IO.File.WriteAllText(tmpFile, "namespace Foo { }");
        _rootDetector.Detect(Arg.Any<string>()).Returns(System.IO.Path.GetDirectoryName(tmpFile)!);
        _fileSystem.FileExists(tmpFile).Returns(true);
        _fileSystem.ReadAllBytes(tmpFile).Returns(System.IO.File.ReadAllBytes(tmpFile));

        try
        {
            var result = await HypaReadTool.ExecuteAsync(
                MakeService(), CancellationToken.None, tmpFile, "full");

            Assert.True(result.IsError is not true);
            var text = TextOf(result);
            Assert.Contains("SUMMARY", text);
        }
        catch (Exception ex)
        {
            Assert.Fail($"Expected no exception, but got: {ex}");
        }
    }

    [Fact]
    public async Task ReadError_ReturnsError_WithIsErrorTrue()
    {
        var tmpFile = System.IO.Path.GetTempFileName();
        _tempFiles.Add(tmpFile);
        _rootDetector.Detect(Arg.Any<string>()).Returns(System.IO.Path.GetDirectoryName(tmpFile)!);
        _fileSystem.FileExists(tmpFile).Returns(true);
        _fileSystem.ReadAllBytes(tmpFile).Returns(_ => throw new System.IO.IOException("disk error"));

        try
        {
            var result = await HypaReadTool.ExecuteAsync(
                MakeService(), CancellationToken.None, tmpFile, "full");
            Assert.True(result.IsError);
            Assert.Contains("Error reading file", TextOf(result));
        }
        catch (Exception ex)
        {
            Assert.Fail($"Expected no exception, but got: {ex}");
        }
    }
}
