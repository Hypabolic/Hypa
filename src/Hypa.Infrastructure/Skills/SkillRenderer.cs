using System.Reflection;
using System.Text;
using Hypa.Runtime.Application.Ports;

namespace Hypa.Infrastructure.Skills;

public sealed class SkillRenderer : ISkillRenderer
{
    private static readonly Assembly Assembly = typeof(SkillRenderer).Assembly;
    private const string SkillResourceName = "Hypa.Infrastructure.Skills.Resources.SKILL.md";
    private const string RulesResourceName = "Hypa.Infrastructure.Skills.Resources.HYPA.md";

    public string Render(bool fullSections)
    {
        var content = ReadResource(SkillResourceName);
        return fullSections ? content : TrimToSections(content, 2);
    }

    public string GetRulesContent() => ReadResource(RulesResourceName);

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource not found: {name}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string TrimToSections(string content, int maxSection)
    {
        var lines = content.Split('\n');
        var result = new List<string>();
        var currentSection = 0;

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("<!-- section:", StringComparison.Ordinal))
            {
                var end = line.IndexOf("-->", StringComparison.Ordinal);
                if (end > 0)
                {
                    var sectionStr = line[13..end].Trim();
                    if (int.TryParse(sectionStr, out var sectionNum))
                        currentSection = sectionNum;
                }

                if (currentSection > maxSection)
                    break;

                continue;
            }

            if (currentSection <= maxSection)
                result.Add(line);
        }

        return string.Join('\n', result).TrimEnd();
    }
}
