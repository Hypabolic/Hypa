using System.Text.Json.Serialization;

namespace Hypa.Annotate.Domain;

/// <summary>
/// A saved annotation with a non-empty user comment.
/// </summary>
public sealed record Annotation
{
    [JsonPropertyName("selectedText")]
    public required string SelectedText { get; init; }

    [JsonPropertyName("capturedAt")]
    public required string CapturedAt { get; init; }

    [JsonPropertyName("context")]
    public CaptureContext Context { get; init; } = new();

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("comment")]
    public required string Comment { get; init; }

    [JsonPropertyName("createdAt")]
    public required string CreatedAt { get; init; }
}
