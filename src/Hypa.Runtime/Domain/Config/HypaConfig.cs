using Microsoft.Extensions.Logging;

namespace Hypa.Runtime.Domain.Config;

public sealed record HypaConfig
{
    public bool Enabled { get; init; } = true;

    public string StoragePath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hypa");

    public string[] ExcludeCommands { get; init; } = [];

    public bool GenericWrapperEnabled { get; init; } = true;

    public LogLevel LogLevel { get; init; } = LogLevel.Warning;

    public static HypaConfig Default { get; } = new();
}
