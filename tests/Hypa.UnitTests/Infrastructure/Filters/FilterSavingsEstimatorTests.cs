using Hypa.Infrastructure.Compression;
using Hypa.Infrastructure.Filters;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Filters;

public sealed class FilterSavingsEstimatorTests
{
    [Fact]
    public void EstimateAll_ProducesSavingsForBuiltInFilters()
    {
        var estimator = new FilterSavingsEstimator(new FilterEngine(), new TiktokenTokenCounter());

        var estimates = estimator.EstimateAll(BuiltInFilters.All);

        Assert.NotEmpty(estimates);
        Assert.Contains(estimates, e => e.FilterId == "dotnet-msbuild-noise" && e.SavedTokens > 0);
        Assert.Contains(estimates, e => e.FilterId == "kubectl-logs" && e.SavedPercent > 0);
        Assert.All(estimates, e =>
        {
            Assert.True(e.OriginalTokens > 0);
            Assert.True(e.CompressedTokens > 0);
        });
    }

    [Fact]
    public void Estimate_UsesSyntheticPayloadAndConfiguredTokenizer()
    {
        var estimator = new FilterSavingsEstimator(new FilterEngine(), new TiktokenTokenCounter());
        var filter = BuiltInFilters.All.Single(f => f.Id == "ping");

        var estimate = estimator.Estimate(filter);

        Assert.Equal("ping", estimate.FilterId);
        Assert.Equal("synthetic", estimate.SampleKind);
        Assert.True(estimate.SavedTokens > 0);
        Assert.True(estimate.CompressedBytes < estimate.OriginalBytes);
    }
}
