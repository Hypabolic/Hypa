using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Application.Plugins;

/// <summary>
/// Last non-empty stdout line from a plugin <c>[[doctor]]</c> command.
/// </summary>
public sealed record PluginDoctorStdoutDto
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    [JsonPropertyName("hint")]
    public string? Hint { get; init; }
}
