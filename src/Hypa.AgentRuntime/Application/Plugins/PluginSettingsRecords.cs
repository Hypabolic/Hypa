namespace Hypa.AgentRuntime.Application.Plugins;

public static class PluginSettingsFieldTypes
{
    public const string String = "string";
    public const string Integer = "integer";
    public const string Boolean = "boolean";
    public const string Choice = "choice";

    public static bool IsKnown(string? type) =>
        type is String or Integer or Boolean or Choice;
}

public sealed record PluginManifestSettingsField
{
    public required string Key { get; init; }

    public required string Type { get; init; }

    public required string Title { get; init; }

    public required string Default { get; init; }

    public IReadOnlyList<string> Choices { get; init; } = [];
}

public sealed record PluginSettingsSnapshot
{
    public required string PluginId { get; init; }

    public required IReadOnlyDictionary<string, string> Values { get; init; }
}

public sealed record PluginSettingsWriteOutcome
{
    public required bool Applied { get; init; }

    public required IReadOnlyDictionary<string, string> Values { get; init; }
}
