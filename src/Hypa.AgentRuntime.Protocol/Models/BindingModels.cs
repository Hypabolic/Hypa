using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary><c>runtime.binding.set</c> params.</summary>
public sealed record BindingSetParams
{
    [JsonPropertyName("binding")]
    public BindingDto? Binding { get; init; }

    /// <summary>Optional pane scope. When set, updates that pane binding and requires step_id under governed modes.</summary>
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    /// <summary>Optional governed switch. When true, binding matrix requires governed fields.</summary>
    [JsonPropertyName("governed")]
    public bool? Governed { get; init; }
}

/// <summary><c>runtime.binding.set</c> / <c>runtime.binding.get</c> result.</summary>
public sealed record BindingResult
{
    [JsonPropertyName("binding")]
    public BindingDto? Binding { get; init; }
}

/// <summary><c>runtime.binding.get</c> params.</summary>
public sealed record BindingGetParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }
}
