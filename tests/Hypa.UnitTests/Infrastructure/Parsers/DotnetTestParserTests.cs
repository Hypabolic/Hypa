using Hypa.Infrastructure.Parsers;
using Hypa.Runtime.Domain.Parsers;
using Hypa.Runtime.Domain.Runner;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Parsers;

public sealed class DotnetTestParserTests
{
    private static readonly DotnetTestParser Parser = new();

    private static readonly CommandInvocation DotnetTestInvocation =
        CommandInvocation.Buffered("dotnet", ["test"], "dotnet test");

    private static readonly CommandInvocation DotnetBuildInvocation =
        CommandInvocation.Buffered("dotnet", ["build"], "dotnet build");

    [Fact]
    public void CanParse_DotnetTest_ReturnsTrue() =>
        Assert.True(Parser.CanParse(DotnetTestInvocation));

    [Fact]
    public void CanParse_DotnetBuild_ReturnsFalse() =>
        Assert.False(Parser.CanParse(DotnetBuildInvocation));

    [Fact]
    public void CanParse_NoArguments_ReturnsFalse() =>
        Assert.False(Parser.CanParse(CommandInvocation.Buffered("dotnet", [], "dotnet")));

    [Fact]
    public void TryParse_FailingTests_SetsTierFull()
    {
        var output = CommandOutput.Captured(DotnetTestOutput_WithFailures, "", 1, TimeSpan.Zero);
        var result = Parser.TryParse(output);
        Assert.True(result.Matched);
        Assert.Equal(ParseTier.Full, result.Tier);
    }

    [Fact]
    public void TryParse_CapturesPassFailCounts()
    {
        var output = CommandOutput.Captured(DotnetTestOutput_WithFailures, "", 1, TimeSpan.Zero);
        var result = Parser.TryParse(output);
        Assert.NotNull(result.Value);
        Assert.Equal(2, result.Value!.Passed);
        Assert.Equal(1, result.Value.Failed);
    }

    [Fact]
    public void TryParse_CapturesFailingTestNames()
    {
        var output = CommandOutput.Captured(DotnetTestOutput_WithFailures, "", 1, TimeSpan.Zero);
        var result = Parser.TryParse(output);
        Assert.NotNull(result.Value);
        Assert.Single(result.Value!.FailingTests);
        Assert.Contains("MyTest", result.Value.FailingTests[0].Name);
    }

    [Fact]
    public void TryParse_UnrecognisedFormat_SetsDegradedTier()
    {
        var output = CommandOutput.Captured("some random output with no structure", "", 0, TimeSpan.Zero);
        var result = Parser.TryParse(output);
        Assert.False(result.Matched);
        Assert.Equal(ParseTier.Degraded, result.Tier);
    }

    [Fact]
    public void TryParse_AllPassing_HasZeroFailingTests()
    {
        var output = CommandOutput.Captured(DotnetTestOutput_AllPassing, "", 0, TimeSpan.Zero);
        var result = Parser.TryParse(output);
        Assert.True(result.Matched);
        Assert.Empty(result.Value!.FailingTests);
        Assert.Equal(3, result.Value.Passed);
    }

    private const string DotnetTestOutput_WithFailures = """
        Test run for MyProject.dll
          Failed MyTest [12 ms]
            Expected: 42
            Actual: 0
            Stack Trace:
              at MyTest() in File.cs:line 10

        Failed!  - Failed: 1, Passed: 2, Skipped: 0, Total: 3
          Total: 3
          Passed: 2
          Failed: 1
          Duration: 1.2 s
        """;

    private const string DotnetTestOutput_AllPassing = """
        Test run for MyProject.dll
        Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3
          Total: 3
          Passed: 3
          Failed: 0
          Duration: 0.5 s
        """;
}
