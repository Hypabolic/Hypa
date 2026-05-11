using Hypa.Infrastructure.Parsers;
using Hypa.Runtime.Domain.Parsers;
using Hypa.Runtime.Domain.Runner;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Parsers;

public sealed class DotnetBuildParserTests
{
    private static readonly DotnetBuildParser Parser = new();

    private static readonly CommandInvocation BuildInvocation =
        CommandInvocation.Buffered("dotnet", ["build"], "dotnet build");

    private static readonly CommandInvocation TestInvocation =
        CommandInvocation.Buffered("dotnet", ["test"], "dotnet test");

    [Fact]
    public void CanParse_DotnetBuild_ReturnsTrue() =>
        Assert.True(Parser.CanParse(BuildInvocation));

    [Fact]
    public void CanParse_DotnetTest_ReturnsFalse() =>
        Assert.False(Parser.CanParse(TestInvocation));

    [Fact]
    public void TryParse_WithErrors_SetsTierFull()
    {
        var output = CommandOutput.Captured(BuildOutput_WithErrors, "", 1, TimeSpan.Zero);
        var result = Parser.TryParse(output);
        Assert.True(result.Matched);
        Assert.Equal(ParseTier.Full, result.Tier);
    }

    [Fact]
    public void TryParse_CapturesErrorDiagnostics()
    {
        var output = CommandOutput.Captured(BuildOutput_WithErrors, "", 1, TimeSpan.Zero);
        var result = Parser.TryParse(output);
        Assert.NotNull(result.Value);
        Assert.Single(result.Value!.Errors);
        Assert.Equal("CS0103", result.Value.Errors[0].Code);
        Assert.Equal("error", result.Value.Errors[0].Severity);
    }

    [Fact]
    public void TryParse_SuccessBuild_SetsSucceededTrue()
    {
        var output = CommandOutput.Captured(BuildOutput_Success, "", 0, TimeSpan.Zero);
        var result = Parser.TryParse(output);
        Assert.True(result.Matched);
        Assert.True(result.Value!.Succeeded);
        Assert.Empty(result.Value.Errors);
    }

    [Fact]
    public void TryParse_MissingBuildResult_SetsDegradedTier()
    {
        var output = CommandOutput.Captured("random build noise", "", 0, TimeSpan.Zero);
        var result = Parser.TryParse(output);
        Assert.False(result.Matched);
        Assert.Equal(ParseTier.Degraded, result.Tier);
    }

    private const string BuildOutput_WithErrors = """
        MyProject/File.cs(10,5): error CS0103: The name 'foo' does not exist in the current context
        Build FAILED.
            1 Error(s)
            0 Warning(s)
        Time Elapsed 00:00:02.34
        """;

    private const string BuildOutput_Success = """
          MyProject -> /bin/Debug/net10.0/MyProject.dll
        Build succeeded.
            0 Error(s)
            0 Warning(s)
        Time Elapsed 00:00:01.12
        """;
}
