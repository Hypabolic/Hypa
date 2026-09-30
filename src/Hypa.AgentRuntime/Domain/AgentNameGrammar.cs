namespace Hypa.AgentRuntime.Domain;

/// <summary>
// / Live agent-name grammar.
/// <c>src/app/agents.rs</c> lines 15-20: start with a lowercase letter,
/// then lowercase letters, digits, '-' or '_' (1-32 characters).
/// </summary>
public static class AgentNameGrammar
{
    public const int MaxLength = 32;

    public const string InvalidMessage =
        "agent name must start with a lowercase letter and contain only lowercase letters, digits, '-' or '_' (1-32 characters)";

    public static bool IsValid(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxLength)
            return false;
        if (name[0] is < 'a' or > 'z')
            return false;
        for (var i = 1; i < name.Length; i++)
        {
            var ch = name[i];
            if (ch is >= 'a' and <= 'z' || ch is >= '0' and <= '9' || ch is '-' or '_')
                continue;
            return false;
        }

        return true;
    }
}
