using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Config;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Application;

public sealed class ConfigServiceTests
{
    [Fact]
    public async Task GetConfigAsync_DelegatesToLoader()
    {
        var expected = HypaConfig.Default with { Enabled = false };
        var loader = Substitute.For<IConfigLoader>();
        loader.LoadAsync(Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(Result<HypaConfig, Error>.Ok(expected)));

        var service = new ConfigService(loader);
        var result = await service.GetConfigAsync(CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public async Task GetConfigAsync_PropagatesFailure()
    {
        var error = new Error("CONFIG_MISSING", "No config found.");
        var loader = Substitute.For<IConfigLoader>();
        loader.LoadAsync(Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(Result<HypaConfig, Error>.Fail(error)));

        var service = new ConfigService(loader);
        var result = await service.GetConfigAsync(CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Equal("CONFIG_MISSING", result.Error.Code);
    }
}
