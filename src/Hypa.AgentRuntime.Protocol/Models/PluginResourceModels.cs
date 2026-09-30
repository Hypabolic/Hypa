using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

public sealed record PluginResourceListParams
{
    [JsonPropertyName("plugin_id")]
    public string? PluginId { get; init; }
}

public sealed record PluginResourceGetParams
{
    [JsonPropertyName("resource_id")]
    public string? ResourceId { get; init; }
}

public sealed record PluginResourceRemoveParams
{
    [JsonPropertyName("resource_id")]
    public string? ResourceId { get; init; }
}

public sealed record PluginResourcePublishParams
{
    [JsonPropertyName("owner_id")]
    public string? OwnerId { get; init; }

    [JsonPropertyName("resource_id")]
    public string? ResourceId { get; init; }

    [JsonPropertyName("schema")]
    public string? Schema { get; init; }

    [JsonPropertyName("revision")]
    public long? Revision { get; init; }

    [JsonPropertyName("expires_at")]
    public string? ExpiresAt { get; init; }

    [JsonPropertyName("value")]
    public PluginCollectionValueDto? Value { get; init; }
}

public sealed record PluginResourceTargetDto
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("tab_id")]
    public string? TabId { get; init; }
}

public sealed record PluginCollectionItemDto
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("attention")]
    public long Attention { get; init; }

    [JsonPropertyName("tokens")]
    public Dictionary<string, string>? Tokens { get; init; }

    [JsonPropertyName("action")]
    public string? Action { get; init; }

    [JsonPropertyName("target")]
    public PluginResourceTargetDto? Target { get; init; }

    [JsonPropertyName("seq")]
    public long? Sequence { get; init; }

    [JsonPropertyName("ttl_ms")]
    public int? TtlMs { get; init; }
}

public sealed record PluginCollectionValueDto
{
    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("items")]
    public IReadOnlyList<PluginCollectionItemDto>? Items { get; init; }
}

public sealed record PluginResourceDto
{
    [JsonPropertyName("owner_id")]
    public string? OwnerId { get; init; }

    [JsonPropertyName("resource_id")]
    public string? ResourceId { get; init; }

    [JsonPropertyName("schema")]
    public string? Schema { get; init; }

    [JsonPropertyName("revision")]
    public long Revision { get; init; }

    [JsonPropertyName("freshness")]
    public string? Freshness { get; init; }

    [JsonPropertyName("expires_at")]
    public string? ExpiresAt { get; init; }

    [JsonPropertyName("value")]
    public PluginCollectionValueDto? Value { get; init; }
}

public sealed record PluginResourceListResult
{
    [JsonPropertyName("resources")]
    public IReadOnlyList<PluginResourceDto>? Resources { get; init; }
}

public sealed record PluginResourceGetResult
{
    [JsonPropertyName("resource")]
    public PluginResourceDto? Resource { get; init; }
}

public sealed record PluginResourcePublishResult
{
    [JsonPropertyName("ignored")]
    public bool Ignored { get; init; }

    [JsonPropertyName("resource")]
    public PluginResourceDto? Resource { get; init; }
}

public sealed record PluginResourceRemoveResult
{
    [JsonPropertyName("resource_id")]
    public string? ResourceId { get; init; }

    [JsonPropertyName("removed")]
    public bool Removed { get; init; }
}

public sealed record PluginManifestResourceDto
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("projection")]
    public string? Projection { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("platforms")]
    public IReadOnlyList<string>? Platforms { get; init; }

    [JsonPropertyName("command")]
    public IReadOnlyList<string>? Command { get; init; }
}
