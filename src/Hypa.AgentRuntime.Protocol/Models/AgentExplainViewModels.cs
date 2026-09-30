using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary><c>agent.explain</c> / <c>agent.focus</c> params. Pane id or unique live name.</summary>
public sealed record AgentTargetParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("agent_id")]
    public string? AgentId { get; init; }
}

/// <summary><c>agent.rename</c> params. Omit or null <c>name</c> to clear.</summary>
public sealed record AgentRenameParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("agent_id")]
    public string? AgentId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

/// <summary><c>agent.send_keys</c> params. Logical keys are validated before encode.</summary>
public sealed record AgentSendKeysParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("agent_id")]
    public string? AgentId { get; init; }

    [JsonPropertyName("keys")]
    public IReadOnlyList<string>? Keys { get; init; }
}

/// <summary><c>agent.send_keys</c> result.</summary>
public sealed record AgentSendKeysResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }
}

/// <summary>
/// <c>agent.view.set</c> params. Filter and sort stay JSON so the recursive
/// </summary>
public sealed record AgentViewSetParams
{
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("filter")]
    public JsonElement Filter { get; init; }

    [JsonPropertyName("sort")]
    public JsonElement Sort { get; init; }
}

/// <summary><c>agent.view.clear</c> params. Missing source clears any active view.</summary>
public sealed record AgentViewClearParams
{
    [JsonPropertyName("source")]
    public string? Source { get; init; }
}

/// <summary><c>agent.view.set</c> / <c>agent.view.clear</c> result.</summary>
public sealed record AgentViewResult
{
    [JsonPropertyName("active")]
    public bool Active { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }
}

/// <summary>Matched manifest rule on <c>agent.explain</c>.</summary>
public sealed record AgentExplainMatchedRule
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}

/// <summary><c>agent.explain</c> result. Screen OSC fields stay empty unless the pane already has them.</summary>
public sealed record AgentExplainResult
{
    [JsonPropertyName("agent")]
    public string? Agent { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("manifest_source")]
    public string? ManifestSource { get; init; }

    [JsonPropertyName("manifest_source_kind")]
    public string? ManifestSourceKind { get; init; }

    [JsonPropertyName("manifest_version")]
    public string? ManifestVersion { get; init; }

    [JsonPropertyName("matched_rule")]
    public AgentExplainMatchedRule? MatchedRule { get; init; }

    [JsonPropertyName("screen_detection_skipped")]
    public bool ScreenDetectionSkipped { get; init; }

    [JsonPropertyName("screen_detection_skip_reason")]
    public string? ScreenDetectionSkipReason { get; init; }

    [JsonPropertyName("skip_state_update")]
    public bool SkipStateUpdate { get; init; }

    [JsonPropertyName("skipped_update_reason")]
    public string? SkippedUpdateReason { get; init; }

    [JsonPropertyName("fallback_reason")]
    public string? FallbackReason { get; init; }

    [JsonPropertyName("warning")]
    public string? Warning { get; init; }

    [JsonPropertyName("osc_title")]
    public string? OscTitle { get; init; }

    [JsonPropertyName("osc_progress")]
    public string? OscProgress { get; init; }
}
