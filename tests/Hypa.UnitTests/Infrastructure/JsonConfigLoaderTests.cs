using System.Text.Json;
using Hypa.Infrastructure.Config;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Config;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Infrastructure;

public sealed class JsonConfigLoaderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");
    private readonly IProjectRootDetector _noRootDetector;

    public JsonConfigLoaderTests()
    {
        Directory.CreateDirectory(_tempDir);
        _noRootDetector = Substitute.For<IProjectRootDetector>();
        _noRootDetector.Detect(Arg.Any<string>()).Returns((string?)null);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public async Task LoadAsync_NoFiles_ReturnsDefaults()
    {
        var loader = new JsonConfigLoader(_noRootDetector, _tempDir);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(HypaConfig.Default.Enabled, result.Value.Enabled);
        Assert.Equal(HypaConfig.Default.LogLevel, result.Value.LogLevel);
    }

    [Fact]
    public async Task LoadAsync_UserConfig_OverridesDefaults()
    {
        WriteJson(Path.Combine(_tempDir, "config.json"), new { enabled = false, storage_path = "/custom" });

        var loader = new JsonConfigLoader(_noRootDetector, _tempDir);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.False(result.Value.Enabled);
        Assert.Equal("/custom", result.Value.StoragePath);
    }

    [Fact]
    public async Task LoadAsync_ProjectConfig_OverridesUserConfig()
    {
        WriteJson(Path.Combine(_tempDir, "config.json"), new { storage_path = "/user" });

        var projectRoot = Path.Combine(_tempDir, "project");
        var projectHypaDir = Path.Combine(projectRoot, ".hypa");
        Directory.CreateDirectory(projectHypaDir);
        WriteJson(Path.Combine(projectHypaDir, "config.json"), new { storage_path = "/project" });

        var detector = Substitute.For<IProjectRootDetector>();
        detector.Detect(Arg.Any<string>()).Returns(projectRoot);

        var loader = new JsonConfigLoader(detector, _tempDir);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal("/project", result.Value.StoragePath);
    }

    [Fact]
    public async Task LoadAsync_EnvVar_OverridesFileConfig()
    {
        WriteJson(Path.Combine(_tempDir, "config.json"), new { storage_path = "/from-file" });

        Environment.SetEnvironmentVariable("HYPA_STORAGE_PATH", "/from-env");
        try
        {
            var loader = new JsonConfigLoader(_noRootDetector, _tempDir);
            var result = await loader.LoadAsync(CancellationToken.None);

            Assert.True(result.IsOk);
            Assert.Equal("/from-env", result.Value.StoragePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYPA_STORAGE_PATH", null);
        }
    }

    [Fact]
    public async Task LoadAsync_LogLevelEnvVar_Parsed()
    {
        Environment.SetEnvironmentVariable("HYPA_LOG_LEVEL", "Debug");
        try
        {
            var loader = new JsonConfigLoader(_noRootDetector, _tempDir);
            var result = await loader.LoadAsync(CancellationToken.None);

            Assert.True(result.IsOk);
            Assert.Equal(LogLevel.Debug, result.Value.LogLevel);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HYPA_LOG_LEVEL", null);
        }
    }

    [Fact]
    public async Task LoadAsync_UpdateCheckEnabled_BindsFalse()
    {
        WriteJson(Path.Combine(_tempDir, "config.json"), new { update_check_enabled = false });

        var loader = new JsonConfigLoader(_noRootDetector, _tempDir);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.False(result.Value.UpdateCheckEnabled);
    }

    [Fact]
    public async Task LoadAsync_UpdateChannel_BindsCustomValue()
    {
        WriteJson(Path.Combine(_tempDir, "config.json"), new { update_channel = "nightly" });

        var loader = new JsonConfigLoader(_noRootDetector, _tempDir);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal("nightly", result.Value.UpdateChannel);
    }

    [Fact]
    public async Task LoadAsync_ReleaseRepository_BindsCustomRepo()
    {
        WriteJson(Path.Combine(_tempDir, "config.json"), new { release_repository = "my-org/my-fork" });

        var loader = new JsonConfigLoader(_noRootDetector, _tempDir);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal("my-org/my-fork", result.Value.ReleaseRepository);
    }

    [Fact]
    public async Task LoadAsync_NoFiles_UpdateDefaults_AreCorrect()
    {
        var loader = new JsonConfigLoader(_noRootDetector, _tempDir);
        var result = await loader.LoadAsync(CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.True(result.Value.UpdateCheckEnabled);
        Assert.Equal("stable", result.Value.UpdateChannel);
        Assert.Equal("Hypabolic/Hypa", result.Value.ReleaseRepository);
    }

    private static void WriteJson(string path, object obj)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(obj));
    }
}
