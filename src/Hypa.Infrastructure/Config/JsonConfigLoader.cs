using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Hypa.Infrastructure.Config;

public sealed class JsonConfigLoader : IConfigLoader
{
    private readonly IProjectRootDetector _rootDetector;
    private readonly string _userConfigDir;

    public JsonConfigLoader(IProjectRootDetector rootDetector)
        : this(rootDetector, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hypa"))
    { }

    internal JsonConfigLoader(IProjectRootDetector rootDetector, string userConfigDir)
    {
        _rootDetector = rootDetector;
        _userConfigDir = userConfigDir;
    }

    public Task<Result<HypaConfig, Error>> LoadAsync(CancellationToken ct)
    {
        var userConfigPath = Path.Combine(_userConfigDir, "config.json");
        var projectRoot = _rootDetector.Detect(Directory.GetCurrentDirectory());
        var projectConfigPath = projectRoot is not null
            ? Path.Combine(projectRoot, ".hypa", "config.json")
            : null;

        var builder = new ConfigurationBuilder();
        builder.AddJsonFile(userConfigPath, optional: true);
        if (projectConfigPath is not null)
            builder.AddJsonFile(projectConfigPath, optional: true);
        builder.AddEnvironmentVariables("HYPA_");

        var configuration = builder.Build();
        var config = BindConfig(configuration);

        return Task.FromResult(Result<HypaConfig, Error>.Ok(config));
    }

    private static HypaConfig BindConfig(IConfiguration cfg)
    {
        var defaults = HypaConfig.Default;

        var enabled = cfg["enabled"];
        var storagePath = cfg["storage_path"];
        var logLevel = cfg["log_level"];

        var excludeChildren = cfg.GetSection("exclude_commands").GetChildren().ToArray();
        var excludeCommands = excludeChildren.Length > 0
            ? excludeChildren.Select(c => c.Value ?? string.Empty).ToArray()
            : defaults.ExcludeCommands;

        return new HypaConfig
        {
            Enabled = enabled is not null
                ? bool.TryParse(enabled, out var b) ? b : defaults.Enabled
                : defaults.Enabled,
            StoragePath = storagePath ?? defaults.StoragePath,
            LogLevel = logLevel is not null
                ? Enum.TryParse<LogLevel>(logLevel, ignoreCase: true, out var ll) ? ll : defaults.LogLevel
                : defaults.LogLevel,
            ExcludeCommands = excludeCommands,
        };
    }
}
