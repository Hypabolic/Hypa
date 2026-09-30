using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary>Wire status tokens for <c>server.reload_config</c>. Hypa emits applied or failed.</summary>
public static class ConfigReloadStatuses
{
    public const string Applied = "applied";
    public const string Failed = "failed";

    // Hypa does not emit this status.</summary>
    public const string Partial = "partial";
}

/// <summary><c>server.reload_config</c> result. Failed is a success envelope, not <c>-32000</c>.</summary>
public sealed record ConfigReloadResult
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("diagnostics")]
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}
