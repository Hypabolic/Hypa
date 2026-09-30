using Hypa.AgentRuntime.Application.Integrations;

namespace Hypa.Cli;

/// <summary>Prints the embedded agent-runtime skill for <c>hypa --skill</c>.</summary>
public static class RuntimeSkillPrinter
{
    public const string ResourceName = RuntimeSkillText.ResourceName;

    public static string Text() => RuntimeSkillText.Read();

    public static void Write(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Write(Text());
    }
}
