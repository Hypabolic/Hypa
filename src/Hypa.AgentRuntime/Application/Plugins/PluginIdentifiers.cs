namespace Hypa.AgentRuntime.Application.Plugins;

public static class PluginIdentifiers
{
    public const string SourcePrefix = "plugin:";
    public const string OfficialPrefix = "hypa:";

    public static string? NormalizePluginId(string? value) =>
        Normalize(value, allowDot: true);

    public static string? NormalizeLocalId(string? value) =>
        Normalize(value, allowDot: false);

    public static bool TryPluginSource(string? source, out string pluginId)
    {
        pluginId = "";
        if (string.IsNullOrWhiteSpace(source)
            || !source.StartsWith(SourcePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = source[SourcePrefix.Length..];
        var normalized = NormalizePluginId(rest);
        if (normalized is null)
            return false;
        pluginId = normalized;
        return true;
    }

    public static bool IsOfficialSource(string? source) =>
        source is not null && source.StartsWith(OfficialPrefix, StringComparison.Ordinal);

    public static string SourceOf(string pluginId) => SourcePrefix + pluginId;

    public static bool CommandLooksLikePowershell(string program)
    {
        var name = program.Trim();
        if (name.Length == 0)
            return false;
        var slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        var file = slash >= 0 ? name[(slash + 1)..] : name;
        return file.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAllowedUnixCommand(string program)
    {
        if (CommandLooksLikePowershell(program))
            return false;
        return true;
    }

    public static IReadOnlyList<string>? EffectivePlatforms(
        IReadOnlyList<string>? item,
        IReadOnlyList<string>? plugin) =>
        item is not null ? item : plugin;

    public static string CurrentPlatform()
    {
        if (OperatingSystem.IsLinux())
            return "linux";
        if (OperatingSystem.IsMacOS())
            return "macos";
        return "windows";
    }

    public static PluginResult<bool> EnsurePlatformSupported(
        IReadOnlyList<string>? platforms,
        string subject)
    {
        if (platforms is null)
            return PluginResult<bool>.Ok(true);
        var host = CurrentPlatform();
        foreach (var p in platforms)
        {
            if (string.Equals(p, host, StringComparison.Ordinal))
                return PluginResult<bool>.Ok(true);
        }

        return PluginResult<bool>.Fail(
            PluginError.PlatformUnsupported,
            $"{subject} does not support the current platform ({host})");
    }

    public static bool TryParseVersion(string value, out Version version)
    {
        version = new Version(0, 0, 0);
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return false;
        var plus = trimmed.IndexOf('+');
        if (plus >= 0)
            trimmed = trimmed[..plus];
        var dash = trimmed.IndexOf('-');
        if (dash >= 0)
            trimmed = trimmed[..dash];
        var parts = trimmed.Split('.');
        if (parts.Length is < 2 or > 4)
            return false;
        var nums = new int[Math.Max(3, parts.Length)];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var n) || n < 0)
                return false;
            nums[i] = n;
        }

        version = new Version(nums[0], nums[1], parts.Length > 2 ? nums[2] : 0);
        return true;
    }

    private static string? Normalize(string? value, bool allowDot)
    {
        if (value is null)
            return null;
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > PluginCommandLimits.IdentifierMaxChars)
            return null;
        foreach (var ch in trimmed)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is ':' or '_' or '-')
                continue;
            if (allowDot && ch == '.')
                continue;
            return null;
        }

        return trimmed;
    }
}
