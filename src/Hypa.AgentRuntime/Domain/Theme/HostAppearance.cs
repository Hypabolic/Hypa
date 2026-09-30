namespace Hypa.AgentRuntime.Domain.Theme;

public enum HostAppearance
{
    Dark = 0,
    Light = 1,
}

public readonly record struct HostRgb(byte R, byte G, byte B)
{
    public const int LightLuminance = 128_000;

    public HostAppearance InferredAppearance()
    {
        var luminance = (int)R * 299 + (int)G * 587 + (int)B * 114;
        return luminance >= LightLuminance ? HostAppearance.Light : HostAppearance.Dark;
    }
}

public enum HostDefaultColorKind
{
    Foreground = 10,
    Background = 11,
}

/// <summary>
// / Value-equal 256-slot host OSC 4 palette.
/// <c>src/terminal_theme.rs:34-48</c> <c>palette: [Option&lt;RgbColor&gt;; 256]</c>.
/// Array reference equality is not used.
/// </summary>
public readonly struct HostPalette : IEquatable<HostPalette>
{
    public const int Size = 256;
    private readonly HostRgb?[]? _colors;

    private HostPalette(HostRgb?[] colors) => _colors = colors;

    public HostRgb? this[int index] =>
        (uint)index >= Size || _colors is null ? null : _colors[index];

    public HostPalette WithColor(byte index, HostRgb color)
    {
        var next = CopySlots();
        next[index] = color;
        return new HostPalette(next);
    }

    /// <summary>
    /// Incoming <c>None</c> keeps the stored index. Same null-keep as fg/bg.
    /// </summary>
    public HostPalette Merge(HostPalette incoming)
    {
        if (incoming._colors is null)
            return this;
        if (_colors is null)
            return incoming;

        var next = CopySlots();
        for (var i = 0; i < Size; i++)
        {
            if (incoming._colors[i] is { } color)
                next[i] = color;
        }

        return new HostPalette(next);
    }

    public bool Equals(HostPalette other)
    {
        if (ReferenceEquals(_colors, other._colors))
            return true;
        for (var i = 0; i < Size; i++)
        {
            if (this[i] != other[i])
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is HostPalette other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        for (var i = 0; i < Size; i++)
            hash.Add(this[i]);
        return hash.ToHashCode();
    }

    public static bool operator ==(HostPalette left, HostPalette right) => left.Equals(right);

    public static bool operator !=(HostPalette left, HostPalette right) => !left.Equals(right);

    private HostRgb?[] CopySlots()
    {
        var next = new HostRgb?[Size];
        if (_colors is not null)
            Array.Copy(_colors, next, Size);
        return next;
    }
}

/// <summary>
/// Host default colours and OSC 4 palette for mux pane VT.
/// a null channel keeps the previous value.
/// Palette-only updates still change equality so mux fans out.
/// </summary>
public readonly record struct HostTerminalTheme(
    HostRgb? Foreground,
    HostRgb? Background,
    HostAppearance? Appearance)
{
    public static HostTerminalTheme Empty { get; } = default;

    public HostPalette Palette { get; init; }

    public bool IsEmpty => Foreground is null && Background is null;

    public HostTerminalTheme WithColor(HostDefaultColorKind kind, HostRgb color) =>
        kind is HostDefaultColorKind.Foreground
            ? this with { Foreground = color }
            : this with { Background = color };

    public HostTerminalTheme WithPaletteColor(byte index, HostRgb color) =>
        this with { Palette = Palette.WithColor(index, color) };

    public HostTerminalTheme WithAppearance(HostAppearance? appearance) =>
        this with { Appearance = appearance };

    /// <summary>
    // Incoming null keeps
    /// the stored channel. Appearance and palette indices follow the same rule.
    /// </summary>
    public HostTerminalTheme Merge(HostTerminalTheme incoming) => new(
        incoming.Foreground ?? Foreground,
        incoming.Background ?? Background,
        incoming.Appearance ?? Appearance)
    {
        Palette = Palette.Merge(incoming.Palette),
    };
}
