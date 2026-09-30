using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

/// <summary>RGB triple on <c>client.host_theme.set</c>. Components are 0–255.</summary>
public sealed record HostThemeRgb
{
    [JsonPropertyName("r")]
    public int? R { get; init; }

    [JsonPropertyName("g")]
    public int? G { get; init; }

    [JsonPropertyName("b")]
    public int? B { get; init; }
}

/// <summary>
/// Sparse OSC 4 palette entry on <c>client.host_theme.set</c>.
/// <c>i</c> is 0–255. Components are 0–255.
/// </summary>
public sealed record HostThemePaletteEntry
{
    [JsonPropertyName("i")]
    public int? I { get; init; }

    [JsonPropertyName("r")]
    public int? R { get; init; }

    [JsonPropertyName("g")]
    public int? G { get; init; }

    [JsonPropertyName("b")]
    public int? B { get; init; }
}

/// <summary>
/// <c>client.host_theme.set</c> params. All fields optional. An empty object
// / is a no-op merge, not an error.
/// Omitted or empty <see cref="Palette"/> is a no-op merge. Last duplicate
/// index wins. <see cref="Palette"/> is bounded by
/// <see cref="MaxPaletteEntries"/> so a duplicate-heavy array cannot grow
/// without limit.
/// </summary>
public sealed record HostThemeSetParams
{
    /// <summary>
    /// Maximum <see cref="Palette"/> length. OSC 4 has 256 indices; extras
    /// exist only so last-duplicate-wins can override an earlier entry.
    /// </summary>
    public const int MaxPaletteEntries = 512;

    [JsonPropertyName("fg")]
    public HostThemeRgb? Fg { get; init; }

    [JsonPropertyName("bg")]
    public HostThemeRgb? Bg { get; init; }

    [JsonPropertyName("appearance")]
    public string? Appearance { get; init; }

    [JsonPropertyName("palette")]
    public HostThemePaletteEntry[]? Palette { get; init; }
}

/// <summary><c>client.host_theme.set</c> result. Echoes the stored theme.</summary>
public sealed record HostThemeSetResult
{
    [JsonPropertyName("fg")]
    public HostThemeRgb? Fg { get; init; }

    [JsonPropertyName("bg")]
    public HostThemeRgb? Bg { get; init; }

    [JsonPropertyName("appearance")]
    public string? Appearance { get; init; }

    [JsonPropertyName("palette")]
    public HostThemePaletteEntry[]? Palette { get; init; }
}
