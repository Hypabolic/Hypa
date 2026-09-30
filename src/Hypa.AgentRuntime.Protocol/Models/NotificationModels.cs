using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary><c>notification.show</c> params.</summary>
public sealed record NotificationShowParams
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("body")]
    public string? Body { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("sound")]
    public string? Sound { get; init; }

    [JsonPropertyName("pane_id")]
    public string? PaneId { get; init; }

    [JsonPropertyName("severity")]
    public string? Severity { get; init; }

    [JsonPropertyName("dedupe_key")]
    public string? DedupeKey { get; init; }

    [JsonPropertyName("action")]
    public string? Action { get; init; }

    [JsonPropertyName("expiry_ms")]
    public int? ExpiryMs { get; init; }

    [JsonPropertyName("position")]
    public string? Position { get; init; }

    [JsonPropertyName("colour")]
    public string? Colour { get; init; }

    [JsonPropertyName("color")]
    public string? Color { get; init; }

    [JsonPropertyName("glyph")]
    public string? Glyph { get; init; }
}

/// <summary><c>notification.show</c> result.</summary>
public sealed record NotificationShowResult
{
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }
}

/// <summary>Closed reasons for <c>notification.show</c>.</summary>
public static class NotificationShowReasons
{
    public const string Shown = "shown";
    public const string Disabled = "disabled";
    public const string RateLimited = "rate_limited";
    public const string NoForegroundClient = "no_foreground_client";
    public const string Busy = "busy";
}

/// <summary>Sound tokens for <c>notification.show</c> and status-triggered toasts.</summary>
public static class NotificationSounds
{
    public const string None = "none";
    public const string Done = "done";
    public const string Request = "request";
}

/// <summary>Core toast source. Plugin sources use <c>plugin:&lt;plugin-id&gt;</c>.</summary>
public static class NotificationSources
{
    public const string Core = "core";
}
