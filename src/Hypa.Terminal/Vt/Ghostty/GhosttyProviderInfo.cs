namespace Hypa.Terminal.Vt.Ghostty;

/// <summary>
/// Identity of a loaded libghostty-vt.
/// No native handles on this type.
/// </summary>
public sealed record GhosttyProviderInfo
{
    /// <summary>Full version string from <c>ghostty_build_info(VERSION_STRING)</c>.</summary>
    public required string Version { get; init; }

    /// <summary>Build metadata (often commit); may be empty.</summary>
    public string? Build { get; init; }

    /// <summary>ABI surface id from Hypa abi-manifest (not upstream semver alone).</summary>
    public string Abi { get; init; } = "1";

    /// <summary>Pinned upstream commit from <c>native/ghostty/PIN.md</c> when known.</summary>
    public string? UpstreamCommit { get; init; }

    /// <summary>Resolved library path that was loaded.</summary>
    public string? LibraryPath { get; init; }

    /// <summary>Link mode recorded for the spike (dynamic preferred).</summary>
    public string LinkMode { get; init; } = "dynamic";
}
