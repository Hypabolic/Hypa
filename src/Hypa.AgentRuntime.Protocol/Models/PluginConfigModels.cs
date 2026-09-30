using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

public sealed record PluginManifestSettingsFieldDto
{
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("default")]
    public string? Default { get; init; }

    [JsonPropertyName("choices")]
    public IReadOnlyList<string>? Choices { get; init; }
}

public sealed record PluginConfigGetParams
{
    [JsonPropertyName("plugin_id")]
    public string? PluginId { get; init; }
}

public sealed record PluginConfigGetResult
{
    [JsonPropertyName("plugin_id")]
    public string? PluginId { get; init; }

    [JsonPropertyName("values")]
    public IReadOnlyDictionary<string, string>? Values { get; init; }
}

public sealed record PluginConfigSetParams
{
    [JsonPropertyName("plugin_id")]
    public string? PluginId { get; init; }

    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }
}

public sealed record PluginConfigSetResult
{
    [JsonPropertyName("plugin_id")]
    public string? PluginId { get; init; }

    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("applied")]
    public bool Applied { get; init; }

    [JsonPropertyName("values")]
    public IReadOnlyDictionary<string, string>? Values { get; init; }
}

public sealed record ConfigChangedPayload
{
    [JsonPropertyName("plugin_id")]
    public string? PluginId { get; init; }

    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }
}
