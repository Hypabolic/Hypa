using Hypa.Infrastructure.DI;
using Hypa.Infrastructure.Storage;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Config;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Storage;

public sealed class HypaDataOptionsTests
{
    // Exercises the actual factory registered in InfrastructureServiceExtensions.
    // Adding the fake IConfigLoader after AddInfrastructure() makes it the last
    // registration, so GetRequiredService<IConfigLoader>() inside the factory returns it.
    private static HypaDataOptions Resolve(IConfigLoader loader)
    {
        var services = new ServiceCollection();
        services.AddInfrastructure();
        services.AddSingleton<IConfigLoader>(loader);
        using var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<HypaDataOptions>();
    }

    [Fact]
    public void HypaDataOptions_UsesDefaultStoragePath_WhenNoConfigOverride()
    {
        var loader = Substitute.For<IConfigLoader>();
        loader.LoadAsync(Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(Result<HypaConfig, Error>.Ok(HypaConfig.Default)));

        var options = Resolve(loader);

        Assert.Equal(HypaConfig.Default.StoragePath, options.DataDirectory);
        Assert.EndsWith(".hypa", options.DataDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HypaDataOptions_UsesCustomStoragePath_WhenConfigOverridePresent()
    {
        var customPath = Path.Combine(Path.GetTempPath(), $"hypa-custom-{Guid.NewGuid():N}");
        var loader = Substitute.For<IConfigLoader>();
        loader.LoadAsync(Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(Result<HypaConfig, Error>.Ok(
                  HypaConfig.Default with { StoragePath = customPath })));

        var options = Resolve(loader);

        Assert.Equal(customPath, options.DataDirectory);
    }

    [Fact]
    public void HypaDataOptions_UsesHypaStoragePathEnvironmentOverride()
    {
        var envPath = Path.Combine(Path.GetTempPath(), $"hypa-env-{Guid.NewGuid():N}");
        var configPath = Path.Combine(Path.GetTempPath(), $"hypa-config-{Guid.NewGuid():N}");
        var previousValue = Environment.GetEnvironmentVariable("HYPA_STORAGE_PATH");
        var loader = Substitute.For<IConfigLoader>();
        loader.LoadAsync(Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(Result<HypaConfig, Error>.Ok(
                  HypaConfig.Default with { StoragePath = configPath })));

        try
        {
            Environment.SetEnvironmentVariable("HYPA_STORAGE_PATH", envPath);

            var options = Resolve(loader);

            Assert.Equal(envPath, options.DataDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYPA_STORAGE_PATH", previousValue);
        }
    }

    [Fact]
    public void HypaDataOptions_UsesDefaultStoragePath_WhenConfigLoadFails()
    {
        var loader = Substitute.For<IConfigLoader>();
        loader.LoadAsync(Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(
                  Result<HypaConfig, Error>.Fail(new Error("CONFIG_LOAD_FAILED", "disk error"))));

        var options = Resolve(loader);

        Assert.Equal(HypaConfig.Default.StoragePath, options.DataDirectory);
    }

    [Fact]
    public void HypaDataOptions_UsesDefaultStoragePath_WhenConfigLoaderThrows()
    {
        var loader = Substitute.For<IConfigLoader>();
        loader.LoadAsync(Arg.Any<CancellationToken>())
              .Returns<Task<Result<HypaConfig, Error>>>(_ =>
                  throw new InvalidOperationException("storage unavailable"));

        var options = Resolve(loader);

        Assert.Equal(HypaConfig.Default.StoragePath, options.DataDirectory);
    }

    [Fact]
    public void HypaDataDirectoryResolver_FallsBackToProjectLocalData_WhenDefaultPathIsNotWritable()
    {
        var rootDetector = Substitute.For<IProjectRootDetector>();
        rootDetector.Detect(Arg.Any<string>()).Returns("/repo");

        var resolved = HypaDataDirectoryResolver.Resolve(
            HypaConfig.Default.StoragePath,
            isExplicit: false,
            rootDetector,
            _ => false);

        Assert.Equal(Path.Combine("/repo", ".hypa", "data"), resolved);
    }

    [Fact]
    public void HypaDataDirectoryResolver_KeepsDefaultPath_WhenWritable()
    {
        var rootDetector = Substitute.For<IProjectRootDetector>();

        var resolved = HypaDataDirectoryResolver.Resolve(
            HypaConfig.Default.StoragePath,
            isExplicit: false,
            rootDetector,
            _ => true);

        Assert.Equal(HypaConfig.Default.StoragePath, resolved);
    }

    [Fact]
    public void HypaDataDirectoryResolver_KeepsExplicitPath_WhenNotWritable()
    {
        var rootDetector = Substitute.For<IProjectRootDetector>();

        var resolved = HypaDataDirectoryResolver.Resolve(
            "/custom/hypa",
            isExplicit: true,
            rootDetector,
            _ => false);

        Assert.Equal("/custom/hypa", resolved);
    }

    [Fact]
    public void HypaDataOptions_DatabasePath_DerivedFromDataDirectory()
    {
        var customPath = "/some/custom/path";
        var loader = Substitute.For<IConfigLoader>();
        loader.LoadAsync(Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(Result<HypaConfig, Error>.Ok(
                  HypaConfig.Default with { StoragePath = customPath })));

        var options = Resolve(loader);

        Assert.Equal(Path.Combine(customPath, "hypa.db"), options.DatabasePath);
    }

    [Fact]
    public void HypaDataOptions_ArtifactsDirectory_DerivedFromDataDirectory()
    {
        var customPath = "/some/custom/path";
        var loader = Substitute.For<IConfigLoader>();
        loader.LoadAsync(Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(Result<HypaConfig, Error>.Ok(
                  HypaConfig.Default with { StoragePath = customPath })));

        var options = Resolve(loader);

        Assert.Equal(Path.Combine(customPath, "artifacts"), options.ArtifactsDirectory);
    }
}
