using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary><c>server.agent_manifests</c> result. Reports active sources.</summary>
public sealed record AgentManifestStatusResult
{
    [JsonPropertyName("last_check_unix")]
    public long? LastCheckUnix { get; init; }

    [JsonPropertyName("last_result")]
    public string? LastResult { get; init; }

    [JsonPropertyName("manifests")]
    public IReadOnlyList<AgentManifestInfo> Manifests { get; init; } = [];
}

/// <summary><c>server.reload_agent_manifests</c> result. Cache reload. Panes stay.</summary>
public sealed record AgentManifestReloadResult
{
    [JsonPropertyName("manifests")]
    public IReadOnlyList<AgentManifestInfo> Manifests { get; init; } = [];
}

/// <summary>One agent detection manifest source row.</summary>
public sealed record AgentManifestInfo
{
    [JsonPropertyName("agent")]
    public required string Agent { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("source_kind")]
    public required string SourceKind { get; init; }

    [JsonPropertyName("active_version")]
    public string? ActiveVersion { get; init; }

    [JsonPropertyName("cached_remote_version")]
    public string? CachedRemoteVersion { get; init; }

    [JsonPropertyName("local_override_shadowing_remote")]
    public bool LocalOverrideShadowingRemote { get; init; }

    [JsonPropertyName("remote_update_result")]
    public string? RemoteUpdateResult { get; init; }

    [JsonPropertyName("remote_update_error")]
    public string? RemoteUpdateError { get; init; }

    [JsonPropertyName("remote_last_checked_unix")]
    public long? RemoteLastCheckedUnix { get; init; }

    [JsonPropertyName("warning")]
    public string? Warning { get; init; }
}
