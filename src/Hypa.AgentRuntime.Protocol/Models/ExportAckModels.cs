using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary><c>events.export.ack</c> params. Atomic consumer surface only — no Run/Step mutation.</summary>
public sealed record ExportAckParams
{
    [JsonPropertyName("runtime_session_id")]
    public string? RuntimeSessionId { get; init; }

    [JsonPropertyName("export_id")]
    public string? ExportId { get; init; }

    [JsonPropertyName("last_seq")]
    public long? LastSeq { get; init; }

    /// <summary>Consumer label (default <c>atomic</c>).</summary>
    [JsonPropertyName("consumer")]
    public string? Consumer { get; init; }
}

/// <summary><c>events.export.ack</c> result. Runtime fields only.</summary>
public sealed record ExportAckResult
{
    [JsonPropertyName("export_id")]
    public required string ExportId { get; init; }

    [JsonPropertyName("last_acked_seq")]
    public long LastAckedSeq { get; init; }

    [JsonPropertyName("acked")]
    public bool Acked { get; init; }
}
