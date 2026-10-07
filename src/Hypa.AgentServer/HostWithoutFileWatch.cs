using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Hypa.AgentServer;

/// <summary>
/// Generic host builders that do not watch configuration files.
/// </summary>
/// <remarks>
/// The default host reads appsettings.json with reloadOnChange, which starts a
/// FileSystemWatcher. On macOS that needs FSEvents, which cannot start when a
/// sandbox denies mach-lookup (brew test, agent sandboxes). The change token then
/// fires again at once and re-registers itself without end, so the process spins
/// at startup. Hypa ships no appsettings.json and loads its own config files
/// without watching them, so the watcher has nothing to do.
/// </remarks>
public static class HostWithoutFileWatch
{
    private const string ReloadConfigOnChangeKey = "hostBuilder:reloadConfigOnChange";

    public static IHostBuilder CreateDefaultBuilder() =>
        Host.CreateDefaultBuilder()
            .ConfigureHostConfiguration(config => config.AddInMemoryCollection(NoReload()));

    public static HostApplicationBuilder CreateApplicationBuilder(string[]? args = null)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(NoReload());
        return Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            Configuration = configuration,
        });
    }

    private static IEnumerable<KeyValuePair<string, string?>> NoReload() =>
        [new KeyValuePair<string, string?>(ReloadConfigOnChangeKey, "false")];
}
