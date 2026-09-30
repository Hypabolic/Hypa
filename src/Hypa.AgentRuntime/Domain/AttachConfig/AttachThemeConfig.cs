namespace Hypa.AgentRuntime.Domain.AttachConfig;

public sealed record AttachThemeConfig
{
    public string Name { get; init; } = "catppuccin";

    public bool AutoSwitch { get; init; }

    public string? DarkName { get; init; }

    public string? LightName { get; init; }

    public IReadOnlyDictionary<string, string> Custom { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public static AttachThemeConfig Default { get; } = new();
}
