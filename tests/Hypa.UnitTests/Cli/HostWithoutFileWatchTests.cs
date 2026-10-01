using Hypa.AgentServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The default host reads appsettings.json with reloadOnChange, which starts a
/// FileSystemWatcher. On macOS, FSEvents cannot start when the sandbox denies
/// mach-lookup (brew test, agent sandboxes). The change token then fires again at
/// once and re-registers itself without end, so `hypa --version` spins forever.
/// Hypa has no appsettings.json, so the host must not watch files.
/// </summary>
public sealed class HostWithoutFileWatchTests
{
    private static IEnumerable<FileConfigurationSource> FileSources(IConfiguration configuration) =>
        ((IConfigurationRoot)configuration).Providers
            .OfType<FileConfigurationProvider>()
            .Select(p => p.Source);

    [Fact]
    public void DefaultBuilder_DoesNotReloadConfigurationOnChange()
    {
        using var host = HostWithoutFileWatch.CreateDefaultBuilder().Build();

        var sources = FileSources(host.Services.GetService(typeof(IConfiguration)) as IConfiguration
            ?? throw new InvalidOperationException("No IConfiguration."));

        Assert.NotEmpty(sources);
        Assert.All(sources, s => Assert.False(s.ReloadOnChange));
    }

    [Fact]
    public void ApplicationBuilder_DoesNotReloadConfigurationOnChange()
    {
        var builder = HostWithoutFileWatch.CreateApplicationBuilder([]);

        var sources = FileSources(builder.Configuration);

        Assert.NotEmpty(sources);
        Assert.All(sources, s => Assert.False(s.ReloadOnChange));
    }
}
