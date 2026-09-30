using System.Text;
using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
/// Portable Markdown export for saved annotations.
/// </summary>
public static class AnnotationMarkdown
{
    /// <summary>
    /// Format saved annotations as portable, agent-neutral Markdown context.
    /// </summary>
    public static string Format(IReadOnlyList<Annotation> annotations)
    {
        var sections = new List<string>(annotations.Count);
        for (var index = 0; index < annotations.Count; index++)
        {
            sections.Add(FormatSection(index + 1, annotations[index]));
        }

        return new StringBuilder()
            .Append("# Annotated context")
            .Append("\n\n")
            .Append(string.Join("\n\n", sections))
            .Append('\n')
            .ToString();
    }

    private static string FormatSection(int number, Annotation annotation)
    {
        var fence = FenceFor(annotation.SelectedText);
        var metadata = BuildMetadata(annotation.Context);
        var lines = new List<string>
        {
            $"## Annotation {number}",
        };

        if (metadata.Length > 0)
            lines.Add(metadata);

        lines.Add("Selected text:");
        lines.Add(string.Empty);
        lines.Add(fence);
        lines.Add(annotation.SelectedText);
        lines.Add(fence);
        lines.Add(string.Empty);
        lines.Add("Comment:");
        lines.Add(string.Empty);
        lines.Add(annotation.Comment);

        return CollapseBlankRuns(lines);
    }

    private static string BuildMetadata(CaptureContext context)
    {
        var lines = new List<string>();
        var sourceParts = new[]
        {
            context.WorkspaceLabel,
            context.TabLabel,
        }.Where(part => !string.IsNullOrEmpty(part));

        var source = string.Join(" / ", sourceParts);
        if (source.Length > 0)
            lines.Add($"Source: {source}");

        if (!string.IsNullOrEmpty(context.FocusedPaneAgent))
            lines.Add($"Agent: {context.FocusedPaneAgent}");

        return lines.Count == 0 ? string.Empty : string.Join('\n', lines);
    }

    /// <summary>
    /// </summary>
    internal static string FenceFor(string text)
    {
        var longest = 0;
        var current = 0;
        foreach (var character in text)
        {
            if (character == '`')
            {
                current++;
                longest = Math.Max(longest, current);
            }
            else
            {
                current = 0;
            }
        }

        return new string('`', Math.Max(longest + 1, 3));
    }

    private static string CollapseBlankRuns(IReadOnlyList<string> lines)
    {
        var filtered = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            if (line.Length == 0 && filtered.Count > 0 && filtered[^1].Length == 0)
                continue;
            filtered.Add(line);
        }

        return string.Join('\n', filtered);
    }
}
