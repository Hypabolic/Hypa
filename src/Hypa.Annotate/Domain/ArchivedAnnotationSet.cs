using System.Text.Json.Serialization;

namespace Hypa.Annotate.Domain;

/// <summary>
/// One recoverable set of annotations moved out of the active list.
/// </summary>
public sealed record ArchivedAnnotationSet
{
    [JsonPropertyName("version")]
    public byte Version { get; init; } = 1;

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("archivedAt")]
    public required string ArchivedAt { get; init; }

    [JsonPropertyName("annotations")]
    public required IReadOnlyList<Annotation> Annotations { get; init; }
}
