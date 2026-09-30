using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

public static class ProtocolSchemaStatus
{
    public const string Implemented = "implemented";
    public const string Reserved = "reserved";
}

public sealed record ProtocolSchemaMethodEntry
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }
}

public sealed record ProtocolSchemaEventEntry
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }
}

public sealed record ProtocolSchemaRenderFieldEntry
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// When the live encoder omits the property.
    /// Null means the encoder writes the property.
    /// </summary>
    [JsonPropertyName("omit_when")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OmitWhen { get; init; }
}

public sealed record ProtocolSchemaRenderPayloadEntry
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Wire <c>kind</c> when this object is a <c>terminal.render</c> body.</summary>
    [JsonPropertyName("kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; init; }

    /// <summary>Parent object name when this object is nested.</summary>
    [JsonPropertyName("parent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Parent { get; init; }

    /// <summary>True for a legacy body a cell painter rejects.</summary>
    [JsonPropertyName("legacy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Legacy { get; init; }

    [JsonPropertyName("fields")]
    public required List<ProtocolSchemaRenderFieldEntry> Fields { get; init; }
}

public sealed record ProtocolSchemaRenderSection
{
    [JsonPropertyName("event")]
    public required string Event { get; init; }

    [JsonPropertyName("payloads")]
    public required List<ProtocolSchemaRenderPayloadEntry> Payloads { get; init; }
}

public sealed record ProtocolSchemaAttachSection
{
    [JsonPropertyName("endpoint_generation")]
    public uint EndpointGeneration { get; init; }

    [JsonPropertyName("methods")]
    public required List<string> Methods { get; init; }

    [JsonPropertyName("events")]
    public required List<string> Events { get; init; }

    [JsonPropertyName("codecs")]
    public required List<string> Codecs { get; init; }

    [JsonPropertyName("required_capabilities")]
    public required List<string> RequiredCapabilities { get; init; }

    [JsonPropertyName("optional_capabilities")]
    public required List<string> OptionalCapabilities { get; init; }

    [JsonPropertyName("records")]
    public required List<string> Records { get; init; }
}

public sealed record ProtocolSchemaDocument
{
    [JsonPropertyName("protocol_name")]
    public required string ProtocolName { get; init; }

    [JsonPropertyName("protocol_major")]
    public int ProtocolMajor { get; init; }

    [JsonPropertyName("protocol_minor")]
    public int ProtocolMinor { get; init; }

    [JsonPropertyName("endpoint_generation")]
    public uint EndpointGeneration { get; init; }

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("methods")]
    public required List<ProtocolSchemaMethodEntry> Methods { get; init; }

    [JsonPropertyName("events")]
    public required List<ProtocolSchemaEventEntry> Events { get; init; }

    [JsonPropertyName("pane_read_sources")]
    public required List<string> PaneReadSources { get; init; }

    [JsonPropertyName("attach")]
    public required ProtocolSchemaAttachSection Attach { get; init; }

    [JsonPropertyName("render")]
    public required ProtocolSchemaRenderSection Render { get; init; }
}
