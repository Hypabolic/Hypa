using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary>
/// Params of a <c>runtime.event</c> envelope (Appendix B event inventory).
/// Payload remains <see cref="JsonElement"/> so event types stay open.
/// </summary>
public sealed record RuntimeEventParams
{
    [JsonPropertyName("subscription_id")]
    public string? SubscriptionId { get; init; }

    [JsonPropertyName("seq")]
    public long Seq { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("reliability")]
    public string? Reliability { get; init; }

    /// <summary>
    /// Logical writer lane already chosen at drain time: control, ordered, or
    /// render. Additive; clients must not reparse payload JSON to classify.
    /// </summary>
    [JsonPropertyName("lane")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Lane { get; init; }

    [JsonPropertyName("occurred_at")]
    public string? OccurredAt { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; init; }
}
