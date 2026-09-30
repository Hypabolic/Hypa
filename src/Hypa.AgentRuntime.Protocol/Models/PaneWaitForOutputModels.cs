using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary>
/// <c>pane.wait_for_output</c> params.
/// The server reads the selected snapshot immediately, then polls.
/// Omit <c>timeout_ms</c> to wait until a match or caller cancel.
/// Timeout-only (no match fields) waits for any change.
/// </summary>
public sealed record PaneWaitForOutputParams
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("timeout_ms")]
    public int? TimeoutMs { get; init; }

    [JsonPropertyName("match")]
    public string? Match { get; init; }

    [JsonPropertyName("match_regex")]
    public string? MatchRegex { get; init; }

    /// <summary>Same values as <c>pane.read</c> <c>source</c>. Default <c>recent</c>.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("lines")]
    public int? Lines { get; init; }
}

/// <summary>Wire <c>error_code</c> for <c>pane.wait_for_output</c> timeout.</summary>
public static class PaneWaitForOutputErrors
{
    public const string Timeout = "timeout";
    public const string TimeoutMessage = "timed out waiting for output match";
}

/// <summary><c>pane.wait_for_output</c> result.</summary>
public sealed record PaneWaitForOutputResult
{
    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("changed")]
    public bool Changed { get; init; }

    [JsonPropertyName("matched")]
    public bool Matched { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("timed_out")]
    public bool TimedOut { get; init; }
}
