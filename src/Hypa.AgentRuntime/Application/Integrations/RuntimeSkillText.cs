using System.Text;

namespace Hypa.AgentRuntime.Application.Integrations;

/// <summary>
/// Embedded runtime skill. <c>hypa --skill</c> and integration install read this resource.
/// </summary>
public static class RuntimeSkillText
{
    public const string ResourceName = "Hypa.AgentRuntime.Resources.skills.hypa-runtime.SKILL.md";

    public static string Read()
    {
        using var stream = typeof(RuntimeSkillText).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("missing embedded runtime skill " + ResourceName);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Skill markdown after the YAML front matter. The fenced block uses this text.</summary>
    public static string WithoutFrontMatter(string skill)
    {
        const string fence = "---";
        if (!skill.StartsWith(fence, StringComparison.Ordinal))
            return skill;
        var lineEnd = skill.IndexOf('\n', StringComparison.Ordinal);
        if (lineEnd < 0)
            return skill;
        var close = skill.IndexOf("\n" + fence, lineEnd, StringComparison.Ordinal);
        if (close < 0)
            return skill;
        var bodyStart = close + 1 + fence.Length;
        if (bodyStart < skill.Length && skill[bodyStart] == '\r')
            bodyStart++;
        if (bodyStart < skill.Length && skill[bodyStart] == '\n')
            bodyStart++;
        return skill[bodyStart..];
    }
}
