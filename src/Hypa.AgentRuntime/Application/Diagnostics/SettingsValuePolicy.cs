using System.Security.Cryptography;
using System.Text;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Theme name may log in the clear. Paths, tokens, and plugin values are
/// digested or omitted.
/// </summary>
public static class SettingsValuePolicy
{
    public const string ThemeNameKey = "theme.name";

    public static bool AllowsCleartext(string key) =>
        string.Equals(key, ThemeNameKey, StringComparison.Ordinal);

    public static bool LooksSensitive(string key)
    {
        if (string.IsNullOrEmpty(key))
            return true;
        var lower = key.ToLowerInvariant();
        return lower.Contains("path", StringComparison.Ordinal)
            || lower.Contains("token", StringComparison.Ordinal)
            || lower.Contains("secret", StringComparison.Ordinal)
            || lower.Contains("password", StringComparison.Ordinal)
            || lower.Contains("plugin", StringComparison.Ordinal);
    }

    public static (string? Value, string ValueKind, string? Digest) Project(string key, string? raw)
    {
        if (AllowsCleartext(key))
            return (raw, "string", null);

        if (string.IsNullOrEmpty(raw))
            return (null, "empty", null);

        return (null, KindOf(raw), Digest(raw));
    }

    public static string Digest(string raw)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var hex = Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
        return "sha256:" + hex;
    }

    private static string KindOf(string raw)
    {
        if (raw.Contains('/') || raw.Contains('\\'))
            return "path";
        if (raw.StartsWith("\"", StringComparison.Ordinal) && raw.EndsWith("\"", StringComparison.Ordinal))
            return "string";
        if (raw is "true" or "false")
            return "bool";
        return "string";
    }
}
