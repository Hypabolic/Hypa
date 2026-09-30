namespace Hypa.AgentRuntime.Domain.AttachConfig;

/// <summary>Built-in theme names. Validation does not paint.</summary>
public static class AttachThemeNames
{
    public static readonly IReadOnlyList<string> BuiltIn =
    [
        "catppuccin",
        "catppuccin-latte",
        "terminal",
        "tokyo-night",
        "tokyo-night-day",
        "dracula",
        "nord",
        "gruvbox",
        "gruvbox-light",
        "one-dark",
        "one-light",
        "solarized",
        "solarized-light",
        "kanagawa",
        "kanagawa-lotus",
        "rose-pine",
        "rose-pine-dawn",
        "vesper",
        "borland",
    ];

    public static bool IsAllowed(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return true;

        return IsBuiltIn(name);
    }

    public static bool IsBuiltIn(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        foreach (var builtIn in BuiltIn)
        {
            if (string.Equals(builtIn, name, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>Dark/light sibling pair for <c>auto_switch</c>. Aliases are not accepted.</summary>
    public static bool TrySiblingPair(string? name, out string dark, out string light)
    {
        var key = string.IsNullOrEmpty(name) ? "catppuccin" : name;
        switch (key)
        {
            case "catppuccin":
            case "catppuccin-latte":
                dark = "catppuccin";
                light = "catppuccin-latte";
                return true;
            case "tokyo-night":
            case "tokyo-night-day":
                dark = "tokyo-night";
                light = "tokyo-night-day";
                return true;
            case "gruvbox":
            case "gruvbox-light":
                dark = "gruvbox";
                light = "gruvbox-light";
                return true;
            case "one-dark":
            case "one-light":
                dark = "one-dark";
                light = "one-light";
                return true;
            case "solarized":
            case "solarized-light":
                dark = "solarized";
                light = "solarized-light";
                return true;
            case "kanagawa":
            case "kanagawa-lotus":
                dark = "kanagawa";
                light = "kanagawa-lotus";
                return true;
            case "rose-pine":
            case "rose-pine-dawn":
                dark = "rose-pine";
                light = "rose-pine-dawn";
                return true;
            case "terminal":
            case "dracula":
            case "nord":
            case "vesper":
            case "borland":
                dark = key;
                light = key;
                return true;
            default:
                dark = "";
                light = "";
                return false;
        }
    }
}
