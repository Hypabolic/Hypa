using System.Text.RegularExpressions;

namespace Hypa.AgentRuntime.Domain;

public readonly partial record struct SessionId(string Value)
{
    /// <summary>
    /// Safe session identifier for use in filesystem paths.
    /// Allowed: 1–64 chars of [A-Za-z0-9._-]. Rejects empty, path separators, and "..".
    /// </summary>
    public static SessionId New(string? name = null)
    {
        var value = string.IsNullOrWhiteSpace(name) ? "default" : name.Trim();
        if (!IsValidName(value))
        {
            throw new ArgumentException(
                "Session name must be 1–64 characters of [A-Za-z0-9._-] only (no path separators).",
                nameof(name));
        }

        return new SessionId(value);
    }

    public static bool IsValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64)
            return false;
        if (name.Contains("..", StringComparison.Ordinal))
            return false;
        if (name.Contains('/') || name.Contains('\\') || name.Contains(':'))
            return false;
        return SessionNameRegex().IsMatch(name);
    }

    public override string ToString() => Value;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionNameRegex();
}

public readonly record struct WorkspaceId(string Value)
{
    public static WorkspaceId New() => new(Guid.NewGuid().ToString("N")[..12]);
    public override string ToString() => Value;
}

public readonly record struct TabId(string Value)
{
    public static TabId New() => new(Guid.NewGuid().ToString("N")[..12]);
    public override string ToString() => Value;
}

public readonly record struct PaneId(string Value)
{
    public static PaneId New() => new(Guid.NewGuid().ToString("N")[..12]);
    public override string ToString() => Value;
}
