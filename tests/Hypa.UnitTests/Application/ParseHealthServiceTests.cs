using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Metrics;
using Hypa.Runtime.Domain.Parsers;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Application;

public sealed class ParseHealthServiceTests
{
    private static ParseHealthService Make(IReadOnlyList<ParseMetricsRecord> records)
    {
        var repo = Substitute.For<IParseMetricsRepository>();
        repo.QueryAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(records);
        return new ParseHealthService(repo);
    }

    [Fact]
    public async Task GetReportAsync_ReturnsEmpty_WhenNoRecords()
    {
        var service = Make([]);
        var result = await service.GetReportAsync(CancellationToken.None);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetReportAsync_GroupsByTier()
    {
        var records = new[]
        {
            Record("dotnet", ParseTier.Passthrough),
            Record("dotnet", ParseTier.Passthrough),
            Record("dotnet", ParseTier.Full),
        };
        var service = Make(records);
        var result = await service.GetReportAsync(CancellationToken.None);
        Assert.Equal(2, result.Count);
        var passthrough = result.First(r => r.Tier == ParseTier.Passthrough);
        var full = result.First(r => r.Tier == ParseTier.Full);
        Assert.Equal(2, passthrough.Count);
        Assert.Equal(1, full.Count);
    }

    [Fact]
    public async Task GetReportAsync_ComputesDegradedRate()
    {
        var records = new[]
        {
            Record("tsc", ParseTier.Full),
            Record("tsc", ParseTier.Degraded),
            Record("tsc", ParseTier.Degraded),
            Record("tsc", ParseTier.Degraded),
        };
        var service = Make(records);
        var result = await service.GetReportAsync(CancellationToken.None);
        var degraded = result.First(r => r.Tier == ParseTier.Degraded);
        Assert.Equal(75.0, degraded.Pct);
    }

    [Fact]
    public async Task GetReportAsync_SegregatesByExecutable()
    {
        var records = new[]
        {
            Record("dotnet", ParseTier.Passthrough),
            Record("git", ParseTier.Passthrough),
        };
        var service = Make(records);
        var result = await service.GetReportAsync(CancellationToken.None);
        Assert.Contains(result, r => r.Executable == "dotnet");
        Assert.Contains(result, r => r.Executable == "git");
    }

    private static ParseMetricsRecord Record(string executable, ParseTier tier) =>
        new ParseMetricsRecord
        {
            RunId = Guid.NewGuid().ToString("N"),
            Executable = executable,
            Arguments = string.Empty,
            ParseTier = tier,
            RecordedAt = DateTimeOffset.UtcNow,
        };
}
