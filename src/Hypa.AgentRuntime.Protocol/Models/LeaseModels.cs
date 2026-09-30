using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary><c>runtime.lease.claim</c> params (B.4).</summary>
public sealed record LeaseClaimParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("takeover")]
    public bool? Takeover { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("ttl_ms")]
    public int? TtlMs { get; init; }
}

/// <summary>
/// <c>runtime.lease.claim</c> result.
/// Outcomes: granted, pending_approval, denied, already_held, invalid.
/// </summary>
public sealed record LeaseClaimResult
{
    [JsonPropertyName("outcome")]
    public string? Outcome { get; init; }

    [JsonPropertyName("lease_id")]
    public string? LeaseId { get; init; }

    [JsonPropertyName("retry_after_ms")]
    public int? RetryAfterMs { get; init; }
}

/// <summary><c>runtime.lease.release</c> params.</summary>
public sealed record LeaseReleaseParams
{
    [JsonPropertyName("lease_id")]
    public string? LeaseId { get; init; }
}

/// <summary><c>runtime.lease.release</c> result.</summary>
public sealed record LeaseReleaseResult
{
    [JsonPropertyName("lease_id")]
    public string? LeaseId { get; init; }

    [JsonPropertyName("released")]
    public bool Released { get; init; }
}

/// <summary><c>runtime.lease.renew</c> params.</summary>
public sealed record LeaseRenewParams
{
    [JsonPropertyName("lease_id")]
    public string? LeaseId { get; init; }

    [JsonPropertyName("ttl_ms")]
    public int? TtlMs { get; init; }

    /// <summary>
    /// Attach client mode for the input-lease holder. additive.
    /// Non-terminal values lock <c>popup.open</c> with <c>ui_busy</c>.
    /// </summary>
    [JsonPropertyName("client_mode")]
    public string? ClientMode { get; init; }
}

/// <summary><c>runtime.lease.renew</c> result.</summary>
public sealed record LeaseRenewResult
{
    [JsonPropertyName("lease_id")]
    public string? LeaseId { get; init; }

    [JsonPropertyName("expires_at")]
    public string? ExpiresAt { get; init; }
}
