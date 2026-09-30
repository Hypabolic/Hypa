using Hypa.Terminal.Pty;

namespace Hypa.Terminal.Vt.Ghostty;

/// <summary>
// / VT provider selection.
/// 0.4.0; <c>src/pane/terminal.rs:193-199</c>). Missing native fails closed.
/// Env: <c>HYPA_VT_PROVIDER</c> (ghostty | libghostty | libghostty-vt),
/// <c>HYPA_VT_REQUIRED</c> (empty | ghostty), <c>HYPA_GHOSTTY_VT</c> (absolute path).
/// <c>basic</c> and <c>f1</c> are rejected. F2 pack marker <c>hypa.channel</c>
/// still requires Ghostty.
/// </summary>
public sealed record VtProviderSelection
{
    public VtProviderKind Provider { get; init; } = VtProviderKind.Ghostty;

    /// <summary>
    /// When true, missing/unloadable Ghostty is a hard process failure.
    /// Default true: Ghostty is required.
    /// </summary>
    public bool RequireGhostty { get; init; } = true;

    /// <summary>Optional absolute path override (<c>HYPA_GHOSTTY_VT</c>).</summary>
    public string? LibraryPathOverride { get; init; }

    public static VtProviderSelection FromEnvironment()
        => FromEnvironment(NativeAssetResolver.ReadChannelMarker());

    /// <summary>
    /// Same as <see cref="FromEnvironment()"/> with an explicit channel token
    /// (tests pass <c>f2</c> without writing beside the testhost).
    /// </summary>
    public static VtProviderSelection FromEnvironment(string? channelMarker)
    {
        var providerEnv = Environment.GetEnvironmentVariable("HYPA_VT_PROVIDER");
        var requiredEnv = Environment.GetEnvironmentVariable("HYPA_VT_REQUIRED");
        var libOverride = Environment.GetEnvironmentVariable("HYPA_GHOSTTY_VT");

        var kind = ParseProvider(providerEnv);
        var requireGhostty = true;
        if (string.Equals(requiredEnv?.Trim(), "ghostty", StringComparison.OrdinalIgnoreCase))
            requireGhostty = true;
        if (string.Equals(channelMarker?.Trim(), "f2", StringComparison.OrdinalIgnoreCase))
            requireGhostty = true;

        if (string.IsNullOrWhiteSpace(libOverride))
            libOverride = null;

        return new VtProviderSelection
        {
            Provider = kind,
            RequireGhostty = requireGhostty,
            LibraryPathOverride = libOverride,
        };
    }

    public static VtProviderKind ParseProvider(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return VtProviderKind.Ghostty;

        var v = value.Trim();
        if (v.Equals("basic", StringComparison.OrdinalIgnoreCase)
            || v.Equals("f1", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "HYPA_VT_PROVIDER '" + value + "' is rejected. Ghostty is the only pane VT. Use ghostty.",
                nameof(value));
        }

        if (v.Equals("ghostty", StringComparison.OrdinalIgnoreCase)
            || v.Equals("libghostty", StringComparison.OrdinalIgnoreCase)
            || v.Equals("libghostty-vt", StringComparison.OrdinalIgnoreCase))
            return VtProviderKind.Ghostty;

        throw new ArgumentException(
            $"Unknown HYPA_VT_PROVIDER '{value}'. Use ghostty.",
            nameof(value));
    }

    /// <summary>Wire / health provider string (snake_case token).</summary>
    public string ProviderWireName => "ghostty";
}

public enum VtProviderKind
{
    /// <summary>Unused. Product construction cannot select a Basic engine.</summary>
    Basic = 0,
    Ghostty = 1,
}
