using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary><c>client.window_title.set</c> params.</summary>
public sealed record WindowTitleSetParams
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }
}

/// <summary><c>client.window_title.set</c> result.</summary>
public sealed record WindowTitleSetResult
{
    [JsonPropertyName("overridden")]
    public required bool Overridden { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }
}

/// <summary><c>client.window_title.clear</c> result.</summary>
public sealed record WindowTitleClearResult
{
    [JsonPropertyName("overridden")]
    public required bool Overridden { get; init; }
}
