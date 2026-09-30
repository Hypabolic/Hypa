using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.AgentRuntime.Domain.Theme;

/// <summary>
/// Mutable attach theme. can set <see cref="SetName"/> and clear auto-switch.
/// </summary>
public sealed class ThemeRuntime
{
    private ThemeRuntime(
        AttachThemeConfig theme,
        string? uiAccent,
        string name,
        ThemePalette palette)
    {
        Theme = theme;
        UiAccent = uiAccent;
        Name = name;
        Palette = palette;
    }

    public AttachThemeConfig Theme { get; private set; }

    public string? UiAccent { get; }

    public HostAppearance? Appearance { get; private set; }

    public bool AppearanceExplicit { get; private set; }

    public string Name { get; private set; }

    public ThemePalette Palette { get; private set; }

    public bool AutoSwitch => Theme.AutoSwitch;

    public static ThemeRuntime Default => FromConfig(AttachThemeConfig.Default, uiAccent: null).Value;

    public static AttachConfigResult<ThemeRuntime> FromConfig(AttachThemeConfig theme, string? uiAccent = null)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var resolved = ThemeResolver.Resolve(theme, appearance: null, uiAccent);
        if (!resolved.IsOk)
            return AttachConfigResult<ThemeRuntime>.Fail(resolved.Errors);

        return AttachConfigResult<ThemeRuntime>.Ok(
            new ThemeRuntime(theme, uiAccent, resolved.Value.Name, resolved.Value.Palette));
    }

    public bool SetName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var next = Theme with { Name = name, AutoSwitch = false };
        var resolved = ThemeResolver.Resolve(next, Appearance, UiAccent);
        if (!resolved.IsOk)
            return false;

        var changed = !Theme.Equals(next)
            || Name != resolved.Value.Name
            || !Palette.Equals(resolved.Value.Palette);
        Theme = next;
        Name = resolved.Value.Name;
        Palette = resolved.Value.Palette;
        return changed;
    }

    public bool SetAppearance(HostAppearance appearance, bool explicitReport)
    {
        if (Appearance == appearance && AppearanceExplicit == explicitReport)
            return false;
        if (AppearanceExplicit && !explicitReport)
            return false;

        Appearance = appearance;
        AppearanceExplicit = explicitReport;
        return Refresh();
    }

    public bool ApplyOscBackground(HostRgb color)
    {
        if (AppearanceExplicit)
            return false;
        return SetAppearance(color.InferredAppearance(), explicitReport: false);
    }

    public bool Refresh()
    {
        var resolved = ThemeResolver.Resolve(Theme, Appearance, UiAccent);
        if (!resolved.IsOk)
            return false;
        if (Name == resolved.Value.Name && Palette.Equals(resolved.Value.Palette))
            return false;
        Name = resolved.Value.Name;
        Palette = resolved.Value.Palette;
        return true;
    }

    public ThemeSnapshot Capture() =>
        new(Theme, Appearance, AppearanceExplicit, Name, Palette);

    public bool Restore(ThemeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot.Palette);
        ArgumentNullException.ThrowIfNull(snapshot.Theme);
        ArgumentNullException.ThrowIfNull(snapshot.Name);
        var changed = !Theme.Equals(snapshot.Theme)
            || Appearance != snapshot.Appearance
            || AppearanceExplicit != snapshot.AppearanceExplicit
            || Name != snapshot.Name
            || !Palette.Equals(snapshot.Palette);
        Theme = snapshot.Theme;
        Appearance = snapshot.Appearance;
        AppearanceExplicit = snapshot.AppearanceExplicit;
        Name = snapshot.Name;
        Palette = snapshot.Palette;
        return changed;
    }
}

/// <summary>In-place restore for settings preview cancel. Painters keep this runtime object.</summary>
public sealed record ThemeSnapshot(
    AttachThemeConfig Theme,
    HostAppearance? Appearance,
    bool AppearanceExplicit,
    string Name,
    ThemePalette Palette);
