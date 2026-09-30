using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary><c>events.subscribe</c> params (B.2).</summary>
public sealed record EventsSubscribeParams
{
    [JsonPropertyName("from_seq")]
    public long? FromSeq { get; init; }

    [JsonPropertyName("types")]
    public IReadOnlyList<string>? Types { get; init; }

    [JsonPropertyName("replay_budget")]
    public int? ReplayBudget { get; init; }

    [JsonPropertyName("live")]
    public bool? Live { get; init; }

    /// <summary>Optional named-type filter. Applies to <c>pane.agent_status_changed</c> and <c>pane.scroll_changed</c>.</summary>
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    /// <summary>Optional named-type filter. Applies only to <c>pane.agent_status_changed</c>.</summary>
    [JsonPropertyName("agent_status")]
    public string? AgentStatus { get; init; }
}

/// <summary><c>events.subscribe</c> result (B.2).</summary>
public sealed record EventsSubscribeResult
{
    [JsonPropertyName("subscription_id")]
    public string? SubscriptionId { get; init; }

    [JsonPropertyName("from_seq")]
    public long FromSeq { get; init; }

    [JsonPropertyName("next_seq")]
    public long NextSeq { get; init; }

    [JsonPropertyName("replay_complete")]
    public bool ReplayComplete { get; init; }

    [JsonPropertyName("attach_client_id")]
    public string? AttachClientId { get; init; }
}

/// <summary><c>events.unsubscribe</c> params.</summary>
public sealed record EventsUnsubscribeParams
{
    [JsonPropertyName("subscription_id")]
    public string? SubscriptionId { get; init; }
}

/// <summary><c>events.unsubscribe</c> result.</summary>
public sealed record EventsUnsubscribeResult
{
    [JsonPropertyName("subscription_id")]
    public string? SubscriptionId { get; init; }

    [JsonPropertyName("closed")]
    public bool Closed { get; init; }
}
