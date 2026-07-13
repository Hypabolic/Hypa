using System.Text.RegularExpressions;
using Hypa.Runtime.Application.Ports;

namespace Hypa.Infrastructure.Compression.Stages;

public sealed partial class ProgressFilterStage : ICompressionStage
{
    public string Id => "filter-progress";

    [GeneratedRegex(@"^[^\S\r\n]*[\[=\->\] %#|.]+[0-9]*[%]?[^\S\r\n]*$")]
    private static partial Regex ProgressBar();

    [GeneratedRegex(@"^[⠀-⣿⠏⠋⠙⠹⠸⠼⠴⠦⠧⠇\s]*$")]
    private static partial Regex SpinnerOnly();

    public string Apply(string text)
    {
        var lines = text.Split('\n');
        var filtered = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            // Lines that end with \r are progress-bar overwrites only if the
            // content underneath (with trailing \r stripped) looks like one;
            // otherwise this is ordinary CRLF-terminated text and must be kept
            // verbatim, trailing \r included.
            if (line.EndsWith('\r'))
            {
                var content = line.TrimEnd('\r');
                if (IsProgressBarLine(content) ||
                    (SpinnerOnly().IsMatch(content) && content.Trim().Length > 0))
                    continue;
                filtered.Add(line);
                continue;
            }
            if (IsProgressBarLine(line))
                continue;
            if (SpinnerOnly().IsMatch(line) && line.Trim().Length > 0)
                continue;
            filtered.Add(line);
        }
        return string.Join('\n', filtered);
    }

    private static bool IsProgressBarLine(string line)
    {
        if (!ProgressBar().IsMatch(line))
            return false;

        var trimmed = line.Trim();
        if (trimmed.Length < 5)
            return false;

        return trimmed.Contains('%', StringComparison.Ordinal) ||
               trimmed.Contains('=', StringComparison.Ordinal) ||
               trimmed.Contains('>', StringComparison.Ordinal) ||
               trimmed.Contains('#', StringComparison.Ordinal) ||
               trimmed.Contains('|', StringComparison.Ordinal);
    }
}
