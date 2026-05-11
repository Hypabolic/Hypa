using Hypa.Infrastructure.Parsers;
using Hypa.Runtime.Domain.Parsers;
using Hypa.Runtime.Domain.Runner;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Parsers;

public sealed class TscParserTests
{
    private static readonly TscParser Parser = new();

    private static readonly CommandInvocation TscInvocation =
        CommandInvocation.Buffered("tsc", ["--noEmit"], "tsc --noEmit");

    private static readonly CommandInvocation NpxTscInvocation =
        CommandInvocation.Buffered("npx", ["tsc", "--noEmit"], "npx tsc --noEmit");

    [Fact]
    public void CanParse_Tsc_ReturnsTrue() =>
        Assert.True(Parser.CanParse(TscInvocation));

    [Fact]
    public void CanParse_NpxTsc_ReturnsTrue() =>
        Assert.True(Parser.CanParse(NpxTscInvocation));

    [Fact]
    public void CanParse_DotnetBuild_ReturnsFalse() =>
        Assert.False(Parser.CanParse(CommandInvocation.Buffered("dotnet", ["build"], "dotnet build")));

    [Fact]
    public void TryParse_WithErrors_SetsTierFull()
    {
        var output = CommandOutput.Captured(TscOutput_WithErrors, "", 1, TimeSpan.Zero);
        var result = Parser.TryParse(output);
        Assert.True(result.Matched);
        Assert.Equal(ParseTier.Full, result.Tier);
    }

    [Fact]
    public void TryParse_CapturesDiagnosticCount()
    {
        var output = CommandOutput.Captured(TscOutput_WithErrors, "", 1, TimeSpan.Zero);
        var result = Parser.TryParse(output);
        Assert.NotNull(result.Value);
        Assert.Equal(2, result.Value!.ErrorCount);
    }

    [Fact]
    public void TryParse_CapturesFileAndCode()
    {
        var output = CommandOutput.Captured(TscOutput_WithErrors, "", 1, TimeSpan.Zero);
        var result = Parser.TryParse(output);
        Assert.NotNull(result.Value);
        var first = result.Value!.Diagnostics[0];
        Assert.Equal("TS2304", first.Code);
        Assert.Equal("src/app.ts", first.File);
    }

    [Fact]
    public void TryParse_NoErrorsOrSummary_SetsDegradedTier()
    {
        var output = CommandOutput.Captured("some other output", "", 0, TimeSpan.Zero);
        var result = Parser.TryParse(output);
        Assert.False(result.Matched);
        Assert.Equal(ParseTier.Degraded, result.Tier);
    }

    private const string TscOutput_WithErrors = """
        src/app.ts(10,5): error TS2304: Cannot find name 'foo'.
        src/util.ts(5,3): error TS2304: Cannot find name 'bar'.
        Found 2 errors.
        """;
}
