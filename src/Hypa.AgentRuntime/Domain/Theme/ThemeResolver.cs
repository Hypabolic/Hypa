using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.AgentRuntime.Domain.Theme;

public sealed record ThemeResolveResult(string Name, ThemePalette Palette);

/// <summary>Pure name + palette resolve. No I/O. No silent catppuccin fallback.</summary>
public static class ThemeResolver
{
    /// <summary>
    /// Default when <c>theme.auto_switch</c> is true and the host has not reported appearance.
    /// Default config and the config reference document this value.
    /// </summary>
    public const HostAppearance MissingAppearanceDefault = HostAppearance.Dark;

    public static AttachConfigResult<ThemeResolveResult> Resolve(
        AttachThemeConfig theme,
        HostAppearance? appearance = null,
        string? uiAccent = null)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var resolvedName = ResolveName(theme, appearance);
        if (!resolvedName.IsOk)
            return AttachConfigResult<ThemeResolveResult>.Fail(resolvedName.Errors);

        var name = resolvedName.Value;
        var loaded = ThemePalette.FromName(name);
        if (!loaded.IsOk)
            return AttachConfigResult<ThemeResolveResult>.Fail(loaded.Errors);

        var overridden = loaded.Value.WithOverrides(theme.Custom);
        if (!overridden.IsOk)
            return AttachConfigResult<ThemeResolveResult>.Fail(overridden.Errors);

        var palette = overridden.Value;
        var customAccent = theme.Custom.ContainsKey("accent");
        if (!customAccent
            && !string.IsNullOrWhiteSpace(uiAccent)
            && !string.Equals(uiAccent, "cyan", StringComparison.Ordinal))
        {
            if (!ThemeColorParser.TryParse(uiAccent, out var legacy))
            {
                return AttachConfigResult<ThemeResolveResult>.Fail(AttachConfigError.Value(
                    "ui.accent",
                    $"ui.accent '{uiAccent}' is not a valid color.",
                    line: null));
            }

            palette = palette with { Accent = legacy };
        }

        return AttachConfigResult<ThemeResolveResult>.Ok(new ThemeResolveResult(name, palette));
    }

    public static AttachConfigResult<string> ResolveName(
        AttachThemeConfig theme,
        HostAppearance? appearance = null)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (!theme.AutoSwitch)
        {
            var manual = string.IsNullOrEmpty(theme.Name) ? "catppuccin" : theme.Name;
            if (!AttachThemeNames.IsBuiltIn(manual))
            {
                return AttachConfigResult<string>.Fail(AttachConfigError.Value(
                    "theme.name",
                    $"theme.name '{theme.Name}' is not a built-in theme.",
                    line: null));
            }

            return AttachConfigResult<string>.Ok(manual);
        }

        var baseName = string.IsNullOrEmpty(theme.Name) ? "catppuccin" : theme.Name;
        if (!AttachThemeNames.TrySiblingPair(baseName, out var siblingDark, out var siblingLight))
        {
            return AttachConfigResult<string>.Fail(AttachConfigError.Value(
                "theme.name",
                $"theme.name '{theme.Name}' is not a built-in theme.",
                line: null));
        }

        var dark = string.IsNullOrEmpty(theme.DarkName) ? siblingDark : theme.DarkName;
        var light = string.IsNullOrEmpty(theme.LightName) ? siblingLight : theme.LightName;
        var effectiveAppearance = appearance ?? MissingAppearanceDefault;
        var chosen = effectiveAppearance is HostAppearance.Light ? light : dark;
        var key = effectiveAppearance is HostAppearance.Light
            ? "theme.light_name"
            : "theme.dark_name";
        if (!AttachThemeNames.IsBuiltIn(chosen))
        {
            return AttachConfigResult<string>.Fail(AttachConfigError.Value(
                key,
                $"{key} '{chosen}' is not a built-in theme.",
                line: null));
        }

        return AttachConfigResult<string>.Ok(chosen);
    }
}
