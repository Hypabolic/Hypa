using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary>Typed source checkpoint metadata on <c>agent.read</c>.</summary>
public sealed record SourceCheckpointDto
{
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("journal_next_seq")]
    public long JournalNextSeq { get; init; }

    [JsonPropertyName("occupant_generation")]
    public int OccupantGeneration { get; init; }

    [JsonPropertyName("runtime_session_id")]
    public string? RuntimeSessionId { get; init; }

    [JsonPropertyName("binding")]
    public BindingDto? Binding { get; init; }
}

/// <summary>Presentation metadata on <c>agent.read</c>.</summary>
public sealed record PresentationMetaDto
{
    [JsonPropertyName("compressor")]
    public string? Compressor { get; init; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    [JsonPropertyName("max_lines")]
    public int MaxLines { get; init; }

    [JsonPropertyName("max_bytes")]
    public int MaxBytes { get; init; }
}

/// <summary>Typed <c>agent.read</c> result for fixture/unit tests.</summary>
public sealed record AgentReadResult
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("raw")]
    public string? Raw { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("agent_status")]
    public string? AgentStatus { get; init; }

    [JsonPropertyName("occupant_generation")]
    public int OccupantGeneration { get; init; }

    [JsonPropertyName("runtime_session_id")]
    public string? RuntimeSessionId { get; init; }

    [JsonPropertyName("source_checkpoint")]
    public SourceCheckpointDto? SourceCheckpoint { get; init; }

    [JsonPropertyName("presentation")]
    public PresentationMetaDto? Presentation { get; init; }
}
