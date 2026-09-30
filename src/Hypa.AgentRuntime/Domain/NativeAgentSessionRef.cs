namespace Hypa.AgentRuntime.Domain;

/// <summary>
/// Native session reference from <c>pane.report_agent_session</c> or a combined
/// <c>pane.report_agent</c>. Durable. Does not change waits or <see cref="AgentStatus"/>.
/// </summary>
public sealed record NativeAgentSessionRef
{
    public const string KindId = "id";
    public const string KindPath = "path";
    public const int MaxIdLength = 512;
    public const int MaxPathLength = 4096;

    public required string Kind { get; init; }
    public required string Value { get; init; }
    public required string Source { get; init; }
    public required string Agent { get; init; }
    public string? SessionStartSource { get; init; }

    public static bool IsSessionStartSource(string? value) =>
        value is "startup" or "resume" or "clear" or "compact" or "branch" or "new" or "fork" or "select";

    /// <summary>
    /// <c>valid_session_path</c>: reject empty values and control characters.
    /// Paths must be fully qualified.
    /// </summary>
    public static bool IsValidId(string? value) =>
        IsValidSessionValue(value, MaxIdLength);

    /// <inheritdoc cref="IsValidId"/>
    public static bool IsValidPath(string? value) =>
        IsValidSessionValue(value, MaxPathLength) && Path.IsPathRooted(value);

    public static bool ContainsControl(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        foreach (var ch in value)
        {
            if (char.IsControl(ch))
                return true;
        }

        return false;
    }

    private static bool IsValidSessionValue(string? value, int maxLength) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= maxLength
        && !ContainsControl(value);
}
