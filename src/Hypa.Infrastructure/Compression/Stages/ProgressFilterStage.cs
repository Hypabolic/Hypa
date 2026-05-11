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
            // Lines that end with \r are progress-bar overwrites.
            if (line.EndsWith('\r'))
                continue;
            if (ProgressBar().IsMatch(line))
                continue;
            if (SpinnerOnly().IsMatch(line) && line.Trim().Length > 0)
                continue;
            filtered.Add(line);
        }
        return string.Join('\n', filtered);
    }
}
