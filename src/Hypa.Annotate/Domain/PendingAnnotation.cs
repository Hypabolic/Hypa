using System.Text.Json.Serialization;

namespace Hypa.Annotate.Domain;

/// <summary>
/// Clipboard text and provenance waiting for a user comment.
/// </summary>
public sealed record PendingAnnotation
{
    [JsonPropertyName("selectedText")]
    public required string SelectedText { get; init; }

    [JsonPropertyName("context")]
    public CaptureContext Context { get; init; } = new();

    [JsonPropertyName("capturedAt")]
    public required string CapturedAt { get; init; }
}
