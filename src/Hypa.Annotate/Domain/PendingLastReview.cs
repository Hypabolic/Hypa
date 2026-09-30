using System.Text.Json.Serialization;

namespace Hypa.Annotate.Domain;

/// <summary>
/// Agent message and send target waiting for a review comment.
/// </summary>
public sealed record PendingLastReview
{
    [JsonPropertyName("agentMessage")]
    public required string AgentMessage { get; init; }

    [JsonPropertyName("targetPaneId")]
    public required string TargetPaneId { get; init; }

    [JsonPropertyName("context")]
    public CaptureContext Context { get; init; } = new();

    [JsonPropertyName("capturedAt")]
    public required string CapturedAt { get; init; }
}
